using Avalonia;
using Avalonia.Media;
using AbletonMulti.Core;

namespace AbletonMulti.App;

/// <summary>画面の色。ライト/ダーク両方で読める中間色にしている。</summary>
public static class Palette
{
    public static readonly Color Red = Color.Parse("#E5484D");
    public static readonly Color Amber = Color.Parse("#E08A00");
    public static readonly Color Green = Color.Parse("#30A46C");
    public static readonly Color Blue = Color.Parse("#3E83F8");
    public static readonly Color Purple = Color.Parse("#8E4EC6");
    public static readonly Color Gray = Color.Parse("#8B8D98");

    public static IBrush Solid(Color c) => new SolidColorBrush(c);
    public static IBrush Tint(Color c, byte alpha = 0x2A) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

    public static Color For(Severity s) => s switch
    {
        Severity.Error => Red,
        Severity.Warning => Amber,
        Severity.Info => Blue,
        _ => Green,
    };
}

/// <summary>色付きの小さなラベル。</summary>
public sealed class Pill(string text, Color color)
{
    public string Text { get; } = text;
    public IBrush Foreground { get; } = Palette.Solid(color);
    public IBrush Background { get; } = Palette.Tint(color);
}

public sealed class IssueVm(Issue issue)
{
    public string Title => issue.Title;
    public string Detail => issue.Detail;
    public string Fix => issue.Fix;
    public bool HasFix => issue.Fix.Length > 0;
    public IReadOnlyList<string> Items => issue.Items;
    public bool HasItems => issue.Items.Count > 0;
    public string ItemsHeader => $"該当する項目を見る（{issue.Items.Count}）";
    public IBrush Accent => Palette.Solid(Palette.For(issue.Severity));
    public Pill Badge => new(issue.Severity switch
    {
        Severity.Error => "要対応",
        Severity.Warning => "注意",
        _ => "お知らせ",
    }, Palette.For(issue.Severity));
}

public sealed class PluginVm(PluginUsage p)
{
    public string Name => p.Name;
    public string Subtitle => string.Join(" · ", new[] { p.Vendor, p.IsInstrument ? "音源" : "エフェクト", $"{p.InstanceCount} か所で使用" }.Where(s => s.Length > 0));
    public string Tracks => "トラック: " + string.Join(", ", p.Tracks);
    public Pill Format => p.Format switch
    {
        PluginFormat.Vst3 => new Pill("VST3", Palette.Blue),
        PluginFormat.Vst2 => new Pill("VST2", Palette.Purple),
        _ => new Pill("AU", Palette.Red),
    };
    public Pill Status => (p.Format, p.InstallState) switch
    {
        (PluginFormat.AudioUnit, _) => new Pill("Mac 専用", Palette.Red),
        (_, InstallState.Installed) => new Pill("✓ この PC にあります", Palette.Green),
        (_, InstallState.Missing) => new Pill("この PC にありません", Palette.Amber),
        _ => new Pill("確認中…", Palette.Gray),
    };
    public string? InstalledPath => p.InstalledPath;
}

public sealed class FileVm(FileDependency d)
{
    public string FileName => d.FileName;
    public string Folder => d.ResolvedPath[..Math.Max(0, d.ResolvedPath.Length - d.FileName.Length)].TrimEnd('/', '\\');
    public string Tracks => "トラック: " + string.Join(", ", d.Tracks);
    public string Size => FormatSize(d.Exists ? new FileInfo(d.ResolvedPath).Length : d.OriginalFileSize);
    public Pill Location => !d.Exists
        ? new Pill("見つかりません", Palette.Red)
        : d.Location switch
        {
            FileLocation.Project => new Pill("✓ プロジェクト内", Palette.Green),
            FileLocation.AbletonLibrary => new Pill(d.LivePackName.Length > 0 ? d.LivePackName : "Ableton ライブラリ", Palette.Blue),
            FileLocation.UserLibrary => new Pill("User Library", Palette.Amber),
            _ => new Pill("プロジェクト外", Palette.Amber),
        };

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

public sealed class TrackVm(TrackInfo t)
{
    public string Name => t.Name;
    public Thickness Indent => new(t.Depth * 22, 0, 0, 0);
    public Pill Kind => t.Kind switch
    {
        TrackKind.Midi => new Pill("MIDI", Palette.Purple),
        TrackKind.Audio => new Pill("Audio", Palette.Blue),
        TrackKind.Group => new Pill("Group", Palette.Gray),
        TrackKind.Return => new Pill("Return", Palette.Green),
        _ => new Pill("Main", Palette.Amber),
    };
    public string Devices => t.Devices.Count > 0 ? string.Join("  ›  ", t.Devices) : "デバイスなし";
    public string Stats => t.Kind switch
    {
        TrackKind.Midi => $"クリップ {t.ClipCount} · ノート {t.MidiNoteCount}",
        TrackKind.Audio => $"クリップ {t.ClipCount}",
        _ => "",
    };
}
