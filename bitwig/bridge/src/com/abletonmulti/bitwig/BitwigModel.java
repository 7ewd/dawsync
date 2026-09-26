package com.abletonmulti.bitwig;

import java.io.File;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.Deque;
import java.util.HashMap;
import java.util.HashSet;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Random;
import java.util.Set;

import com.bitwig.extension.api.Color;
import com.bitwig.extension.api.project.Factory;
import com.bitwig.extension.api.project.Project;
import com.bitwig.extension.api.project.Transport;
import com.bitwig.extension.api.project.parameter.Unit;
import com.bitwig.extension.api.project.timeline.AudioClip;
import com.bitwig.extension.api.project.timeline.AudioNote;
import com.bitwig.extension.api.project.timeline.Clip;
import com.bitwig.extension.api.project.timeline.EventTimeline;
import com.bitwig.extension.api.project.timeline.InstrumentNote;
import com.bitwig.extension.api.project.timeline.NoteClip;
import com.bitwig.extension.api.project.timeline.WarpMode;
import com.bitwig.extension.api.project.track.Track;
import com.bitwig.extension.api.project.track.TrackGroup;
import com.bitwig.extension.api.project.track.TrackOrTrackGroup;
import com.bitwig.extension.api.project.track.TrackType;

/**
 * Bitwig のプロジェクトを「キー → 値」の集まりとして読み書きする。キーと値の形は Live 用の model.py と同じ。
 *
 *   tempo, sig
 *   t/<id>                  {"o": 並び順, "k": midi|audio|group, "g": 入っているグループの id}
 *   t/<id>/name|color（ミュート・ソロは各自のものなので同期しない）
 *   c/a/<トラック id>/<開始位置>  アレンジャーのクリップ {"k", "len", "dur", "n" or "file", "p"}
 *
 * Live と違い、変更の通知はプロジェクト全体で 1 つしか来ないので、変わったら全部読み直して前回と比べる。
 * すべて Bitwig のドキュメントのスレッド（main）で呼ぶこと。
 */
final class BitwigModel {
    interface Output {
        void log(String message);
        void warn(String message);
    }

    private static final double NOTE_EPS = 1e-6;

    final Object dig;
    private final Project api;
    private final Factory factory;
    private final Internals in;
    private final Output out;

    private final Map<Object, String> ids = new IdentityHashMap<>();   // 内部のトラック → id
    private final Set<String> used = new HashSet<>();
    private final Map<String, Double> order = new HashMap<>();
    private final Set<String> unplaced = new HashSet<>();
    private final Map<String, Object> last = new HashMap<>();
    private final Map<String, Object> orphans = new LinkedHashMap<>();
    private final List<Map<String, Object>> deferred = new ArrayList<>();
    // 今反映しているまとまりの中の「クリップの移動」（消す＋別の位置に作る）。移動先のキー → 移動元の位置
    private final Map<String, String> moves = new HashMap<>();
    private final Set<String> movedAway = new HashSet<>();
    // このまとまりの中で、トラックなどを内部で消した（並べ替えの一時グループ・作り直しなど）。PARAMETERS_FIRST を参照
    private boolean structuralDelete;
    private final Set<String> warned = new HashSet<>();
    private final String tag;
    private int counter;
    private boolean initialized;
    private boolean baselineNext;

    // 最後に読んだときのトラック
    private final Map<String, TrackOrTrackGroup> tracks = new HashMap<>();
    private final Map<String, String> parents = new HashMap<>();
    private Set<String> batchDeletes = Set.of();

    BitwigModel(Object dig, Project api, Internals in, Output out) throws ReflectiveOperationException {
        this.dig = dig;
        this.api = api;
        this.in = in;
        this.out = out;
        this.factory = (Factory) api.getClass().getMethod("getFactory").invoke(api);
        this.tag = String.format("%06x", new Random().nextInt(1 << 24));
        this.baselineNext = true;  // 最初に読んだ状態は送らない（アプリが必要ならスナップショットを取る）
    }

    // ================================================================ read

    private String newId() {
        counter++;
        return tag + counter;
    }

    private String assign(Object internal, String fallback) {
        String id = ids.get(internal);
        if (id == null) {
            id = !initialized && !used.contains(fallback) ? fallback : newId();
            ids.put(internal, id);
            used.add(id);
        }
        return id;
    }

    /** トラックを「グループ → 中身」の順に平らに並べる（Live のトラック一覧と同じ並び）。 */
    private void flatten(TrackGroup group, String parentId, List<TrackOrTrackGroup> list, List<String> parentIds) {
        for (Object o : group.mainTracks()) {
            TrackOrTrackGroup t = (TrackOrTrackGroup) o;
            list.add(t);
            parentIds.add(parentId);
            if (t instanceof TrackGroup g) flatten(g, null, list, parentIds);
        }
    }

    /** 今のプロジェクトを全部読む。 */
    Map<String, Object> readAll() {
        Map<String, Object> state = new LinkedHashMap<>();
        Transport transport = api.getTransport();
        state.put("tempo", round(transport.getTempo().getValue(Unit.BPM), 3));
        var sig = transport.getTimeSignature();
        state.put("sig", List.of((double) sig.getNumerator(), (double) sig.getDenominator()));

        List<TrackOrTrackGroup> flat = new ArrayList<>();
        List<String> ignored = new ArrayList<>();
        flatten(api.getTrackGroup(), null, flat, ignored);
        List<String> flatIds = new ArrayList<>();
        for (int n = 0; n < flat.size(); n++) flatIds.add(assign(Internals.target(flat.get(n)), "t" + n));
        fixOrders(flatIds);

        tracks.clear();
        parents.clear();
        for (int n = 0; n < flat.size(); n++) tracks.put(flatIds.get(n), flat.get(n));
        readParents(api.getTrackGroup(), null);
        initialized = true;

        for (int n = 0; n < flat.size(); n++) {
            String id = flatIds.get(n);
            TrackOrTrackGroup t = flat.get(n);
            String key = "t/" + id;
            state.put(key, Json.obj("o", order.get(id), "k", kindOf(t), "g", parents.get(id)));
            state.put(key + "/name", t.getTitle());
            state.put(key + "/color", (double) colorToInt(t.getColor()));
            if (!(t instanceof TrackGroup) && t.asTrack() != null) readClips(id, t.asTrack(), state);
        }
        // 消えたトラックの id は忘れる
        Set<Object> alive = Collections.newSetFromMap(new IdentityHashMap<>());
        for (TrackOrTrackGroup t : flat) alive.add(Internals.target(t));
        ids.keySet().retainAll(alive);
        return state;
    }

    private void readParents(TrackGroup group, String parentId) {
        for (Object o : group.mainTracks()) {
            TrackOrTrackGroup t = (TrackOrTrackGroup) o;
            String id = ids.get(Internals.target(t));
            parents.put(id, parentId);
            if (t instanceof TrackGroup g) readParents(g, id);
        }
    }

    private static String kindOf(TrackOrTrackGroup t) {
        if (t instanceof TrackGroup) return "group";
        Track track = t.asTrack();
        return track != null && track.getTrackType() == TrackType.AUDIO ? "audio" : "midi";
    }

    private void readClips(String tid, Track track, Map<String, Object> state) {
        for (Object e : track.clipTimeline().getEvents()) {
            if (!(e instanceof Clip c)) continue;
            Map<String, Object> value = readClip(c);
            if (value != null) state.put("c/a/" + tid + "/" + timeKey(c.getTime()), value);
        }
    }

    private Map<String, Object> readClip(Clip c) {
        boolean looping = c.isLoopEnabled();
        double sm = round(c.getPlayStartOffset()), dur = round(c.getDuration());
        double ls = round(c.getLoopStart()), le = round(c.getLoopStart() + c.getLoopDuration());
        double em = looping ? le : round(sm + dur);
        if (!looping) {
            // Live と同じく、ループしていないクリップのループ範囲はクリップの始まりと終わり
            ls = sm;
            le = em;
        }
        Map<String, Object> props = Json.obj(
                "name", c.getTitle() == null ? "" : c.getTitle(),
                "color", (double) colorToInt(c.getColor()),
                "looping", looping, "ls", ls, "le", le, "sm", sm, "em", em);
        double len = round(looping ? le - ls : em - sm);
        EventTimeline content = c.getContent().getEventTimeline();
        if (c instanceof AudioClip) {
            AudioNote an = audioNote(c);
            String file = an != null && an.getAudioFile() != null ? an.getAudioFile().getAbsolutePath() : null;
            if (an != null) readAudioProps(an, props);
            return Json.obj("k", "audio", "len", len, "file", file, "p", props, "dur", dur);
        }
        if (!(c instanceof NoteClip)) return null;
        List<List<Object>> rows = new ArrayList<>();
        for (Object o : content.getEvents())
            if (o instanceof InstrumentNote n) rows.add(noteRow(n));
        rows.sort(ROW_ORDER);
        return Json.obj("k", "midi", "len", len, "n", new ArrayList<Object>(rows), "p", props, "dur", dur);
    }

    /** ノート 1 つ: [音程, 開始, 長さ, ベロシティ, ミュート, 確率, ベロシティ範囲, リリースベロシティ]（Live と同じ形） */
    private static List<Object> noteRow(InstrumentNote n) {
        return List.of((double) n.getKey(), round(n.getTime()), round(n.getDuration()), round(n.getOnVelocity() * 127.0, 3),
                n.isMuted() ? 1.0 : 0.0, 1.0, 0.0, round(n.getOffVelocity() * 127.0, 3));
    }

    private static final Comparator<List<Object>> ROW_ORDER = (a, b) -> {
        for (int n = 0; n < Math.min(a.size(), b.size()); n++) {
            int c = Double.compare(Json.num(a.get(n), 0), Json.num(b.get(n), 0));
            if (c != 0) return c;
        }
        return Integer.compare(a.size(), b.size());
    };

    // ------------------------------------------------------------ orders

    private record OrderKey(double o, String id) implements Comparable<OrderKey> {
        public int compareTo(OrderKey other) {
            int c = Double.compare(o, other.o);
            return c != 0 ? c : id.compareTo(other.id);
        }
    }

    /** 並び順が付いていないもの・動いたものに並び順を付ける（model.py の _fix_orders と同じ）。 */
    private void fixOrders(List<String> list) {
        List<OrderKey> keys = new ArrayList<>();
        for (String id : list)
            keys.add(order.containsKey(id) && !unplaced.contains(id) ? new OrderKey(order.get(id), id) : null);
        Set<Integer> keep = lis(keys);
        Double prev = null;
        for (int n = 0; n < list.size(); n++) {
            String id = list.get(n);
            if (keep.contains(n)) {
                prev = order.get(id);
                continue;
            }
            if (unplaced.contains(id) && order.containsKey(id)) continue;
            Double next = null;
            for (int j = n + 1; j < list.size(); j++)
                if (keep.contains(j)) {
                    next = order.get(list.get(j));
                    break;
                }
            double o;
            if (prev == null && next == null) o = n;
            else if (prev == null) o = next - 1.0;
            else if (next == null) o = prev + 1.0;
            else o = (prev + next) / 2.0;
            order.put(id, o);
            prev = o;
        }
    }

    /** 最長増加部分列の添字（null は飛ばす）。 */
    private static Set<Integer> lis(List<OrderKey> keys) {
        List<OrderKey> tails = new ArrayList<>();
        List<Integer> tailsIdx = new ArrayList<>();
        Map<Integer, Integer> prev = new HashMap<>();
        for (int idx = 0; idx < keys.size(); idx++) {
            OrderKey k = keys.get(idx);
            if (k == null) continue;
            int lo = 0, hi = tails.size();
            while (lo < hi) {
                int mid = (lo + hi) / 2;
                if (tails.get(mid).compareTo(k) < 0) lo = mid + 1;
                else hi = mid;
            }
            prev.put(idx, lo > 0 ? tailsIdx.get(lo - 1) : null);
            if (lo == tails.size()) {
                tails.add(k);
                tailsIdx.add(idx);
            } else {
                tails.set(lo, k);
                tailsIdx.set(lo, idx);
            }
        }
        Set<Integer> result = new HashSet<>();
        Integer i = tailsIdx.isEmpty() ? null : tailsIdx.get(tailsIdx.size() - 1);
        while (i != null) {
            result.add(i);
            i = prev.get(i);
        }
        return result;
    }

    // ============================================================ changes

    /** 前回から変わったものを操作のリストにする。 */
    List<Map<String, Object>> collectChanges() {
        Map<String, Object> state = readAll();
        boolean baseline = baselineNext;
        baselineNext = false;
        List<Map<String, Object>> ops = new ArrayList<>();
        for (String key : new ArrayList<>(last.keySet())) {
            if (state.containsKey(key)) continue;
            Object before = last.remove(key);
            if (baseline || before == null) continue;
            String[] parts = key.split("/");
            if (parts.length == 2 && parts[0].equals("t")) ops.add(op(key, null));
            else if (parts[0].equals("c") && state.containsKey("t/" + parts[2])) ops.add(op(key, null));
        }
        for (Map.Entry<String, Object> e : state.entrySet()) {
            if (last.containsKey(e.getKey()) && Json.same(last.get(e.getKey()), e.getValue())) continue;
            Object previous = last.put(e.getKey(), e.getValue());
            if (!baseline) ops.add(op(e.getKey(), withDelta(e.getKey(), e.getValue(), previous)));
        }
        ops.sort(Comparator.comparingInt(o -> priority((String) o.get("k"))));
        return ops;
    }

    /** 最後に読んだ／反映した状態（調査用）。 */
    Map<String, Object> lastState() {
        return new java.util.TreeMap<>(last);
    }

    List<Map<String, Object>> snapshot() {
        Map<String, Object> state = readAll();
        last.clear();
        last.putAll(state);
        List<Map<String, Object>> ops = new ArrayList<>();
        for (Map.Entry<String, Object> e : state.entrySet()) ops.add(op(e.getKey(), e.getValue()));
        ops.sort(Comparator.comparingInt(o -> priority((String) o.get("k"))));
        return ops;
    }

    private static Map<String, Object> op(String key, Object value) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("k", key);
        m.put("v", value);
        return m;
    }

    static int priority(String key) {
        String[] parts = key.split("/");
        if (parts.length == 2 && (parts[0].equals("t") || parts[0].equals("r") || parts[0].equals("s"))) return 0;
        if (parts.length == 2 && parts[0].equals("d")) return 1;
        if (parts[0].equals("c")) return 2;
        return 3;
    }

    /** ルームと同じだとわかったとき、ルームの id に付け替える。 */
    void adopt(Map<String, Object> mapping) {
        Map<String, String> renamed = new HashMap<>();
        for (String id : new HashSet<>(ids.values())) {
            Object to = mapping.get(id);
            renamed.put(id, to instanceof String s ? s : newId());
        }
        ids.replaceAll((k, v) -> renamed.get(v));
        Map<String, Double> newOrder = new HashMap<>();
        for (Map.Entry<String, Double> e : order.entrySet())
            if (renamed.containsKey(e.getKey())) newOrder.put(renamed.get(e.getKey()), e.getValue());
        order.clear();
        order.putAll(newOrder);
        unplaced.clear();
        used.addAll(ids.values());
        last.clear();
        baselineNext = true;
    }

    /** まっさらにする（楽器トラック 1 本、テンポ 120、4/4）。 */
    void makeBlank() throws ReflectiveOperationException {
        // テンポなどのパラメータは、トラックを消すより先に変える（後だとオーディオエンジンが落ちる。PARAMETERS_FIRST を参照）
        Transport transport = api.getTransport();
        transport.getTempo().setValue(120.0, Unit.BPM);
        transport.setTimeSignature(factory.createTimeSignature(4, 4, transport.getTimeSignature().getTickRate()));
        Track keep = factory.createInstrumentTrack();
        TrackGroup root = api.getTrackGroup();
        in.insertTrack(Internals.target(root), Internals.target(keep), 0);
        List<Object> victims = new ArrayList<>();
        for (Object o : root.mainTracks())
            if (Internals.target(o) != Internals.target(keep)) victims.add(Internals.target(o));
        for (Object o : root.effectTracks()) victims.add(Internals.target(o));
        in.delete(dig, victims);
        last.clear();
        baselineNext = true;
    }

    // ============================================================= apply

    /** 届いた変更を反映する。pending は自分が送ってまだ戻ってきていないキー。 */
    /**
     * 届いた変更を反映する。pending は、自分が送ってまだ戻ってきていない変更それぞれの「変えた項目」
     * （"*" は全部）。そのキーは無視してよい（サーバー上では自分の変更の方が後）が、クリップは
     * 自分が変えた項目だけを無視する（ノートは足し引きなので、両方の編集を残せる）。
     */
    void apply(List<Object> rawOps, boolean force, Map<String, List<Set<String>>> pending) {
        List<Map<String, Object>> ops = new ArrayList<>();
        for (Object o : rawOps) {
            Map<String, Object> m = Json.map(o);
            if (m == null || !(m.get("k") instanceof String key)) continue;
            List<Set<String>> waiting = pending.getOrDefault(key, List.of());
            if (force || waiting.isEmpty()) {
                ops.add(m);
                continue;
            }
            Map<String, Object> value = Json.map(m.get("v"));
            Map<String, Object> delta = value == null ? null : Json.map(value.get("delta"));
            if (delta == null) continue;
            Set<String> mine = new HashSet<>();
            for (Set<String> s : waiting) mine.addAll(s);
            if (mine.contains("*")) continue;
            List<Object> fields = new ArrayList<>();
            List<Object> changed = Json.list(delta.get("f"));
            if (changed != null) for (Object f : changed) if (!mine.contains(f)) fields.add(f);
            Map<String, Object> narrowed = new LinkedHashMap<>(delta);
            narrowed.put("f", fields);
            Map<String, Object> v2 = new LinkedHashMap<>(value);
            v2.put("delta", narrowed);
            ops.add(op(key, v2));
        }
        if (ops.isEmpty()) return;

        readAll();
        for (Map<String, Object> op : ops) {
            String[] parts = ((String) op.get("k")).split("/");
            Map<String, Object> v = Json.map(op.get("v"));
            if (parts.length == 2 && parts[0].equals("t") && v != null && tracks.containsKey(parts[1]))
                order.put(parts[1], Json.num(v.get("o"), 0));
        }
        Set<String> deletes = new HashSet<>();
        for (Map<String, Object> op : ops) {
            String[] parts = ((String) op.get("k")).split("/");
            if (op.get("v") == null && parts.length == 2 && parts[0].equals("t")) deletes.add(parts[1]);
        }
        batchDeletes = deletes;

        ops.sort((a, b) -> {
            String ka = (String) a.get("k"), kb = (String) b.get("k");
            int c = Integer.compare(priority(ka), priority(kb));
            if (c != 0) return c;
            // トラックは作ってから消す。クリップは消してから作る（重なりで切れ端が残らないように）
            boolean deletionsFirst = priority(ka) != 0;
            boolean fa = deletionsFirst ? a.get("v") != null : a.get("v") == null;
            boolean fb = deletionsFirst ? b.get("v") != null : b.get("v") == null;
            c = Boolean.compare(fa, fb);
            if (c != 0) return c;
            c = Double.compare(orderOf(a.get("v")), orderOf(b.get("v")));
            return c != 0 ? c : ka.compareTo(kb);
        });

        // PARAMETERS_FIRST: Bitwig では、トラックなどを消した後、エンジンに伝わる前に（同じ tick の中で）
        // テンポ・ミュートなどのパラメータを変えるとオーディオエンジンが落ちる（エフェクトトラックの削除で確認）。
        // なので、今あるものへのパラメータの変更を先にやり、消した後に残ったパラメータの変更は次の tick に回す。
        findMoves(ops);
        structuralDelete = false;
        Set<String> applied = new HashSet<>();
        List<Map<String, Object>> rest = new ArrayList<>();
        for (Map<String, Object> op : ops) {
            String key = (String) op.get("k");
            if (isParameter(key) && parameterTargetExists(key)) {
                if (applyOne(key, op.get("v"))) applied.add(key);
            } else {
                rest.add(op);
            }
        }
        boolean deleted = false;
        for (Map<String, Object> op : rest) {
            String key = (String) op.get("k");
            if (isParameter(key) && (deleted || structuralDelete)) {
                deferred.add(op);
                continue;
            }
            if (applyOne(key, op.get("v"))) applied.add(key);
            if (op.get("v") == null) deleted = true;
        }
        applyOrphans(applied, deleted || structuralDelete);
        batchDeletes = Set.of();
        moves.clear();
        movedAway.clear();

        // 反映した結果を「最後の値」にする（送り返さない）。反映でできた新しいキーも同じ扱い
        Map<String, Object> state = readAll();
        for (String key : applied) {
            if (state.containsKey(key)) last.put(key, state.get(key));
            else last.remove(key);
        }
        for (Map.Entry<String, Object> e : state.entrySet())
            if (!last.containsKey(e.getKey())) last.put(e.getKey(), e.getValue());
        last.keySet().removeIf(k -> !state.containsKey(k));
    }

    /** 前の tick で後回しにした変更があるか。 */
    boolean hasDeferred() {
        return !deferred.isEmpty();
    }

    /** 前の tick で後回しにしたパラメータの変更を反映する（その間にこちらで変えたものは除く）。 */
    void applyDeferred(Map<String, List<Set<String>>> pending) {
        List<Object> ops = new ArrayList<>(deferred);
        deferred.clear();
        apply(ops, false, pending);
    }

    /** オーディオエンジンにすぐ伝わる「パラメータ」のキー（PARAMETERS_FIRST を参照）。 */
    private static boolean isParameter(String key) {
        return key.equals("tempo") || key.equals("sig") || key.endsWith("/mute") || key.endsWith("/solo")
                || key.endsWith("/vol") || key.endsWith("/pan") || key.contains("/send/");
    }

    private boolean parameterTargetExists(String key) {
        String[] parts = key.split("/");
        return parts.length == 1 || parts[0].equals("t") && tracks.containsKey(parts[1]);
    }

    /**
     * 相手がクリップを動かすと「元の位置のキーを消す」「新しい位置のキーを作る」の 2 つが届く。
     * そのまま消して作り直すと、Bitwig でそのクリップを開いていた人のエディタから消えてしまうので、
     * 同じトラックで消す 1 つと作る 1 つが組になっていたら、今あるクリップをそのまま動かす。
     */
    private void findMoves(List<Map<String, Object>> ops) {
        moves.clear();
        movedAway.clear();
        Map<String, List<String>> removed = new HashMap<>(), added = new HashMap<>();
        for (Map<String, Object> op : ops) {
            String[] parts = ((String) op.get("k")).split("/");
            if (parts.length != 4 || !parts[0].equals("c") || !parts[1].equals("a")) continue;
            TrackOrTrackGroup owner = tracks.get(parts[2]);
            Track track = owner == null || owner instanceof TrackGroup ? null : owner.asTrack();
            if (track == null) continue;
            boolean exists = findClip(track, parts[3]) != null;
            if (op.get("v") == null && exists) removed.computeIfAbsent(parts[2], k -> new ArrayList<>()).add(parts[3]);
            if (op.get("v") != null && !exists) added.computeIfAbsent(parts[2], k -> new ArrayList<>()).add(parts[3]);
        }
        for (Map.Entry<String, List<String>> e : removed.entrySet()) {
            List<String> to = added.get(e.getKey());
            if (e.getValue().size() != 1 || to == null || to.size() != 1) continue;
            moves.put("c/a/" + e.getKey() + "/" + to.get(0), e.getValue().get(0));
            movedAway.add("c/a/" + e.getKey() + "/" + e.getValue().get(0));
        }
    }

    // ------------------------------------------------ 変わったところ（delta）
    //
    // クリップの変更は「値全体」に加えて、変わったところも送る（Live の model.py の with_delta と同じ形）:
    //   "delta": {"f": 変わった項目（"dur" や "p.name"）, "a": 足したノート, "d": 消したノート}

    private static List<Object> fullRowOf(Object r) {
        return fullRow(Json.list(r));
    }

    private static String identOf(List<Object> row) {
        return noteKey(Json.num(row.get(0), 0), Json.num(row.get(1), 0), Json.num(row.get(2), 0));
    }

    static Object withDelta(String key, Object value, Object previous) {
        Map<String, Object> v = Json.map(value), p = Json.map(previous);
        if (!key.startsWith("c/") || v == null || p == null || !Json.same(v.get("k"), p.get("k"))) return value;
        List<Object> fields = new ArrayList<>();
        Set<String> names = new java.util.TreeSet<>(v.keySet());
        names.addAll(p.keySet());
        for (String f : names)
            if (!f.equals("n") && !f.equals("p") && !f.equals("delta") && !Json.same(v.get(f), p.get(f))) fields.add(f);
        Map<String, Object> vp = Json.map(v.get("p")), pp = Json.map(p.get("p"));
        Set<String> props = new java.util.TreeSet<>();
        if (vp != null) props.addAll(vp.keySet());
        if (pp != null) props.addAll(pp.keySet());
        for (String f : props)
            if (!Json.same(vp == null ? null : vp.get(f), pp == null ? null : pp.get(f))) fields.add("p." + f);
        Map<String, Object> delta = Json.obj("f", fields);
        if ("midi".equals(v.get("k"))) {
            List<Object> oldRows = new ArrayList<>();
            List<Object> prevRows = Json.list(p.get("n"));
            if (prevRows != null) for (Object r : prevRows) oldRows.add(fullRowOf(r));
            List<Object> added = new ArrayList<>();
            List<Object> newRows = Json.list(v.get("n"));
            if (newRows != null)
                for (Object r : newRows) {
                    List<Object> row = fullRowOf(r);
                    int i = indexOfSame(oldRows, row);
                    if (i >= 0) oldRows.remove(i);
                    else added.add(row);
                }
            delta.put("a", added);
            delta.put("d", oldRows);
        }
        Map<String, Object> out = new LinkedHashMap<>(v);
        out.put("delta", delta);
        return out;
    }

    private static int indexOfSame(List<Object> rows, List<Object> row) {
        for (int i = 0; i < rows.size(); i++) if (Json.same(rows.get(i), row)) return i;
        return -1;
    }

    /** 今の値 current に、届いた値 incoming の delta（変わったところ）だけを重ねる。 */
    static Map<String, Object> mergeDelta(Map<String, Object> current, Map<String, Object> incoming) {
        Map<String, Object> delta = Json.map(incoming.get("delta"));
        Map<String, Object> result = new LinkedHashMap<>(current);
        Map<String, Object> props = new LinkedHashMap<>();
        if (Json.map(current.get("p")) != null) props.putAll(Json.map(current.get("p")));
        result.put("p", props);
        Map<String, Object> incomingProps = Json.map(incoming.get("p"));
        List<Object> fields = Json.list(delta.get("f"));
        if (fields != null)
            for (Object o : fields) {
                String f = String.valueOf(o);
                if (f.startsWith("p.")) {
                    String name = f.substring(2);
                    if (incomingProps != null && incomingProps.containsKey(name)) props.put(name, incomingProps.get(name));
                    else props.remove(name);
                } else if (incoming.containsKey(f)) {
                    result.put(f, incoming.get(f));
                } else {
                    result.remove(f);
                }
            }
        if (delta.containsKey("a") || delta.containsKey("d")) {
            List<Object> rows = new ArrayList<>();
            List<Object> currentRows = Json.list(current.get("n"));
            if (currentRows != null) for (Object r : currentRows) rows.add(fullRowOf(r));
            List<Object> removed = Json.list(delta.get("d"));
            if (removed != null)
                for (Object r : removed) {
                    String ident = identOf(fullRowOf(r));
                    for (int i = 0; i < rows.size(); i++)
                        if (identOf(Json.list(rows.get(i))).equals(ident)) {
                            rows.remove(i);
                            break;
                        }
                }
            List<Object> added = Json.list(delta.get("a"));
            if (added != null)
                for (Object r : added) {
                    List<Object> row = fullRowOf(r);
                    String ident = identOf(row);
                    rows.removeIf(x -> identOf(Json.list(x)).equals(ident));
                    rows.add(row);
                }
            result.put("n", rows);
        }
        result.remove("delta");
        return result;
    }

    private static double orderOf(Object v) {
        Map<String, Object> m = Json.map(v);
        return m != null ? Json.num(m.get("o"), 0) : 0;
    }

    private boolean applyOne(String key, Object value) {
        try {
            String[] parts = key.split("/");
            switch (parts[0]) {
                case "tempo" -> {
                    double bpm = Json.num(value, 120);
                    if (Math.abs(api.getTransport().getTempo().getValue(Unit.BPM) - bpm) > 1e-6)
                        api.getTransport().getTempo().setValue(bpm, Unit.BPM);
                }
                case "sig" -> {
                    List<Object> l = Json.list(value);
                    if (l == null || l.size() < 2) return false;
                    Transport transport = api.getTransport();
                    var current = transport.getTimeSignature();
                    int num = (int) Json.num(l.get(0), 4), den = (int) Json.num(l.get(1), 4);
                    if (current.getNumerator() != num || current.getDenominator() != den)
                        transport.setTimeSignature(factory.createTimeSignature(num, den, current.getTickRate()));
                }
                case "t" -> {
                    if (parts.length == 2) return applyTrack(parts[1], Json.map(value));
                    TrackOrTrackGroup t = tracks.get(parts[1]);
                    if (t == null) {
                        remember(key, value);
                        return false;
                    }
                    applyTrackProp(t, parts[2], value);
                }
                case "c" -> {
                    if (parts.length < 4 || !parts[1].equals("a")) {
                        if (value != null) warnOnce("session", "Bitwig ではクリップランチャー（セッション）のクリップはまだ同期できません");
                        return false;
                    }
                    if (!tracks.containsKey(parts[2])) {
                        if (value != null) remember(key, value);
                        return false;
                    }
                    applyClip(parts[2], parts[3], Json.map(value));
                }
                default -> {
                    return false;  // リターン・マスター・シーン・デバイスはまだ
                }
            }
            return true;
        } catch (Exception e) {
            out.log("failed to apply " + key + ": " + e);
            return false;
        }
    }

    private void remember(String key, Object value) {
        if (orphans.size() < 20000) orphans.put(key, value);
    }

    private void applyOrphans(Set<String> applied, boolean deleted) {
        if (orphans.isEmpty()) return;
        readAll();
        List<String> ready = new ArrayList<>();
        for (String key : orphans.keySet()) {
            String[] parts = key.split("/");
            if (parts.length >= 3 && (parts[0].equals("t") && tracks.containsKey(parts[1])
                    || parts[0].equals("c") && tracks.containsKey(parts[2]))) ready.add(key);
        }
        ready.sort(Comparator.comparingInt(BitwigModel::priority));
        for (String key : ready) {
            Object value = orphans.remove(key);
            if (deleted && isParameter(key)) {
                deferred.add(op(key, value));
                continue;
            }
            if (applyOne(key, value)) applied.add(key);
        }
    }

    // ------------------------------------------------------------ tracks

    private TrackGroup groupFor(String gid) {
        if (gid != null && tracks.get(gid) instanceof TrackGroup g) return g;
        return api.getTrackGroup();
    }

    /** group の中で、並び順 o の id が入るべき位置。 */
    private int targetIndex(TrackGroup group, String id, double o) {
        OrderKey me = new OrderKey(o, id);
        int count = 0;
        for (Object x : group.mainTracks()) {
            String other = ids.get(Internals.target(x));
            if (other == null || other.equals(id) || !order.containsKey(other)) continue;
            if (new OrderKey(order.get(other), other).compareTo(me) < 0) count++;
        }
        return count;
    }

    private boolean applyTrack(String id, Map<String, Object> value) throws ReflectiveOperationException {
        TrackOrTrackGroup existing = tracks.get(id);
        if (value == null) {
            if (existing != null) deleteTrack(id, existing);
            order.remove(id);
            readAll();
            return true;
        }
        double o = Json.num(value.get("o"), 0);
        order.put(id, o);
        String kind = Json.str(value.get("k"));
        if (kind == null) kind = existing != null ? kindOf(existing) : "midi";
        String gid = Json.str(value.get("g"));
        if (gid != null && !tracks.containsKey(gid) && !gid.equals(id)) {
            warnOnce("nogroup:" + gid, "グループがまだ届いていないので、いったんグループの外に置きます");
        }
        TrackGroup parent = groupFor(gid);
        Object parentInternal = Internals.target(parent);

        if (existing != null && !kindOf(existing).equals(kind)) {
            // バウンスなどで種類が変わった → 作り直す
            TrackOrTrackGroup old = existing;
            ids.remove(Internals.target(old));
            createTrack(id, kind, parent, o);
            in.delete(dig, List.of(Internals.target(old)));
            structuralDelete = true;
            last.keySet().removeIf(k -> k.startsWith("c/a/" + id + "/"));
            readAll();
            return true;
        }
        if (existing == null) {
            createTrack(id, kind, parent, o);
            readAll();
            return true;
        }
        String currentParent = parents.get(id);
        if (!java.util.Objects.equals(currentParent, gid == null || tracks.containsKey(gid) ? gid : null)) {
            if (in.canMoveTracks()) {
                TrackGroup from = groupFor(currentParent);
                in.moveTrack(parentInternal, Internals.target(from), Internals.target(existing), targetIndex(parent, id, o));
                unplaced.remove(id);
            } else {
                warnOnce("move", "この Bitwig ではトラックをグループに入れる／出すを同期できません");
            }
            readAll();
            return true;
        }
        int pos = targetIndex(parent, id, o);
        int current = parent.mainTracks().indexOf(existing);
        if (current >= 0 && current != pos) {
            if (in.canMoveTracks() && reorder(parent, existing, pos)) {
                unplaced.remove(id);
                readAll();
            } else {
                unplaced.add(id);
                warnOnce("reorder", "この Bitwig ではトラックの並べ替えを同期できません（中身は同期されます）");
            }
        }
        return true;
    }

    /**
     * 同じグループの中での並べ替え。Bitwig の内部には「別のグループへ移す」しか見つからなかったので、
     * いったん空のグループへ移してから元のグループの pos 番目へ戻し、空のグループを消す（トラックの中身はそのまま）。
     */
    private boolean reorder(TrackGroup parent, TrackOrTrackGroup track, int pos) throws ReflectiveOperationException {
        Object parentInternal = Internals.target(parent);
        TrackGroup temp = factory.createTrackGroup();
        Object tempInternal = Internals.target(temp);
        in.insertTrack(parentInternal, tempInternal, parent.mainTracks().size());
        try {
            in.moveTrack(tempInternal, parentInternal, Internals.target(track), 0);
            in.moveTrack(parentInternal, tempInternal, Internals.target(track), pos);
        } finally {
            in.delete(dig, List.of(tempInternal));
            structuralDelete = true;
        }
        return parent.mainTracks().indexOf(track) == pos;
    }

    private void createTrack(String id, String kind, TrackGroup parent, double o) throws ReflectiveOperationException {
        TrackOrTrackGroup created = switch (kind == null ? "midi" : kind) {
            case "group" -> factory.createTrackGroup();
            case "audio" -> factory.createAudioTrack();
            default -> factory.createInstrumentTrack();
        };
        Object internal = Internals.target(created);
        in.insertTrack(Internals.target(parent), internal, targetIndex(parent, id, o));
        ids.put(internal, id);
        used.add(id);
        unplaced.remove(id);
    }

    private void deleteTrack(String id, TrackOrTrackGroup t) throws ReflectiveOperationException {
        if (t instanceof TrackGroup g) {
            boolean survivors = false;
            for (Object c : g.mainTracks()) {
                String cid = ids.get(Internals.target(c));
                if (cid == null || !batchDeletes.contains(cid)) survivors = true;
            }
            if (survivors && in.canUngroup()) {
                // 相手は「グループ解除」しただけ。中のトラックは残す
                in.ungroup(Internals.target(g), dig);
                structuralDelete = true;
                return;
            }
        }
        in.delete(dig, List.of(Internals.target(t)));
        structuralDelete = true;
    }

    private void applyTrackProp(TrackOrTrackGroup t, String prop, Object value) {
        switch (prop) {
            case "name" -> t.setTitle(value == null ? "" : value.toString());
            case "color" -> t.setColor(intToColor((int) Json.num(value, 0)));
            default -> { }  // ミュート・ソロ・音量・パン・センドは同期しない（各自で調整する）
        }
    }

    // ------------------------------------------------------------- clips

    private Clip findClip(Track track, String timeKey) {
        for (Object e : track.clipTimeline().getEvents())
            if (e instanceof Clip c && timeKey(c.getTime()).equals(timeKey)) return c;
        return null;
    }

    private void applyClip(String tid, String timeKey, Map<String, Object> value) throws ReflectiveOperationException {
        TrackOrTrackGroup owner = tracks.get(tid);
        Track track = owner == null ? null : owner.asTrack();
        if (track == null || owner instanceof TrackGroup) return;
        String key = "c/a/" + tid + "/" + timeKey;
        Clip existing = findClip(track, timeKey);
        if (value == null) {
            if (existing != null && !movedAway.contains(key)) in.deleteEvents(List.of(existing));
            return;
        }
        if (existing == null && moves.containsKey(key)) {
            // 動かしたクリップ: 作り直さずに今あるものを動かす（中身の違いはこの後で直す）
            Clip moved = findClip(track, moves.get(key));
            if (moved != null) {
                moved.setTime(Double.parseDouble(timeKey));
                existing = moved;
            }
        }
        if (existing != null && Json.map(value.get("delta")) != null) {
            // 変わったところだけを今のクリップに重ねる（相手の古い値でこちらの編集を消さないように）
            Map<String, Object> current = readClip(existing);
            if (current != null && Json.same(current.get("k"), value.get("k"))) value = mergeDelta(current, value);
        }
        String kind = Json.str(value.get("k"));
        boolean audioTrack = track.getTrackType() == TrackType.AUDIO;
        if ("midi".equals(kind) == audioTrack) return;  // Live と同じく、種類の合わないトラックには置かない

        if (existing != null) {
            boolean isAudio = existing instanceof AudioClip;
            boolean recreate = isAudio != "audio".equals(kind);
            if (!recreate && isAudio) {
                Object current = readClip(existing).get("file");
                recreate = !sameFile(Json.str(current), Json.str(value.get("file")));
            }
            if (recreate) {
                in.deleteEvents(List.of(existing));
                existing = null;
            }
        }

        double start = Double.parseDouble(timeKey);
        double len = Math.max(Json.num(value.get("len"), 4.0), 0.25);
        double dur = Math.max(Json.num(value.get("dur"), len), 0.25);
        Map<String, Object> props = Json.map(value.get("p"));
        if (props == null) props = Map.of();

        if (existing == null) {
            Clip clip;
            if ("audio".equals(kind)) {
                String file = Json.str(value.get("file"));
                if (file == null || !new File(file).isFile()) {
                    warnOnce("nofile:" + tid + "/" + timeKey, "サンプルファイルが届いていないのでクリップを作れませんでした");
                    return;
                }
                clip = factory.createAudioClip(false);
                // バウンスは今のテンポで書き出したものなので、元の速さで鳴るようにワープマーカーを置く
                // （置かないと Bitwig はファイルを 120 BPM とみなして伸び縮みさせる）
                var sample = factory.createSampleReferenceForLocalFile(new File(file));
                double seconds = sample.getDurationInSeconds();
                double beats = seconds > 0 ? seconds * api.getTransport().getTempo().getValue(Unit.BPM) / 60.0 : len;
                AudioNote note = factory.createAudioNote(0.0, beats, sample);
                if (seconds > 0) {
                    var markers = factory.createWarpEvents();
                    markers.addWarpEvent(0.0, 0.0);
                    markers.addWarpEvent(beats, seconds);
                    note.setWarpEvents(markers);
                }
                clip.getContent().getEventTimeline().addEvent(note);
                clip.setTime(start);
                clip.setDuration(dur);
                writeClipProps(clip, props);
                track.clipTimeline().addEvent(clip);
                writeAudioProps(note, props, len);
                return;
            } else {
                clip = factory.createNoteClip(false);
                List<Object> rows = Json.list(value.get("n"));
                if (rows != null)
                    for (Object r : rows) clip.getContent().getEventTimeline().addEvent(createNote(Json.list(r)));
            }
            clip.setTime(start);
            clip.setDuration(dur);
            writeClipProps(clip, props);
            track.clipTimeline().addEvent(clip);
            return;
        }

        if (Math.abs(existing.getDuration() - dur) > 1e-6) existing.setDuration(dur);
        if ("midi".equals(kind)) writeNotes(existing, Json.list(value.get("n")));
        writeClipProps(existing, props);
        if ("audio".equals(kind) && audioNote(existing) instanceof AudioNote note) writeAudioProps(note, props, len);
    }

    // ------------------------------------------------ audio (warp / pitch)

    // Live のワープモード（Clip.warp_mode の番号）⇔ Bitwig のストレッチモード。できるだけ性質の近いものを選ぶ
    private static final WarpMode[] LIVE_TO_BITWIG = {
            WarpMode.SLICE,           // 0 Beats（トランジェントで切って並べる）
            WarpMode.ELASTIQUE_SOLO,  // 1 Tones（単音向け）
            WarpMode.STRETCH,         // 2 Texture（グラニュラー）
            WarpMode.REPITCH,         // 3 Re-Pitch
            WarpMode.ELASTIQUE,       // 4 Complex
            WarpMode.SLICE,           // 5 REX
            WarpMode.ELASTIQUE_PRO,   // 6 Complex Pro
    };

    private static int bitwigToLive(WarpMode mode) {
        return switch (mode) {
            case SLICE -> 0;
            case ELASTIQUE_SOLO -> 1;
            case REPITCH -> 3;
            case ELASTIQUE, ELASTIQUE_ECO -> 4;
            case ELASTIQUE_PRO -> 6;
            default -> 2;  // STRETCH, STRETCH_HD, CYCLIC
        };
    }

    private static AudioNote audioNote(Clip c) {
        for (Object o : c.getContent().getEventTimeline().getEvents())
            if (o instanceof AudioNote an) return an;
        return null;
    }

    /**
     * ワープとピッチを Live と同じ形で読む:
     *   warp（ワープ有無、Bitwig の Raw がオフ）、wm（ワープモード、Live の番号）、
     *   wmk（ワープマーカー [[クリップ内の拍, ファイルの秒], ...]）、pc / pf（ピッチ: 半音 / セント）
     */
    private void readAudioProps(AudioNote an, Map<String, Object> props) {
        WarpMode mode = an.getWarpMode();
        boolean warping = mode != null && mode != WarpMode.RAW;
        props.put("warp", warping);
        if (warping) {
            props.put("wm", (double) bitwigToLive(mode));
            List<Object> markers = new ArrayList<>();
            var events = an.getWarpEvents();
            if (events != null)
                for (int i = 0; i < events.getWarpMarkerCount(); i++) {
                    var m = events.getWarpMarkerAt(i);
                    markers.add(List.of(round(an.getTime() + m.getBeatTime()), round(m.getSeconds(), 6)));
                }
            if (markers.size() >= 2) props.put("wmk", markers);
        }
        double transpose = in.readTranspose(an);
        double coarse = Math.rint(transpose);
        props.put("pc", coarse);
        props.put("pf", round((transpose - coarse) * 100.0, 2));
    }

    private void writeAudioProps(AudioNote an, Map<String, Object> props, double len) {
        boolean warping = !props.containsKey("warp") || Json.bool(props.get("warp"));
        WarpMode mode = WarpMode.RAW;
        if (warping) {
            int wm = (int) Json.num(props.get("wm"), -1);
            mode = wm >= 0 && wm < LIVE_TO_BITWIG.length ? LIVE_TO_BITWIG[wm] : an.getWarpMode() == WarpMode.RAW ? WarpMode.STRETCH : an.getWarpMode();
        }
        if (an.getWarpMode() != mode) an.setWarpMode(mode);

        List<Object> wanted = Json.list(props.get("wmk"));
        if (warping && wanted != null && wanted.size() >= 2) {
            // Live のマーカーの拍はクリップの中の位置。Bitwig ではオーディオイベントの頭からの位置なので、
            // 最初のマーカーの位置にイベントを置き、そこからの差にする
            List<double[]> ms = new ArrayList<>();
            for (Object o : wanted) {
                List<Object> m = Json.list(o);
                if (m != null && m.size() >= 2) ms.add(new double[] { Json.num(m.get(0), 0), Json.num(m.get(1), 0) });
            }
            ms.sort(Comparator.comparingDouble(m -> m[0]));
            double b0 = ms.get(0)[0];
            List<Object> current = new ArrayList<>();
            Map<String, Object> now = new LinkedHashMap<>();
            readAudioProps(an, now);
            if (now.get("wmk") != null) current = Json.list(now.get("wmk"));
            if (!Json.same(current, normalize(ms))) {
                var markers = factory.createWarpEvents();
                for (double[] m : ms) markers.addWarpEvent(m[0] - b0, m[1]);
                if (Math.abs(an.getTime() - b0) > 1e-6) an.setTime(b0);
                double end = Math.max(ms.get(ms.size() - 1)[0], len);
                if (end - b0 > 1e-3 && Math.abs(an.getDuration() - (end - b0)) > 1e-6) an.setDuration(end - b0);
                an.setWarpEvents(markers);
            }
        }

        if (props.containsKey("pc") || props.containsKey("pf")) {
            double transpose = Json.num(props.get("pc"), 0) + Json.num(props.get("pf"), 0) / 100.0;
            if (Math.abs(in.readTranspose(an) - transpose) > 1e-4) an.getTranspose().setValue(transpose, Unit.SEMITONES);
        }
    }

    private static List<Object> normalize(List<double[]> markers) {
        List<Object> out = new ArrayList<>();
        for (double[] m : markers) out.add(List.of(round(m[0]), round(m[1], 6)));
        return out;
    }

    private static boolean sameFile(String a, String b) {
        if (a == null || b == null) return a == b;
        return new File(a).getAbsoluteFile().toPath().normalize().toString()
                .equalsIgnoreCase(new File(b).getAbsoluteFile().toPath().normalize().toString());
    }

    private void writeClipProps(Clip clip, Map<String, Object> props) {
        if (props.get("name") instanceof String name && !name.equals(clip.getTitle())) clip.setTitle(name);
        if (props.get("color") instanceof Number c && colorToInt(clip.getColor()) != c.intValue()) clip.setColor(intToColor(c.intValue()));
        if (props.containsKey("looping")) {
            boolean looping = Json.bool(props.get("looping"));
            if (clip.isLoopEnabled() != looping) clip.setIsLoopEnabled(looping);
        }
        boolean looping = props.containsKey("looping") ? Json.bool(props.get("looping")) : clip.isLoopEnabled();
        if (looping && props.get("ls") instanceof Number ls && props.get("le") instanceof Number le) {
            double start = ls.doubleValue(), length = Math.max(le.doubleValue() - start, 1.0 / 64);
            if (Math.abs(clip.getLoopStart() - start) > 1e-6) clip.setLoopStart(start);
            if (Math.abs(clip.getLoopDuration() - length) > 1e-6) clip.setLoopDuration(length);
        }
        if (props.get("sm") instanceof Number sm && Math.abs(clip.getPlayStartOffset() - sm.doubleValue()) > 1e-6)
            clip.setPlayStartOffset(sm.doubleValue());
    }

    private InstrumentNote createNote(List<Object> r) {
        List<Object> row = fullRow(r);
        InstrumentNote note = factory.createInstrumentNote(0, (int) Json.num(row.get(0), 60), Json.num(row.get(1), 0),
                Math.max(Json.num(row.get(2), 0.25), 1.0 / 1024), Json.num(row.get(3), 100) / 127.0, Json.num(row.get(7), 64) / 127.0);
        if (Json.bool(row.get(4))) note.setIsMuted(true);
        double chance = Json.num(row.get(5), 1.0);
        if (chance < 1.0) note.setChance(chance);
        return note;
    }

    private static List<Object> fullRow(List<Object> r) {
        List<Object> row = new ArrayList<>(r == null ? List.of() : r);
        Object[] defaults = { 60.0, 0.0, 0.25, 100.0, 0.0, 1.0, 0.0, 64.0 };
        while (row.size() < 8) row.add(defaults[row.size()]);
        return row;
    }

    private static String noteKey(double pitch, double time, double dur) {
        return (int) pitch + "@" + round(time) + "+" + round(dur);
    }

    /**
     * クリップのノートを rows の状態にする。変わっていないノートはそのまま残す
     * （Bitwig のノートエクスプレッション・MPE が消えないように）。
     */
    private void writeNotes(Clip clip, List<Object> rows) throws ReflectiveOperationException {
        Map<String, Deque<List<Object>>> want = new LinkedHashMap<>();
        if (rows != null)
            for (Object o : rows) {
                List<Object> r = fullRow(Json.list(o));
                want.computeIfAbsent(noteKey(Json.num(r.get(0), 0), Json.num(r.get(1), 0), Json.num(r.get(2), 0)),
                        k -> new ArrayDeque<>()).add(r);
            }
        EventTimeline timeline = clip.getContent().getEventTimeline();
        List<Object> remove = new ArrayList<>();
        for (Object o : timeline.getEvents()) {
            if (!(o instanceof InstrumentNote n)) continue;
            Deque<List<Object>> bucket = want.get(noteKey(n.getKey(), n.getTime(), n.getDuration()));
            if (bucket == null || bucket.isEmpty()) {
                remove.add(n);
                continue;
            }
            List<Object> r = bucket.poll();
            double vel = Json.num(r.get(3), 100) / 127.0;
            if (Math.abs(n.getOnVelocity() - vel) > 0.5 / 127.0) n.setOnVelocity(vel);
            boolean mute = Json.bool(r.get(4));
            if (n.isMuted() != mute) n.setIsMuted(mute);
            double release = Json.num(r.get(7), 64) / 127.0;
            if (Math.abs(n.getOffVelocity() - release) > 0.5 / 127.0) n.setOffVelocity(release);
        }
        in.deleteEvents(remove);
        for (Deque<List<Object>> bucket : want.values())
            for (List<Object> r : bucket) timeline.addEvent(createNote(r));
    }

    // ----------------------------------------------------------- helpers

    private void warnOnce(String code, String message) {
        if (warned.add(code)) out.warn(message);
    }

    static double round(double x) {
        return round(x, 5);
    }

    static double round(double x, int digits) {
        double f = Math.pow(10, digits);
        double r = Math.rint(x * f) / f;
        return r == 0 ? 0.0 : r;  // -0.0 を 0.0 に
    }

    /** Live の _time_key と同じ（"%.4f" から末尾の 0 と . を取る）。 */
    static String timeKey(double t) {
        String s = String.format(Locale.ROOT, "%.4f", t);
        if (s.contains(".")) {
            s = s.replaceAll("0+$", "");
            if (s.endsWith(".")) s = s.substring(0, s.length() - 1);
        }
        return s.equals("-0") ? "0" : s;
    }

    static int colorToInt(Color c) {
        if (c == null) return 0;
        return (c.getRed255() << 16) | (c.getGreen255() << 8) | c.getBlue255();
    }

    static Color intToColor(int rgb) {
        return Color.fromRGB255((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
    }
}
