package com.dawsync.bitwig;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * test_timing.py から呼ばれる。vectors.json の例を Bitwig 用の TimingMaps で計算して、結果を JSON で書き出す
 * （比べるのは test_timing.py）。Bitwig が無くても動く（TimingMaps と Json だけを使う）。
 */
public final class TimingMapsCheck {
    public static void main(String[] args) throws Exception {
        Map<String, Object> vectors = Json.map(Json.parse(Files.readString(Path.of(args[0]), StandardCharsets.UTF_8)));
        Map<String, Object> out = new LinkedHashMap<>();
        List<Object> tempo = new ArrayList<>();
        for (Object o : Json.list(vectors.get("tempo"))) {
            Map<String, Object> c = Json.map(o);
            List<List<Object>> rows = TimingMaps.normalizeTempo(Json.list(c.get("in")));
            List<Object> at = new ArrayList<>();
            List<Object> samples = Json.list(c.get("at"));
            if (samples != null)
                for (Object s : samples) at.add(rows.isEmpty() ? null : TimingMaps.tempoAt(rows, Json.num(Json.list(s).get(0), 0)));
            tempo.add(Json.obj("out", rows, "at", at));
        }
        out.put("tempo", tempo);
        List<Object> sig = new ArrayList<>();
        for (Object o : Json.list(vectors.get("sig"))) {
            Map<String, Object> c = Json.map(o);
            List<List<Object>> rows = TimingMaps.normalizeSig(Json.list(c.get("in")));
            List<Object> at = new ArrayList<>();
            List<Object> samples = Json.list(c.get("at"));
            if (samples != null)
                for (Object s : samples) {
                    int[] v = TimingMaps.sigAt(rows, Json.num(Json.list(s).get(0), 0));
                    at.add(List.of((double) v[0], (double) v[1]));
                }
            sig.add(Json.obj("out", rows, "at", at));
        }
        out.put("sig", sig);
        List<Object> close = new ArrayList<>();
        for (Object o : Json.list(vectors.get("close"))) {
            Map<String, Object> c = Json.map(o);
            close.add(TimingMaps.tempoClose(Json.list(c.get("a")), Json.list(c.get("b")), 0.01));
        }
        out.put("close", close);
        List<Object> set = new ArrayList<>();
        for (Object o : Json.list(vectors.get("set_tempo_at"))) {
            Map<String, Object> c = Json.map(o);
            set.add(TimingMaps.setTempoAt(Json.list(c.get("in")), Json.num(c.get("beat"), 0), Json.num(c.get("bpm"), 120)));
        }
        out.put("set_tempo_at", set);
        // Bitwig に書き込む点の並び（段差は同じ時刻の 2 点）
        List<Object> linear = new ArrayList<>();
        for (Object o : Json.list(vectors.get("tempo"))) {
            List<Object> pts = new ArrayList<>();
            List<List<Object>> rows = TimingMaps.normalizeTempo(Json.list(Json.map(o).get("in")));
            for (double[] p : TimingMaps.toLinearPoints(rows)) pts.add(List.of(p[0], p[1]));
            linear.add(pts);
        }
        out.put("linear", linear);
        System.out.println(Json.write(out));
    }
}
