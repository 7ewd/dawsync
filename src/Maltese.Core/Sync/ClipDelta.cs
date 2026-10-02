using System.Text.Json.Nodes;

namespace Maltese.Core.Sync;

/// <summary>
/// クリップの変更に付いてくる「変わったところ」（delta）をルームの状態に重ねる。
/// Live の Remote Script（model.py の with_delta / merge_delta）と Bitwig の拡張と同じ規則:
///   {"f": 変わった項目（"dur" や "p.name"）, "a": 足したノート, "d": 消したノート}
/// 2 人が同じクリップを同時に編集したとき、後から届いた人の古い値で先の人の編集を消さないため。
/// </summary>
public static class ClipDelta
{
    /// <summary>今の値 existing に incoming を反映した値（delta が無ければ incoming そのもの）。</summary>
    /// <remarks>
    /// 無いクリップへの delta（相手が消した・動かしたのと同時に、中身を編集していた）は反映しない（null を返す）。
    /// 反映すると、消した・動かしたはずのクリップが元の位置に戻ってきて、動かした先と 2 つになってしまう。
    /// Live・Bitwig の拡張も同じ規則で無視するので、全員が同じ結果になる。
    /// </remarks>
    public static JsonNode? Apply(JsonNode? existing, JsonNode? incoming)
    {
        if (existing is null && incoming is JsonObject patch && patch["delta"] is JsonObject)
            return null;
        // 種類（MIDI / オーディオ）が変わったクリップへの編集も反映しない（相手が作り直す前のクリップへの編集。
        // ノート全部（n）を付けずに送ってくるので、これで作り直すと空のクリップになってしまう）
        if (existing is JsonObject kept && incoming is JsonObject edit && edit["delta"] is JsonObject
            && (string?)kept["k"] != (string?)edit["k"])
            return kept;
        if (incoming is not JsonObject value || value["delta"] is not JsonObject delta || existing is not JsonObject current
            || (string?)current["k"] != (string?)value["k"])
            return Strip(incoming);

        var result = (JsonObject)current.DeepClone();
        result.Remove("delta");
        var props = result["p"] as JsonObject ?? new JsonObject();
        result["p"] = props;
        foreach (var field in (delta["f"] as JsonArray ?? []).Select(f => (string?)f).OfType<string>())
        {
            if (field.StartsWith("p.", StringComparison.Ordinal))
            {
                var name = field[2..];
                if (value["p"] is JsonObject incomingProps && incomingProps.ContainsKey(name))
                    props[name] = incomingProps[name]?.DeepClone();
                else
                    props.Remove(name);
            }
            else if (value.ContainsKey(field))
                result[field] = value[field]?.DeepClone();
            else
                result.Remove(field);
        }
        if (delta.ContainsKey("a") || delta.ContainsKey("d"))
            result["n"] = ApplyRows(current["n"] as JsonArray, delta["a"] as JsonArray, delta["d"] as JsonArray);
        return result;
    }

    /// <summary>
    /// 送るときは、ノートの変更（delta の a / d）があれば、ノート全部（n）は付けない。受け取る側は今のクリップに
    /// a / d を重ねるだけなので要らず、ノートの多いクリップをドラッグで編集すると、毎回全部送って回線が詰まるため。
    /// </summary>
    public static JsonNode? WithoutNotes(JsonNode? value)
    {
        if (value is not JsonObject o || o["delta"] is not JsonObject delta || !o.ContainsKey("n")
            || !(delta.ContainsKey("a") || delta.ContainsKey("d")))
            return value;
        var copy = (JsonObject)o.DeepClone();
        copy.Remove("n");
        return copy;
    }

    private static JsonNode? Strip(JsonNode? value)
    {
        if (value is not JsonObject o || !o.ContainsKey("delta")) return value;
        var copy = (JsonObject)o.DeepClone();
        copy.Remove("delta");
        return copy;
    }

    private static readonly double[] Defaults = [60, 0, 0.25, 100, 0, 1, 0, 64];

    private static double[] FullRow(JsonNode? row)
    {
        var items = (row as JsonArray ?? []).ToList();
        var values = new double[Math.Max(items.Count, Defaults.Length)];
        for (var i = 0; i < values.Length; i++)
            values[i] = i < items.Count && items[i] is JsonValue v && v.TryGetValue<double>(out var d) ? d
                : i < Defaults.Length ? Defaults[i] : 0;
        return values;
    }

    // 同じノートか。REAPER は 1 拍 960 の目盛りに丸めるので、その 1 目盛りくらいの差は同じとみなす
    // （Live・Bitwig・REAPER のスクリプトと同じ規則。違うと、ルームの状態にだけ消えないノートが残る）
    private const double NoteTolerance = 0.002;

    private static bool SameNote(double[] a, double[] b) =>
        (int)a[0] == (int)b[0] && Math.Abs(a[1] - b[1]) <= NoteTolerance && Math.Abs(a[2] - b[2]) <= NoteTolerance;

    private static JsonArray ApplyRows(JsonArray? rows, JsonArray? added, JsonArray? removed)
    {
        var result = (rows ?? []).Select(FullRow).ToList();
        foreach (var r in (removed ?? []).Select(FullRow))
        {
            var i = result.FindIndex(x => SameNote(x, r));
            if (i >= 0) result.RemoveAt(i);
        }
        foreach (var r in (added ?? []).Select(FullRow))
        {
            result.RemoveAll(x => SameNote(x, r));
            result.Add(r);
        }
        result.Sort((a, b) =>
        {
            for (var i = 0; i < a.Length; i++)
            {
                var c = a[i].CompareTo(b[i]);
                if (c != 0) return c;
            }
            return 0;
        });
        return new JsonArray(result.Select(r => (JsonNode)new JsonArray(r.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())).ToArray());
    }
}
