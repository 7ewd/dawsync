using System.Text.RegularExpressions;

namespace Maltese.Core;

/// <summary>アプリのバージョン（ルームで相手とバージョンが違うと知らせるのに使う）。</summary>
public static class AppInfo
{
    public const string Version = "0.6.4";

    /// <summary>"1.2.3" のようなバージョンを比べる（読めないものは一番古い扱い）。</summary>
    public static int Compare(string? a, string? b) => Parse(a).CompareTo(Parse(b));

    /// <summary>一緒に使えるバージョンか（3 つ目の数字だけの違いは、やりとりの決まりが同じ）。</summary>
    public static bool Compatible(string? a, string? b)
    {
        var x = Parse(a);
        var y = Parse(b);
        return a is not null && b is not null && x.Major == y.Major && x.Minor == y.Minor;
    }

    private static Version Parse(string? v) =>
        v is not null && System.Version.TryParse(Regex.Match(v, @"\d+(\.\d+){0,3}").Value, out var parsed) ? parsed : new Version(0, 0);

    /// <summary>スクリプトの中身から、バージョンの書かれた行（<paramref name="pattern"/> の 1 つ目のグループ）を読む。</summary>
    public static string? FindVersion(byte[] content, string pattern)
    {
        var m = Regex.Match(System.Text.Encoding.UTF8.GetString(content), pattern);
        return m.Success ? m.Groups[1].Value : null;
    }
}
