package com.dawsync.bitwig;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

/**
 * テンポ・拍子の変化（tempo_map / sig_map）の共通の形。Live の model.py・REAPER の model.lua と同じ規則
 * （tests/timing で同じ例を確かめる）。
 *
 *   tempo_map の行は [拍, BPM] か [拍, BPM, 1]。3 つ目が 1 の点からは次の点の BPM まで直線で変わる（ランプ）。
 *   無い・0 なら次の点まで同じ BPM（段差）。同じ拍に 2 点あるときは、前の点がランプの行き先、後の点がその拍からの値。
 *   sig_map の行は [拍, 分子, 分母]（その拍から）。
 *
 * Bitwig の API の外（DAWproject・Json）だけを使う（Bitwig が無くてもテストできるように）。
 */
final class TimingMaps {
    static final double TEMPO_EPS = 0.002;

    private TimingMaps() { }

    private static double round(double x, int digits) {
        double f = Math.pow(10, digits);
        double r = Math.rint(x * f) / f;
        return r == 0 ? 0.0 : r;
    }

    private static boolean finite(double x) {
        return !Double.isNaN(x) && !Double.isInfinite(x);
    }

    private static boolean flag(Object v) {
        return Json.bool(v);
    }

    /** テンポの点を決まった形にする（並べる・意味の無い点を除く・拍 0 から始める）。読めなければ空。 */
    static List<List<Object>> normalizeTempo(List<?> rows) {
        List<double[]> points = new ArrayList<>();
        if (rows != null)
            for (Object o : rows) {
                List<Object> row = Json.list(o);
                if (row == null || row.size() < 2 || !(row.get(0) instanceof Number) || !(row.get(1) instanceof Number)) continue;
                double beat = Json.num(row.get(0), 0), bpm = Json.num(row.get(1), 0);
                if (!finite(beat) || !finite(bpm) || bpm <= 0) continue;
                points.add(new double[] { round(Math.max(0, beat), 5), round(bpm, 3), row.size() > 2 && flag(row.get(2)) ? 1 : 0 });
            }
        List<List<Object>> result = new ArrayList<>();
        if (points.isEmpty()) return result;
        points.sort(Comparator.comparingDouble(p -> p[0]));  // 同じ拍の点は元の順のまま（List.sort は安定）
        if (points.get(0)[0] > 0) points.add(0, new double[] { 0.0, points.get(0)[1], 0 });
        List<double[]> out = new ArrayList<>();
        for (int n = 0; n < points.size(); n++) {
            double[] p = points.get(n);
            double[] next = n + 1 < points.size() ? points.get(n + 1) : null;
            boolean ramp = p[2] != 0 && next != null && next[0] > p[0] && next[1] != p[1];
            double[] current = { p[0], p[1], ramp ? 1 : 0 };
            double[] last = out.isEmpty() ? null : out.get(out.size() - 1);
            if (last != null && last[0] == p[0]) {
                // 同じ拍の点。直前の点がランプの行き先なら残して跳ぶ。そうでなければ跳ぶ前の値は意味が無いので置き換える
                double[] before = out.size() >= 2 ? out.get(out.size() - 2) : null;
                if (before != null && before[2] != 0 && before[0] < p[0] && last[1] != p[1]) out.add(current);
                else out.set(out.size() - 1, current);
                continue;
            }
            if (last != null && last[2] == 0 && last[1] == p[1] && !ramp) continue;  // 前と同じ BPM が続くだけ
            out.add(current);
        }
        // ランプの途中に入った点で、前後を結ぶ線の上にあるものは除く
        int n = 1;
        while (n < out.size() - 1) {
            double[] a = out.get(n - 1), q = out.get(n), c = out.get(n + 1);
            if (a[2] != 0 && q[2] != 0 && a[0] < q[0] && q[0] < c[0]
                    && Math.abs(a[1] + (c[1] - a[1]) * (q[0] - a[0]) / (c[0] - a[0]) - q[1]) <= TEMPO_EPS) {
                out.remove(n);
                continue;
            }
            n++;
        }
        out.get(out.size() - 1)[2] = 0;
        for (double[] p : out) result.add(p[2] != 0 ? List.of(p[0], p[1], 1.0) : List.of(p[0], p[1]));
        return result;
    }

    private static int index(List<List<Object>> rows, double beat) {
        int index = 0;
        for (int n = 0; n < rows.size(); n++) {
            if (Json.num(rows.get(n).get(0), 0) <= beat + 1e-9) index = n;
            else break;
        }
        return index;
    }

    /** 決まった形のテンポマップの、拍 beat での BPM（ちょうど点の上なら、その点からの値）。 */
    static double tempoAt(List<List<Object>> rows, double beat) {
        int i = index(rows, beat);
        List<Object> row = rows.get(i);
        double b0 = Json.num(row.get(0), 0), v0 = Json.num(row.get(1), 120);
        if (b0 <= beat + 1e-9 && row.size() > 2 && flag(row.get(2)) && i + 1 < rows.size()) {
            double b1 = Json.num(rows.get(i + 1).get(0), 0), v1 = Json.num(rows.get(i + 1).get(1), v0);
            if (b1 > b0) return v0 + (v1 - v0) * (beat - b0) / (b1 - b0);
        }
        return v0;
    }

    static boolean tempoVaries(List<?> rows) {
        return normalizeTempo(rows).size() > 1;
    }

    /** 2 つのテンポマップが、どこでもほぼ同じ BPM になるか（書き方の違いは気にしない）。 */
    static boolean tempoClose(List<?> x, List<?> y, double tol) {
        List<List<Object>> a = normalizeTempo(x), b = normalizeTempo(y);
        if (a.isEmpty() || b.isEmpty()) return a.isEmpty() && b.isEmpty();
        java.util.TreeSet<Double> beats = new java.util.TreeSet<>();
        for (List<Object> r : a) beats.add(Json.num(r.get(0), 0));
        for (List<Object> r : b) beats.add(Json.num(r.get(0), 0));
        List<Double> samples = new ArrayList<>();
        Double previous = null;
        for (double beat : beats) {
            samples.add(beat);
            if (beat > 0) samples.add(beat - 0.001);
            if (previous != null) samples.add((previous + beat) / 2.0);
            previous = beat;
        }
        samples.add(beats.last() + 1.0);
        for (double s : samples)
            if (Math.abs(tempoAt(a, s) - tempoAt(b, s)) > tol) return false;
        return true;
    }

    /** 拍 beat を含む区間（その前の最後の点）の BPM を変える。 */
    static List<List<Object>> setTempoAt(List<?> rows, double beat, double bpm) {
        List<List<Object>> normalized = normalizeTempo(rows);
        if (normalized.isEmpty()) return normalizeTempo(List.of(List.of(0.0, bpm)));
        List<Object> edited = new ArrayList<>();
        int i = index(normalized, beat);
        for (int n = 0; n < normalized.size(); n++) {
            List<Object> row = new ArrayList<>(normalized.get(n));
            if (n == i) row.set(1, bpm);
            edited.add(row);
        }
        return normalizeTempo(edited);
    }

    /** 拍子の変わり目を決まった形にする。読めなければ空。 */
    static List<List<Object>> normalizeSig(List<?> rows) {
        List<double[]> points = new ArrayList<>();
        if (rows != null)
            for (Object o : rows) {
                List<Object> row = Json.list(o);
                if (row == null || row.size() < 3 || !(row.get(0) instanceof Number) || !(row.get(1) instanceof Number)
                        || !(row.get(2) instanceof Number)) continue;
                double beat = Json.num(row.get(0), 0);
                double num = Math.rint(Json.num(row.get(1), 0)), den = Math.rint(Json.num(row.get(2), 0));
                if (!finite(beat) || !finite(num) || !finite(den) || num < 1 || den < 1) continue;
                points.add(new double[] { round(Math.max(0, beat), 5), num, den });
            }
        List<List<Object>> result = new ArrayList<>();
        if (points.isEmpty()) return result;
        points.sort(Comparator.comparingDouble(p -> p[0]));
        if (points.get(0)[0] > 0) points.add(0, new double[] { 0.0, points.get(0)[1], points.get(0)[2] });
        List<double[]> sameBeat = new ArrayList<>();
        for (double[] p : points) {
            if (!sameBeat.isEmpty() && sameBeat.get(sameBeat.size() - 1)[0] == p[0]) sameBeat.set(sameBeat.size() - 1, p);
            else sameBeat.add(p);
        }
        double[] last = null;
        for (double[] p : sameBeat) {
            if (last != null && last[1] == p[1] && last[2] == p[2]) continue;
            result.add(List.of(p[0], p[1], p[2]));
            last = p;
        }
        return result;
    }

    /** 拍 beat での拍子 {分子, 分母}。 */
    static int[] sigAt(List<List<Object>> rows, double beat) {
        int[] current = null;
        for (List<Object> row : rows) {
            if (current == null || Json.num(row.get(0), 0) <= beat + 1e-9)
                current = new int[] { (int) Json.num(row.get(1), 4), (int) Json.num(row.get(2), 4) };
            else break;
        }
        return current == null ? new int[] { 4, 4 } : current;
    }

    static boolean sigVaries(List<?> rows) {
        return normalizeSig(rows).size() > 1;
    }

    static List<List<Object>> setSigAt(List<?> rows, double beat, int num, int den) {
        List<List<Object>> normalized = normalizeSig(rows);
        if (normalized.isEmpty()) return normalizeSig(List.of(List.of(0.0, (double) num, (double) den)));
        List<Object> edited = new ArrayList<>();
        int i = 0;
        for (int n = 0; n < normalized.size(); n++)
            if (Json.num(normalized.get(n).get(0), 0) <= beat + 1e-9) i = n;
        for (int n = 0; n < normalized.size(); n++) {
            List<Object> row = new ArrayList<>(normalized.get(n));
            if (n == i) {
                row.set(1, (double) num);
                row.set(2, (double) den);
            }
            edited.add(row);
        }
        return normalizeSig(edited);
    }

    /**
     * テンポマップを、点と点の間を直線で結ぶ点の並び（Bitwig のオートメーション）にする。
     * 段差は同じ時刻の 2 点（前の値・次の値）になる。返すのは {拍, BPM} の並び（拍の順）。
     */
    static List<double[]> toLinearPoints(List<List<Object>> rows) {
        List<double[]> out = new ArrayList<>();
        for (int n = 0; n < rows.size(); n++) {
            List<Object> row = rows.get(n);
            double beat = Json.num(row.get(0), 0), bpm = Json.num(row.get(1), 120);
            out.add(new double[] { beat, bpm });
            boolean ramp = row.size() > 2 && flag(row.get(2));
            if (!ramp && n + 1 < rows.size()) {
                double next = Json.num(rows.get(n + 1).get(0), beat);
                if (next > beat) out.add(new double[] { next, bpm });  // 次の点まで同じ値（そこで跳ぶ）
            }
        }
        return out;
    }
}
