package com.abletonmulti.bitwig;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.time.LocalTime;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import com.bitwig.extension.api.project.Project;
import com.bitwig.extension.controller.ControllerExtension;
import com.bitwig.extension.controller.api.ControllerHost;

/**
 * Bitwig の中で動き、同じ PC の AbletonMulti アプリと 127.0.0.1:47400 で通信する。
 * やりとりは Live の Remote Script（multi.py）とまったく同じなので、アプリからは Live と同じに見える。
 *
 * スレッド:
 *   - Bitwig の「コントロールサーフェス」スレッド: 50ms ごとに pump() を呼び、main スレッドへ tick を投げる
 *   - Bitwig の main スレッド: プロジェクトの読み書きはすべてここ（mainTick）
 *   - 通信用のスレッド 2 本（受信・送信）
 */
public class BridgeExtension extends ControllerExtension {
    private static final String APP_HOST = "127.0.0.1";
    private static final int APP_PORT = 47400;
    private static final int PROTOCOL = 2;
    private static final long FULL_CHECK_MS = 1000;
    private static final long MIN_CHECK_MS = 100;
    private static final File LOG_DIR = new File(System.getProperty("java.io.tmpdir"), "abletonmulti-bitwig");

    private static final Object CONNECTED = new Object();
    private static final Object DISCONNECTED = new Object();

    private final ControllerHost mHost;
    private Object mProjectProxy;
    private volatile boolean mRunning;
    private final AtomicBoolean mBusy = new AtomicBoolean();
    private volatile String mPopup;

    // 通信
    private final ConcurrentLinkedQueue<Object> mInbox = new ConcurrentLinkedQueue<>();
    private final LinkedBlockingQueue<String> mOutbox = new LinkedBlockingQueue<>();
    private volatile Socket mSocket;
    private volatile OutputStream mOut;

    // main スレッドだけで使う
    private BitwigModel mModel;
    private Internals mInternals;
    private Project mApi;
    private final List<Object[]> mListeners = new ArrayList<>();
    // key -> 送ったがまだ戻ってきていない変更それぞれの「変えた項目」（"*" は全部）
    private final Map<String, List<java.util.Set<String>>> mPending = new HashMap<>();
    private boolean mConnected;
    private volatile boolean mDirty;
    private long mLastFullCheck;
    private boolean mBroken;

    protected BridgeExtension(BridgeDefinition definition, ControllerHost host) {
        super(definition, host);
        mHost = host;
    }

    @Override
    public void init() {
        LOG_DIR.mkdirs();
        log("AbletonMulti for Bitwig " + BridgeDefinition.VERSION + " loaded");
        mProjectProxy = mHost.getProject();
        mRunning = true;
        Thread reader = new Thread(this::networkLoop, "AbletonMulti network");
        reader.setDaemon(true);
        reader.start();
        Thread writer = new Thread(this::writeLoop, "AbletonMulti writer");
        writer.setDaemon(true);
        writer.start();
        mHost.scheduleTask(this::pump, 200);
    }

    @Override
    public void exit() {
        mRunning = false;
        closeSocket();
        try {
            Internals.exec(mProjectProxy, this::detach);
        } catch (Exception ignored) {
        }
    }

    @Override
    public void flush() { }

    // ------------------------------------------------------------ ticking

    /** コントロールサーフェスのスレッド。main スレッドに tick を投げる。 */
    private void pump() {
        if (!mRunning) return;
        String popup = mPopup;
        if (popup != null) {
            mPopup = null;
            mHost.showPopupNotification(popup);
        }
        if (mBusy.compareAndSet(false, true)) {
            try {
                Internals.exec(mProjectProxy, () -> {
                    try {
                        mainTick();
                    } catch (Throwable t) {
                        log("error: " + stack(t));
                    } finally {
                        mBusy.set(false);
                    }
                });
            } catch (Throwable t) {
                mBusy.set(false);
                log("exec failed: " + stack(t));
            }
        }
        mHost.scheduleTask(this::pump, 50);
    }

    private void mainTick() throws Exception {
        if (mBroken) return;
        Object dig = Internals.projectTarget(mProjectProxy);
        if (dig == null) return;
        if (mModel == null || mModel.dig != dig) attach(dig);
        if (mModel == null) return;

        debugCommands();
        // 届いた変更を反映する前に、自分の変更を先に送る（ユーザーの操作が上書きされて消えないように）
        flushLocal(false);
        if (mConnected && mModel.hasDeferred()) {
            // 前の tick で後回しにしたパラメータの変更（BitwigModel の PARAMETERS_FIRST を参照）
            mInternals.begin(mModel.dig, "AbletonMulti");
            try {
                mModel.applyDeferred(mPending);
            } finally {
                mInternals.end(mModel.dig);
            }
            return;
        }
        Object item;
        while ((item = mInbox.poll()) != null) {
            if (item == CONNECTED) onConnected();
            else if (item == DISCONNECTED) onDisconnected();
            else if (mConnected && handle(Json.map(item))) break;  // 中身を変えたら、続きはエンジンに伝わった後（次の tick）
        }
    }

    /** プロジェクトが開かれた・切り替わった。 */
    private void attach(Object dig) throws Exception {
        detach();
        try {
            mInternals = new Internals(dig);
            ClassLoader cl = dig.getClass().getClassLoader();
            Class<?> digClass = cl.loadClass("com.bitwig.flt.document.core.master.Dig");
            mApi = (Project) cl.loadClass("com.bitwig.flt.document.api.document.ApiProject").getConstructor(digClass).newInstance(dig);
            mModel = new BitwigModel(dig, mApi, mInternals, new BitwigModel.Output() {
                @Override public void log(String message) { BridgeExtension.this.log(message); }
                @Override public void warn(String message) { BridgeExtension.this.warn(message); }
            });
        } catch (Throwable t) {
            // この Bitwig では内部の API が見つからない
            mBroken = true;
            log("this Bitwig is not supported: " + stack(t));
            mPopup = "AbletonMulti: この Bitwig のバージョンには対応していません";
            return;
        }
        Runnable mark = () -> mDirty = true;
        listen(dig, mark);
        listen(Internals.target(mApi.getTrackGroup()), mark);
        listen(Internals.target(mApi.getTransport()), mark);
        mDirty = true;
        mPending.clear();
        log("attached to project " + dig);
        if (mConnected) sendHello();  // 別のプロジェクトになったことをアプリに知らせる
    }

    private void listen(Object internal, Runnable onChange) {
        try {
            mListeners.add(new Object[] { internal, mInternals.listen(internal, onChange) });
        } catch (Exception e) {
            log("listen failed on " + internal + ": " + e);
        }
    }

    private void detach() {
        for (Object[] l : mListeners) mInternals.unlisten(l[0], l[1]);
        mListeners.clear();
        if (mApi != null) {
            try {
                mApi.getClass().getMethod("dispose").invoke(mApi);
            } catch (Exception ignored) {
            }
        }
        mModel = null;
        mApi = null;
    }

    private void flushLocal(boolean force) {
        if (!mConnected || mModel == null) return;
        long now = System.currentTimeMillis();
        if (!force && !mDirty && now - mLastFullCheck < FULL_CHECK_MS) return;
        // ドラッグ中などは変更の通知が続けて来るので、読み直しは 100ms に 1 回まで（重いプロジェクトで操作が重くならないように）
        if (!force && now - mLastFullCheck < MIN_CHECK_MS) return;
        mDirty = false;
        mLastFullCheck = now;
        sendOps(mModel.collectChanges());
        debugDump();
    }

    /** 調査用: LOG_DIR に cmd.txt を置くと、1 行ずつ実行して消す（開発中の実験用）。 */
    private void debugCommands() {
        File file = new File(LOG_DIR, "cmd.txt");
        if (!file.exists() || !new File(LOG_DIR, "debug").exists()) return;
        List<String> lines;
        try {
            lines = java.nio.file.Files.readAllLines(file.toPath(), StandardCharsets.UTF_8);
            file.delete();
        } catch (IOException e) {
            return;
        }
        for (String line : lines) {
            if (line.isBlank()) continue;
            log("cmd: " + line);
            try {
                String[] a = line.trim().split(" +");
                var root = mApi.getTrackGroup();
                switch (a[0]) {
                    case "tx-begin" -> mInternals.begin(mModel.dig, "AbletonMulti test");
                    case "tx-end" -> mInternals.end(mModel.dig);
                    case "delete-track" -> mInternals.delete(mModel.dig, List.of(Internals.target(root.mainTracks().get(Integer.parseInt(a[1])))));
                    case "delete-effect" -> mInternals.delete(mModel.dig, List.of(Internals.target(root.effectTracks().get(Integer.parseInt(a[1])))));
                    case "delete-all" -> {
                        List<Object> all = new ArrayList<>();
                        for (Object o : root.mainTracks()) all.add(Internals.target(o));
                        mInternals.delete(mModel.dig, all);
                    }
                    case "insert-inst" -> {
                        var f = (com.bitwig.extension.api.project.Factory) mApi.getClass().getMethod("getFactory").invoke(mApi);
                        mInternals.insertTrack(Internals.target(root), Internals.target(f.createInstrumentTrack()), Integer.parseInt(a[1]));
                    }
                    case "blank" -> mModel.makeBlank();
                    case "clip-dur", "clip-move", "clip-note" -> {
                        // Bitwig 側の編集（テスト用）: clip-dur トラック名 開始 長さ / clip-move トラック名 開始 新しい開始
                        // / clip-note トラック名 開始 音程 位置 長さ
                        var track = findTrack(root, a[1]);
                        com.bitwig.extension.api.project.timeline.Clip clip = null;
                        for (Object e : track.clipTimeline().getEvents())
                            if (e instanceof com.bitwig.extension.api.project.timeline.Clip c && Math.abs(c.getTime() - Double.parseDouble(a[2])) < 1e-6) clip = c;
                        if (clip == null) throw new IllegalArgumentException("no clip at " + a[2]);
                        mInternals.begin(mModel.dig, "AbletonMulti test");
                        try {
                            switch (a[0]) {
                                case "clip-dur" -> clip.setDuration(Double.parseDouble(a[3]));
                                case "clip-move" -> clip.setTime(Double.parseDouble(a[3]));
                                default -> {
                                    var f = (com.bitwig.extension.api.project.Factory) mApi.getClass().getMethod("getFactory").invoke(mApi);
                                    clip.getContent().getEventTimeline().addEvent(f.createInstrumentNote(0, Integer.parseInt(a[3]),
                                            Double.parseDouble(a[4]), Double.parseDouble(a[5]), 0.8, 0.5));
                                }
                            }
                        } finally {
                            mInternals.end(mModel.dig);
                        }
                    }
                    case "expr" -> {
                        // オーディオイベントのピッチ（ノートエクスプレッション）の中身を調べる
                        ClassLoader cl = mModel.dig.getClass().getClassLoader();
                        Object kind = cl.loadClass("N2l").getField(a.length > 1 ? a[1] : "Xzy").get(null);
                        for (Object o : root.mainTracks()) {
                            var t = ((com.bitwig.extension.api.project.track.TrackOrTrackGroup) o).asTrack();
                            if (t == null) continue;
                            for (Object e : t.clipTimeline().getEvents()) {
                                if (!(e instanceof com.bitwig.extension.api.project.timeline.AudioClip c)) continue;
                                for (Object n : c.getContent().getEventTimeline().getEvents()) {
                                    Object note = Internals.target(n);
                                    Object exprs = Internals.find(note.getClass(), "A1o", 0).invoke(note);
                                    Object lane = exprs.getClass().getMethod("Xzy", kind.getClass(), boolean.class).invoke(exprs, kind, false);
                                    log("lane = " + (lane == null ? "null" : lane.getClass().getName()));
                                    if (lane == null) continue;
                                    for (String m : new String[] { "FKQ", "L1N" })
                                        log("  " + m + " = " + Internals.find(lane.getClass(), m, 0).invoke(lane));
                                    for (Class<?> k = lane.getClass(); k != null; k = k.getSuperclass())
                                        for (var m : k.getDeclaredMethods())
                                            if (m.getParameterCount() == 0 && List.class.isAssignableFrom(m.getReturnType())) {
                                                m.setAccessible(true);
                                                List<?> l = (List<?>) m.invoke(lane);
                                                log("  list " + k.getSimpleName() + "." + m.getName() + " size " + (l == null ? -1 : l.size()));
                                                if (l != null) for (Object p : l) log("    " + dumpFields(p, 0));
                                            }
                                }
                            }
                        }
                    }
                    case "fields" -> {
                        // 選んだ種類の内部オブジェクトのフィールドを全部書き出す（メソッドは呼ばない）
                        StringBuilder sb = new StringBuilder();
                        for (Object o : root.mainTracks()) {
                            var t = ((com.bitwig.extension.api.project.track.TrackOrTrackGroup) o).asTrack();
                            if (t == null) continue;
                            for (Object e : t.clipTimeline().getEvents()) {
                                if (!(e instanceof com.bitwig.extension.api.project.timeline.AudioClip c)) continue;
                                sb.append("clip ").append(dumpFields(Internals.target(c), 0)).append('\n');
                                for (Object n : c.getContent().getEventTimeline().getEvents())
                                    sb.append("  note ").append(dumpFields(Internals.target(n), 1)).append('\n');
                            }
                        }
                        try (FileOutputStream f = new FileOutputStream(new File(LOG_DIR, a.length > 1 ? a[1] : "fields.txt"))) {
                            f.write(sb.toString().getBytes(StandardCharsets.UTF_8));
                        }
                    }
                    case "tempo" -> mApi.getTransport().getTempo().setValue(Double.parseDouble(a[1]), com.bitwig.extension.api.project.parameter.Unit.BPM);
                    case "sig" -> {
                        var f = (com.bitwig.extension.api.project.Factory) mApi.getClass().getMethod("getFactory").invoke(mApi);
                        var cur = mApi.getTransport().getTimeSignature();
                        log("current sig " + cur.getNumerator() + "/" + cur.getDenominator() + " tick " + cur.getTickRate());
                        mApi.getTransport().setTimeSignature(f.createTimeSignature(Integer.parseInt(a[1]), Integer.parseInt(a[2]), cur.getTickRate()));
                    }
                    case "delete-rest" -> {
                        List<Object> all = new ArrayList<>();
                        for (Object o : root.mainTracks()) all.add(Internals.target(o));
                        all.remove(0);
                        for (Object o : root.effectTracks()) all.add(Internals.target(o));
                        mInternals.delete(mModel.dig, all);
                    }
                    case "dump" -> {
                        try (FileOutputStream f = new FileOutputStream(new File(LOG_DIR, "state.json"))) {
                            f.write(Json.write(new java.util.TreeMap<>(mModel.readAll())).getBytes(StandardCharsets.UTF_8));
                        }
                    }
                    default -> log("unknown command");
                }
                log("ok");
            } catch (Throwable t) {
                log("cmd failed: " + stack(t));
            }
        }
    }

    private static com.bitwig.extension.api.project.track.Track findTrack(com.bitwig.extension.api.project.track.TrackGroup group, String name) {
        for (Object o : group.mainTracks()) {
            if (o instanceof com.bitwig.extension.api.project.track.TrackGroup g) {
                var t = findTrack(g, name);
                if (t != null) return t;
            } else if (o instanceof com.bitwig.extension.api.project.track.TrackOrTrackGroup t && name.equals(t.getTitle())) {
                return t.asTrack();
            }
        }
        return null;
    }

    /** 調査用: オブジェクトのフィールドを文字列にする（depth 段まで中のオブジェクトもたどる）。 */
    private static String dumpFields(Object o, int depth) {
        StringBuilder sb = new StringBuilder(o.getClass().getName()).append(" {");
        for (Class<?> k = o.getClass(); k != null && k != Object.class; k = k.getSuperclass()) {
            for (java.lang.reflect.Field f : k.getDeclaredFields()) {
                if (java.lang.reflect.Modifier.isStatic(f.getModifiers())) continue;
                try {
                    f.setAccessible(true);
                    Object v = f.get(o);
                    String s;
                    if (v == null || v instanceof Number || v instanceof Boolean || v instanceof String || v instanceof Enum<?>) s = String.valueOf(v);
                    else if (v instanceof double[] d) s = java.util.Arrays.toString(d);
                    else if (v instanceof int[] d) s = java.util.Arrays.toString(d);
                    else if (depth > 0 && v.getClass().getName().indexOf('.') < 0 && !(v instanceof java.util.Collection<?>)) s = dumpFields(v, depth - 1);
                    else s = v.getClass().getSimpleName() + (v instanceof java.util.Collection<?> c ? "(" + c.size() + ")" : "");
                    sb.append(' ').append(k.getSimpleName()).append('.').append(f.getName()).append('=').append(s);
                } catch (Throwable t) {
                    sb.append(' ').append(f.getName()).append("=?");
                }
            }
        }
        return sb.append(" }").toString();
    }

    /** 調査用: LOG_DIR に "debug" というファイルがあれば、今の状態を state.json に書き出す。 */
    private void debugDump() {
        if (mModel == null || !new File(LOG_DIR, "debug").exists()) return;
        try (FileOutputStream f = new FileOutputStream(new File(LOG_DIR, "state.json"))) {
            f.write(Json.write(mModel.lastState()).getBytes(StandardCharsets.UTF_8));
        } catch (IOException ignored) {
        }
    }

    private void sendOps(List<Map<String, Object>> ops) {
        if (ops.isEmpty() || !mConnected) return;
        for (Map<String, Object> op : ops) {
            Map<String, Object> value = Json.map(op.get("v"));
            Map<String, Object> delta = value == null ? null : Json.map(value.get("delta"));
            java.util.Set<String> fields = new java.util.HashSet<>();
            if (delta == null) fields.add("*");
            else if (Json.list(delta.get("f")) != null) for (Object f : Json.list(delta.get("f"))) fields.add(String.valueOf(f));
            mPending.computeIfAbsent((String) op.get("k"), k -> new ArrayList<>()).add(fields);
        }
        send(Json.obj("t", "ops", "ops", ops));
    }

    // ----------------------------------------------------------- messages

    /** 届いたメッセージを処理する。プロジェクトの中身を変えたら true。 */
    private boolean handle(Map<String, Object> msg) throws Exception {
        if (msg == null) return false;
        String kind = Json.str(msg.get("t"));
        if (kind == null) return false;
        switch (kind) {
            case "apply" -> {
                flushLocal(true);
                List<Object> ops = Json.list(msg.get("ops"));
                if (ops == null) return false;
                mInternals.begin(mModel.dig, "AbletonMulti");
                try {
                    mModel.apply(ops, Json.bool(msg.get("force")), mPending);
                } finally {
                    mInternals.end(mModel.dig);
                }
                debugDump();
                return true;
            }
            case "ack" -> {
                List<Object> keys = Json.list(msg.get("keys"));
                if (keys == null) return false;
                for (Object k : keys) {
                    if (!(k instanceof String key)) continue;
                    List<java.util.Set<String>> waiting = mPending.get(key);
                    if (waiting != null && !waiting.isEmpty()) waiting.remove(0);
                    if (waiting != null && waiting.isEmpty()) mPending.remove(key);
                }
            }
            case "reset" -> mPending.clear();
            case "snapshot_req" -> {
                flushLocal(true);
                send(Json.obj("t", "snapshot", "id", msg.get("id"), "ops", mModel.snapshot()));
            }
            case "blank" -> {
                mInternals.begin(mModel.dig, "AbletonMulti: まっさらにする");
                try {
                    mModel.makeBlank();
                } finally {
                    mInternals.end(mModel.dig);
                }
                mModel.collectChanges();  // まっさらにした変更そのものは送らない
                send(Json.obj("t", "blanked", "id", msg.get("id")));
                return true;
            }
            case "adopt" -> {
                Map<String, Object> map = Json.map(msg.get("map"));
                mModel.adopt(map == null ? Map.of() : map);
                mModel.collectChanges();
                send(Json.obj("t", "adopted", "id", msg.get("id")));
            }
            default -> { }
        }
        return false;
    }

    private void onConnected() {
        mConnected = true;
        mPending.clear();
        sendHello();
        mPopup = "AbletonMulti: アプリに接続しました";
    }

    private void onDisconnected() {
        if (mConnected) mPopup = "AbletonMulti: アプリとの接続が切れました";
        mConnected = false;
        mPending.clear();
    }

    private void sendHello() {
        String version = mHost.getHostVersion();
        send(Json.obj("t", "hello", "proto", (double) PROTOCOL, "script", "bitwig-" + BridgeDefinition.VERSION,
                "live", "Bitwig Studio " + version, "daw", "bitwig"));
        send(Json.obj("t", "api", "info", Json.obj(
                "daw", "bitwig", "version", version,
                "moveTracks", mInternals != null && mInternals.canMoveTracks(),
                "ungroup", mInternals != null && mInternals.canUngroup())));
    }

    private void warn(String message) {
        log("warn: " + message);
        send(Json.obj("t", "warn", "msg", message));
    }

    private void send(Map<String, Object> msg) {
        if (mSocket != null) mOutbox.add(Json.write(msg) + "\n");
    }

    // ------------------------------------------------------------ network

    private void networkLoop() {
        while (mRunning) {
            Socket socket = new Socket();
            boolean opened = false;
            try {
                socket.connect(new InetSocketAddress(APP_HOST, appPort()), 2000);
                socket.setTcpNoDelay(true);
                mOutbox.clear();
                mOut = socket.getOutputStream();
                mSocket = socket;
                opened = true;
                mInbox.add(CONNECTED);
                BufferedReader reader = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
                String line;
                while (mRunning && (line = reader.readLine()) != null) {
                    if (line.isBlank()) continue;
                    try {
                        mInbox.add(Json.parse(line));
                    } catch (RuntimeException e) {
                        log("bad message: " + e);
                    }
                }
            } catch (IOException ignored) {
                // アプリが起動していない、または切れた
            } finally {
                mSocket = null;
                mOut = null;
                try {
                    socket.close();
                } catch (IOException ignored) {
                }
                if (opened) mInbox.add(DISCONNECTED);
            }
            sleep(2000);
        }
    }

    /**
     * つなぐ先のポート。テスト用に、LOG_DIR の debug と port（"ポート 期限のミリ秒"）があれば、期限までそちらを使う
     * （本物の Live の Remote Script と同じ 47400 を使うと、テストが本物の Live につながってしまうため）。
     */
    private static int appPort() {
        try {
            File file = new File(LOG_DIR, "port");
            if (file.exists() && new File(LOG_DIR, "debug").exists()) {
                String[] a = java.nio.file.Files.readString(file.toPath()).trim().split(" +");
                if (a.length == 2 && System.currentTimeMillis() < Long.parseLong(a[1])) return Integer.parseInt(a[0]);
            }
        } catch (IOException | RuntimeException ignored) {
        }
        return APP_PORT;
    }

    private void writeLoop() {
        while (mRunning) {
            String line;
            try {
                line = mOutbox.poll(200, TimeUnit.MILLISECONDS);
            } catch (InterruptedException e) {
                return;
            }
            OutputStream out = mOut;
            if (line == null || out == null) continue;
            try {
                out.write(line.getBytes(StandardCharsets.UTF_8));
                if (mOutbox.isEmpty()) out.flush();
            } catch (IOException e) {
                closeSocket();
            }
        }
    }

    private void closeSocket() {
        Socket s = mSocket;
        if (s != null) {
            try {
                s.close();
            } catch (IOException ignored) {
            }
        }
    }

    private static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException ignored) {
        }
    }

    // ------------------------------------------------------------ logging

    private synchronized void log(String message) {
        try (FileOutputStream f = new FileOutputStream(new File(LOG_DIR, "bridge.log"), true)) {
            f.write((LocalTime.now() + " " + message + "\n").getBytes(StandardCharsets.UTF_8));
        } catch (IOException ignored) {
        }
    }

    private static String stack(Throwable t) {
        StringWriter w = new StringWriter();
        t.printStackTrace(new PrintWriter(w));
        return w.toString();
    }
}
