namespace Maltese.Core;

public enum TrackKind { Audio, Midi, Group, Return, Main }

public enum PluginFormat { Vst2, Vst3, AudioUnit }

public enum InstallState { Unknown, Installed, Missing }

/// <summary>サンプルや Max for Live デバイスのファイルがどこにあるか。</summary>
public enum FileLocation
{
    /// <summary>プロジェクトフォルダ内。そのまま共有できる。</summary>
    Project,
    /// <summary>Ableton の Core Library / Pack。相手も同じ Pack を持っていれば OK。</summary>
    AbletonLibrary,
    /// <summary>自分の User Library。相手の PC には無い。</summary>
    UserLibrary,
    /// <summary>プロジェクト外の任意の場所。相手の PC には無い。</summary>
    External,
}

public enum Severity { Ok, Info, Warning, Error }

public sealed class TrackInfo
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required TrackKind Kind { get; init; }
    public int GroupId { get; init; } = -1;
    public int Depth { get; set; }
    public List<string> Devices { get; } = [];
    public int ClipCount { get; set; }
    public int MidiNoteCount { get; set; }
}

public sealed class PluginUsage
{
    public required PluginFormat Format { get; init; }
    public required string Name { get; init; }
    public string Vendor { get; set; } = "";
    /// <summary>VST3 はクラスID(32桁hex)、VST2 は UniqueId、AU はサブタイプ等。</summary>
    public string Uid { get; init; } = "";
    public bool IsInstrument { get; init; }
    public int InstanceCount { get; set; }
    public SortedSet<string> Tracks { get; } = new(StringComparer.Ordinal);
    public InstallState InstallState { get; set; } = InstallState.Unknown;
    public string? InstalledPath { get; set; }

    public string Key => $"{Format}:{(Uid.Length > 0 ? Uid : Name.ToLowerInvariant())}";
}

public sealed class FileDependency
{
    /// <summary>.als に書かれていた絶対パス（保存した PC 上のパス）。</summary>
    public required string StoredPath { get; init; }
    public required string RelativePath { get; init; }
    public required int RelativePathType { get; init; }
    public required string LivePackName { get; init; }
    public long OriginalFileSize { get; init; }
    /// <summary>この PC で実際に見つかったパス（見つからなければ最有力候補）。</summary>
    public required string ResolvedPath { get; init; }
    public required bool Exists { get; init; }
    public required FileLocation Location { get; init; }
    public SortedSet<string> Tracks { get; } = new(StringComparer.Ordinal);
    public int UseCount { get; set; }

    // Windows で保存されたパスを Mac で読む場合もあるので、\ も区切りとして扱う
    public string FileName => ResolvedPath.Replace('\\', '/').Split('/')[^1];
    public bool NeedsCollecting => Location is FileLocation.External or FileLocation.UserLibrary;
}

public sealed class Issue
{
    public required Severity Severity { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Fix { get; init; } = "";
    public List<string> Items { get; init; } = [];
}

public sealed class AlsProject
{
    public required string FilePath { get; init; }
    public required string Creator { get; init; }
    /// <summary>"12.4.6" のようなバージョン文字列。取れなければ空。</summary>
    public required string LiveVersion { get; init; }
    public double? Tempo { get; init; }
    public List<TrackInfo> Tracks { get; } = [];
    public List<PluginUsage> Plugins { get; } = [];
    public List<FileDependency> Samples { get; } = [];
    public List<FileDependency> MaxDevices { get; } = [];
    public List<string> ExternalRoutingTracks { get; } = [];

    public string ProjectDirectory => Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
    public string Name => Path.GetFileNameWithoutExtension(FilePath);

    public IEnumerable<string> RequiredPacks =>
        Samples.Concat(MaxDevices)
            .Select(d => d.LivePackName)
            .Where(p => p.Length > 0)
            .Distinct()
            .Order();
}

public sealed class AnalysisResult
{
    public required AlsProject Project { get; init; }
    public required List<Issue> Issues { get; init; }

    public Severity Overall => Issues.Count == 0 ? Severity.Ok : Issues.Max(i => i.Severity);
    public int ErrorCount => Issues.Count(i => i.Severity == Severity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == Severity.Warning);
}
