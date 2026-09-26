using System.Globalization;
using System.Text.Json.Nodes;

namespace AbletonMulti.Core.Sync;

public enum ActivityKind { Self, Remote, System, Warning }

public sealed record ActivityEntry(DateTime Time, ActivityKind Kind, string Who, string Text, string? Key = null);

/// <summary>同期の操作（キーと値）を人が読める日本語にする。キーの意味は RemoteScript/model.py の先頭を参照。</summary>
public static class OpDescriber
{
    /// <param name="state">変更が反映される前のルームの状態（トラック名などを引くのに使う）</param>
    public static string Describe(Op op, IReadOnlyDictionary<string, JsonNode?> state)
    {
        var p = op.Key.Split('/');
        var v = op.Value;
        var existed = state.ContainsKey(op.Key);
        try
        {
            return p switch
            {
                ["tempo"] => $"テンポを {Num(v):0.##} BPM に変更",
                ["sig"] => $"拍子を {(int?)v?[0]}/{(int?)v?[1]} に変更",

                ["t", var id] => v is null ? $"トラック{Name(state, "t", id)}を削除"
                    : !existed ? $"{KindName((string?)v["k"])}トラックを追加"
                    : $"トラック{Name(state, "t", id)}を移動",
                ["r", var id] => v is null ? $"リターントラック{Name(state, "r", id)}を削除"
                    : !existed ? "リターントラックを追加" : $"リターントラック{Name(state, "r", id)}を移動",
                ["s", var id] => v is null ? $"シーン{SceneName(state, id)}を削除"
                    : !existed ? "シーンを追加" : $"シーン{SceneName(state, id)}を移動",
                ["s", var id, "name"] => $"シーン{SceneName(state, id)}の名前を「{(string?)v}」に変更",
                ["s", _, _] => "シーンの色を変更",

                ["t" or "r", var id, .. var rest] => Target(state, p[0], id) + Property(state, rest, v),
                ["m", .. var rest] => "マスター" + Property(state, rest, v),

                ["d", var id] => DescribeDevice(state, id, v, existed),
                ["d", var id, "p", _] => $"{Where(state, id)}の {DeviceName(state, id)} › {ParamText(v)}",
                ["d", var id, "preset"] => $"{Where(state, id)}の {DeviceName(state, id)} のプリセットを変更",
                ["d", var id, "sample"] => $"{Where(state, id)}の {DeviceName(state, id)} のサンプルを「{FileName(v?["file"])}」に変更",
                ["d", var id, "c", var ci, var prop] =>
                    $"{DeviceName(state, id)} のチェーン {int.Parse(ci, CultureInfo.InvariantCulture) + 1} の{ChainProp(prop, v)}",

                ["c", var where, var track, var pos] => DescribeClip(state, where, track, pos, v, existed),
                _ => op.Key,
            };
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or IndexOutOfRangeException)
        {
            return op.Key;
        }
    }

    private static string DescribeDevice(IReadOnlyDictionary<string, JsonNode?> state, string id, JsonNode? v, bool existed)
    {
        if (v is null) return $"{Where(state, id)}の「{DeviceName(state, id)}」を削除";
        var name = (string?)v["n"] ?? "デバイス";
        var at = ContainerName(state, (string?)v["at"]);
        if (!existed) return $"{at}に「{name}」を追加";
        var before = (string?)state[$"d/{id}"]?["at"];
        return before != (string?)v["at"] ? $"「{name}」を{at}へ移動" : $"{at}の「{name}」を並べ替え";
    }

    private static string DescribeClip(IReadOnlyDictionary<string, JsonNode?> state, string where, string track, string pos, JsonNode? v, bool existed)
    {
        var place = where == "s"
            ? $"スロット {SceneIndex(state, pos) + 1}"
            : $"{Bar(pos, state)} 小節目（アレンジメント）";
        var trackName = Name(state, "t", track);
        if (v is null) return $"{trackName}の {place} のクリップを削除";
        var what = (string?)v["k"] == "audio" ? $"オーディオクリップ「{FileName(v["file"])}」" : $"MIDI クリップ（{(v["n"] as JsonArray)?.Count ?? 0} 音）";
        return existed ? $"{trackName}の {place} の{what}を編集" : $"{trackName}の {place} に{what}を追加";
    }

    private static string Property(IReadOnlyDictionary<string, JsonNode?> state, string[] rest, JsonNode? v) => rest switch
    {
        ["vol"] => "の音量を変更",
        ["pan"] => $"のパンを {Pan(Num(v))} に変更",
        ["mute"] => (bool?)v == true ? "をミュート" : "のミュートを解除",
        ["solo"] => (bool?)v == true ? "をソロ" : "のソロを解除",
        ["send", var rid] => $"のセンド{Name(state, "r", rid)}を変更",
        ["name"] => $"の名前を「{(string?)v}」に変更",
        ["color"] => "の色を変更",
        _ => "を変更",
    };

    private static string ChainProp(string prop, JsonNode? v) => prop switch
    {
        "vol" => "音量を変更",
        "pan" => "パンを変更",
        "mute" => (bool?)v == true ? "ミュートをオン" : "ミュートをオフ",
        "name" => $"名前を「{(string?)v}」に変更",
        _ => "設定を変更",
    };

    private static string ParamText(JsonNode? v)
    {
        var name = (string?)v?[1] ?? "パラメータ";
        if (name == "Device On") return Num(v?[0]) >= 0.5 ? "オン" : "オフ";
        return $"{name} を変更";
    }

    private static string Target(IReadOnlyDictionary<string, JsonNode?> state, string kind, string id) =>
        kind == "r" ? $"リターン{Name(state, "r", id)}" : Name(state, "t", id);

    private static string Name(IReadOnlyDictionary<string, JsonNode?> state, string kind, string id) =>
        (string?)state.GetValueOrDefault($"{kind}/{id}/name") is { Length: > 0 } name ? $"「{name}」" : "";

    private static string SceneName(IReadOnlyDictionary<string, JsonNode?> state, string id) =>
        (string?)state.GetValueOrDefault($"s/{id}/name") is { Length: > 0 } name ? $"「{name}」" : $" {SceneIndex(state, id) + 1} ";

    private static int SceneIndex(IReadOnlyDictionary<string, JsonNode?> state, string id) =>
        Math.Max(0, SessionController.Ordered(state, "s").IndexOf(id));

    private static string DeviceName(IReadOnlyDictionary<string, JsonNode?> state, string id) =>
        (string?)state.GetValueOrDefault($"d/{id}")?["n"] ?? "デバイス";

    /// <summary>デバイスがどこにあるか（「Bass」など）</summary>
    private static string Where(IReadOnlyDictionary<string, JsonNode?> state, string deviceId) =>
        ContainerName(state, (string?)state.GetValueOrDefault($"d/{deviceId}")?["at"]);

    private static string ContainerName(IReadOnlyDictionary<string, JsonNode?> state, string? at)
    {
        if (at is null) return "";
        var p = at.Split('/');
        return p switch
        {
            ["m"] => "マスター",
            ["t", var id] => Name(state, "t", id) is { Length: > 0 } n ? n : "トラック",
            ["r", var id] => $"リターン{Name(state, "r", id)}",
            ["d", var id, "c", _] => $"{DeviceName(state, id)} のチェーン",
            _ => "",
        };
    }

    private static string KindName(string? kind) => kind switch
    {
        "midi" => "MIDI ",
        "audio" => "オーディオ",
        "group" => "グループ",
        _ => "",
    };

    private static string FileName(JsonNode? file) => file switch
    {
        JsonObject o => (string?)o["name"] ?? "?",
        JsonValue value when value.TryGetValue<string>(out var path) => path.Replace('\\', '/').Split('/')[^1],
        _ => "?",
    };

    private static int Bar(string startBeats, IReadOnlyDictionary<string, JsonNode?> state)
    {
        var sig = state.GetValueOrDefault("sig");
        var num = (int?)sig?[0] ?? 4;
        var den = (int?)sig?[1] ?? 4;
        return (int)(double.Parse(startBeats, CultureInfo.InvariantCulture) / (num * 4.0 / den)) + 1;
    }

    private static string Pan(double v) => Math.Abs(v) < 0.01 ? "センター" : v < 0 ? $"{-v * 50:0}L" : $"{v * 50:0}R";

    private static double Num(JsonNode? v)
    {
        try { return v?.GetValue<double>() ?? 0; }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return 0; }
    }
}
