package com.dawsync.bitwig;

import java.lang.reflect.Field;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.util.Collection;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.ConcurrentHashMap;

/**
 * Bitwig の内部（公開 API に無いもの）を呼ぶところ。名前は難読化されているので、Bitwig のバージョンが
 * 変わると壊れることがある。見つからないときは例外を投げ、呼び出し側で「この機能は使えない」扱いにする。
 *
 * 調べた内容（Bitwig Studio 6.0）:
 *   ControlSurfaceObject.getTarget()           公開 API の Project → 内部のプロジェクト（Dig）
 *   ControlSurfaceObject.exec(Runnable)        ドキュメントのスレッド（main）で実行する
 *   AjW.r3B(String, boolean) / AjW.r3B(boolean) Undo の 1 まとまりの開始 / 終了（公開 API の deleteObject と同じ使い方）
 *   AjW.Xzy(Collection)                        トラックを消す（公開 API の deleteObject と同じ）
 *   cG.r3B(Collection)                         タイムラインからクリップ・ノートを消す（"Delete Events"）。
 *                                              ノートは音程ごとのレーン（aQm、ノートの親 UO1.J_()）が cG なので、そこから消す
 *   jIV.r3B(List, int, wpI, Owm)               トラックグループの index 番目にトラックを入れる
 *   jIV.r3B(jIV, List, int, int)               別のグループからトラックを移す（"Move Tracks Between Track Groups"）
 *   jIV.Hm(Dig)                                グループ解除（中身を親へ出してグループを消す）
 *   UO1.r3B(TrW) / UO1.Xzy(TrW)                変更の通知を受け取る / やめる（ルートのトラックグループまで伝わる）
 */
final class Internals {
    private final ClassLoader cl;
    private final Class<?> ajw;
    private final Class<?> uo1;
    private final Class<?> trackGroup;
    private final Class<?> listenerType;
    private final Method begin;
    private final Method end;
    private final Method delete;
    private final Method deleteEvents;
    private final Class<?> eventTimeline;
    private final Method parent;
    private final Method insertTracks;
    private Method moveTracks;
    private Method ungroup;
    private final Method addListener;
    private final Method removeListener;

    Internals(Object dig) throws ReflectiveOperationException {
        cl = dig.getClass().getClassLoader();
        ajw = cl.loadClass("com.bitwig.flt.document.core.iface.AjW");
        uo1 = cl.loadClass("com.bitwig.ramona.core.UO1");
        trackGroup = cl.loadClass("com.bitwig.flt.document.core.master.jIV");
        listenerType = cl.loadClass("com.bitwig.ramona.core.TrW");
        begin = ajw.getMethod("r3B", String.class, boolean.class);
        end = ajw.getMethod("r3B", boolean.class);
        delete = ajw.getMethod("Xzy", Collection.class);
        eventTimeline = cl.loadClass("cG");
        deleteEvents = eventTimeline.getMethod("r3B", Collection.class);
        parent = uo1.getMethod("J_");
        insertTracks = trackGroup.getMethod("r3B", List.class, int.class,
                cl.loadClass("com.bitwig.base.async.wpI"), cl.loadClass("Owm"));
        addListener = uo1.getMethod("r3B", listenerType);
        removeListener = uo1.getMethod("Xzy", listenerType);
        try {
            moveTracks = trackGroup.getDeclaredMethod("r3B", trackGroup, List.class, int.class, int.class);
            moveTracks.setAccessible(true);
        } catch (ReflectiveOperationException e) {
            moveTracks = null;
        }
        try {
            ungroup = trackGroup.getDeclaredMethod("Hm", cl.loadClass("com.bitwig.flt.document.core.master.Dig"));
            ungroup.setAccessible(true);
        } catch (ReflectiveOperationException e) {
            ungroup = null;
        }
    }

    static Object projectTarget(Object projectProxy) throws ReflectiveOperationException {
        return find(projectProxy.getClass(), "getTarget", 0).invoke(projectProxy);
    }

    static void exec(Object projectProxy, Runnable r) throws ReflectiveOperationException {
        find(projectProxy.getClass(), "exec", 1).invoke(projectProxy, r);
    }

    /** 公開 API のラッパー（ApiObject）→ 内部のオブジェクト。 */
    static Object target(Object api) {
        try {
            return api.getClass().getMethod("getTarget").invoke(api);
        } catch (ReflectiveOperationException e) {
            throw new IllegalStateException(e);
        }
    }

    boolean isTrackGroup(Object internal) {
        return trackGroup.isInstance(internal);
    }

    void begin(Object dig, String name) throws ReflectiveOperationException {
        call(begin, dig, name, false);
    }

    void end(Object dig) throws ReflectiveOperationException {
        call(end, dig, false);
    }

    void delete(Object dig, Collection<?> internals) throws ReflectiveOperationException {
        if (!internals.isEmpty()) call(delete, dig, internals);
    }

    /** events（公開 API のクリップやノート）を、それぞれが入っているタイムラインから消す。 */
    void deleteEvents(Collection<?> events) throws ReflectiveOperationException {
        Map<Object, List<Object>> byTimeline = new java.util.IdentityHashMap<>();
        for (Object e : events) {
            Object internal = target(e);
            Object timeline = call(parent, internal);
            while (timeline != null && !eventTimeline.isInstance(timeline)) timeline = call(parent, timeline);
            if (timeline == null) throw new IllegalStateException("no timeline for " + internal);
            byTimeline.computeIfAbsent(timeline, k -> new java.util.ArrayList<>()).add(internal);
        }
        for (Map.Entry<Object, List<Object>> e : byTimeline.entrySet()) call(deleteEvents, e.getKey(), e.getValue());
    }

    /**
     * オーディオイベントのトランスポーズ（半音、小数あり）を読む。公開 API は書くだけなので内部から読む:
     * note.A1o()（エクスプレッションの入れ物）→ Xzy(N2l.Xzy, false)（トランスポーズのレーン、無ければ null）
     * → r3B()（点のリスト）→ 最初の点の jYK.Xzy（値。単位は半音のまま）
     */
    double readTranspose(Object noteApi) {
        try {
            Object note = target(noteApi);
            Method exprsOf = cached(EXPRS, note.getClass(), k -> Optional.of(find(k, "A1o", 0))).orElse(null);
            if (exprsOf == null) return 0;
            Object exprs = exprsOf.invoke(note);
            Object kind = transposeKind();
            Method laneOf = cached(LANE, exprs.getClass(), k -> Optional.of(k.getMethod("Xzy", kind.getClass(), boolean.class))).orElse(null);
            if (laneOf == null) return 0;
            Object lane = laneOf.invoke(exprs, kind, false);
            if (lane == null) return 0;
            Optional<Method> points = cached(POINTS, lane.getClass(), c -> {
                for (Class<?> k = c; k != null; k = k.getSuperclass())
                    if (k.getSimpleName().equals("gCK"))
                        for (Method m : k.getDeclaredMethods())
                            if (m.getName().equals("r3B") && m.getParameterCount() == 0 && List.class.isAssignableFrom(m.getReturnType())) {
                                m.setAccessible(true);
                                return Optional.of(m);
                            }
                return Optional.empty();
            });
            if (points.isEmpty()) return 0;
            List<?> list = (List<?>) points.get().invoke(lane);
            if (list == null || list.isEmpty()) return 0;
            Object point = list.get(0);
            Optional<Field> value = cached(VALUE, point.getClass(), c -> {
                for (Class<?> k = c; k != null; k = k.getSuperclass())
                    if (k.getSimpleName().equals("jYK")) {
                        Field f = k.getDeclaredField("Xzy");
                        f.setAccessible(true);
                        return Optional.of(f);
                    }
                return Optional.empty();
            });
            if (value.isPresent()) return ((Number) value.get().get(point)).doubleValue();
        } catch (ReflectiveOperationException | RuntimeException e) {
            return 0;
        }
        return 0;
    }

    // readTranspose で使う内部のメソッドなど。オーディオクリップごとに読むたびに探すと重いので、クラスごとに覚えておく
    private static final Map<Class<?>, Optional<Method>> EXPRS = new ConcurrentHashMap<>();
    private static final Map<Class<?>, Optional<Method>> LANE = new ConcurrentHashMap<>();
    private static final Map<Class<?>, Optional<Method>> POINTS = new ConcurrentHashMap<>();
    private static final Map<Class<?>, Optional<Field>> VALUE = new ConcurrentHashMap<>();
    private static volatile Object[] sKind;  // {クラスローダー, N2l.Xzy}

    private interface Lookup<T> {
        Optional<T> find(Class<?> k) throws ReflectiveOperationException;
    }

    /** 見つからなかったことも覚えておく（毎回探し直さないように）。 */
    private static <T> Optional<T> cached(Map<Class<?>, Optional<T>> cache, Class<?> k, Lookup<T> lookup) {
        Optional<T> v = cache.get(k);
        if (v == null) {
            try {
                v = lookup.find(k);
            } catch (ReflectiveOperationException | RuntimeException e) {
                v = Optional.empty();
            }
            cache.put(k, v);
        }
        return v;
    }

    /** トランスポーズのエクスプレッションの種類（N2l.Xzy）。 */
    private Object transposeKind() throws ReflectiveOperationException {
        Object[] cache = sKind;
        if (cache != null && cache[0] == cl) return cache[1];
        Object kind = cl.loadClass("N2l").getField("Xzy").get(null);
        sKind = new Object[] { cl, kind };
        return kind;
    }

    void insertTrack(Object group, Object track, int index) throws ReflectiveOperationException {
        call(insertTracks, group, List.of(track), index, null, null);
    }

    boolean canMoveTracks() {
        return moveTracks != null;
    }

    void moveTrack(Object toGroup, Object fromGroup, Object track, int index) throws ReflectiveOperationException {
        Object effectIndex = find(toGroup.getClass(), "eV", 0).invoke(toGroup);
        call(moveTracks, toGroup, fromGroup, List.of(track), index, effectIndex);
    }

    boolean canUngroup() {
        return ungroup != null;
    }

    void ungroup(Object group, Object dig) throws ReflectiveOperationException {
        call(ungroup, group, dig);
    }

    /** 変更があったら onChange を呼ぶリスナーを付ける。戻り値を渡すと外せる。 */
    Object listen(Object internal, Runnable onChange) throws ReflectiveOperationException {
        Object proxy = Proxy.newProxyInstance(cl, new Class<?>[] { listenerType }, (p, m, args) -> {
            if (m.getDeclaringClass() == Object.class) {
                return switch (m.getName()) {
                    case "hashCode" -> System.identityHashCode(p);
                    case "equals" -> p == args[0];
                    default -> "DawSyncListener";
                };
            }
            onChange.run();
            return defaultValue(m.getReturnType());
        });
        call(addListener, internal, proxy);
        return proxy;
    }

    /** 戻り値の型に合った「何もしない」値（プリミティブ型に null を返すと、呼んだ側で例外になる）。 */
    private static Object defaultValue(Class<?> type) {
        if (!type.isPrimitive() || type == void.class) return null;
        if (type == boolean.class) return false;
        if (type == char.class) return ' ';
        if (type == byte.class) return (byte) 0;
        if (type == short.class) return (short) 0;
        if (type == int.class) return 0;
        if (type == long.class) return 0L;
        if (type == float.class) return 0f;
        return 0.0;
    }

    void unlisten(Object internal, Object proxy) {
        try {
            call(removeListener, internal, proxy);
        } catch (Exception ignored) {
        }
    }

    private static Object call(Method m, Object target, Object... args) throws ReflectiveOperationException {
        try {
            return m.invoke(target, args);
        } catch (InvocationTargetException e) {
            Throwable cause = e.getCause();
            if (cause instanceof RuntimeException r) throw r;
            if (cause instanceof Error err) throw err;
            throw e;
        }
    }

    static Method find(Class<?> c, String name, int params) throws NoSuchMethodException {
        for (Class<?> k = c; k != null; k = k.getSuperclass())
            for (Method m : k.getDeclaredMethods())
                if (m.getName().equals(name) && m.getParameterCount() == params) {
                    m.setAccessible(true);
                    return m;
                }
        throw new NoSuchMethodException(c.getName() + "." + name);
    }
}
