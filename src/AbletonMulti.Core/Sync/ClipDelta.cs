using System.Text.Json.Nodes;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// クリップの変更に付いてくる「変わったところ」（delta）をルームの状態に重ねる。
/// Live の Remote Script（model.py の with_delta / merge_delta）と Bitwig の拡張と同じ規則:
///   {"f": 変わった項目（"dur" や "p.name"）, "a": 足したノート, "d": 消したノート}
/// 2 人が同じクリップを同時に編集したとき、後から届いた人の古い値で先の人の編集を消さないため。
/// </summary>
public static class ClipDelta
{
    /// <summary>今の値 existing に incoming を反映した値（delta が無ければ incoming そのもの）。</summary>
    public static JsonNode? Apply(JsonNode? existing, JsonNode? incoming)
    {
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
        var values = (row as JsonArray ?? []).Select(v => v is null ? 0 : v.GetValue<double>()).ToList();
        while (values.Count < Defaults.Length) values.Add(Defaults[values.Count]);
        return values.ToArray();
    }

    private static (int, double, double) Ident(double[] row) =>
        ((int)row[0], Math.Round(row[1], 5), Math.Round(row[2], 5));

    private static JsonArray ApplyRows(JsonArray? rows, JsonArray? added, JsonArray? removed)
    {
        var result = (rows ?? []).Select(FullRow).ToList();
        foreach (var r in (removed ?? []).Select(FullRow))
        {
            var i = result.FindIndex(x => Ident(x) == Ident(r));
            if (i >= 0) result.RemoveAt(i);
        }
        foreach (var r in (added ?? []).Select(FullRow))
        {
            result.RemoveAll(x => Ident(x) == Ident(r));
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
