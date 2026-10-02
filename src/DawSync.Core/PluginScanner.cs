using System.Text.Json;

namespace DawSync.Core;

public sealed record InstalledPlugin(PluginFormat Format, string Name, string Path, IReadOnlySet<string> ClassIds);

/// <summary>この PC にインストールされているプラグインの一覧。</summary>
public sealed partial class PluginCatalog
{
    public required IReadOnlyList<InstalledPlugin> Plugins { get; init; }
    public required IReadOnlyList<string> ScannedFolders { get; init; }

    public InstalledPlugin? Find(PluginUsage usage)
    {
        var candidates = Plugins.Where(p => p.Format == usage.Format).ToList();

        // VST3 はクラスIDで確実に照合できる（moduleinfo.json がある場合）
        if (usage.Format == PluginFormat.Vst3 && usage.Uid.Length > 0)
        {
            var byId = candidates.FirstOrDefault(p => p.ClassIds.Contains(usage.Uid));
            if (byId is not null) return byId;
        }

        // それ以外は名前で照合。"Serum 2" ⇔ "Serum2.vst3"、"Pro-Q 4" ⇔ "FabFilter Pro-Q 4.vst3"、
        // "Partial" ⇔ "Partial v1.1.vst3" のような揺れを吸収する。
        var wanted = new HashSet<string> { Normalize(usage.Name) };
        if (usage.Vendor.Length > 0) wanted.Add(Normalize(usage.Vendor + usage.Name));
        return candidates.FirstOrDefault(p => NameVariants(p).Overlaps(wanted));
    }

    public static string Normalize(string s) =>
        string.Concat(s.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static HashSet<string> NameVariants(InstalledPlugin p)
    {
        var bare = VersionSuffix().Replace(p.Name, "");
        var variants = new HashSet<string> { Normalize(p.Name), Normalize(bare) };
        // メーカー名のフォルダに入っている場合、ファイル名の先頭のメーカー名を外した形も候補にする
        var folder = Normalize(Path.GetFileName(Path.GetDirectoryName(p.Path)) ?? "");
        foreach (var v in variants.ToList())
            if (folder.Length > 0 && v.StartsWith(folder, StringComparison.Ordinal) && v.Length > folder.Length)
                variants.Add(v[folder.Length..]);
        return variants;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[\s_-]*v?\d+(\.\d+)+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VersionSuffix();
}

public static class PluginScanner
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static IEnumerable<(string Folder, PluginFormat Format)> DefaultFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return (Path.Combine(common, "VST3"), PluginFormat.Vst3);
            yield return (Path.Combine(localAppData, "Programs", "Common", "VST3"), PluginFormat.Vst3);
            yield return (Path.Combine(programFiles, "VstPlugins"), PluginFormat.Vst2);
            yield return (Path.Combine(programFiles, "Steinberg", "VstPlugins"), PluginFormat.Vst2);
            yield return (Path.Combine(common, "VST2"), PluginFormat.Vst2);
            yield return (Path.Combine(common, "Steinberg", "VST2"), PluginFormat.Vst2);
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var root in new[] { "/Library/Audio/Plug-Ins", Path.Combine(home, "Library/Audio/Plug-Ins") })
            {
                yield return (Path.Combine(root, "VST3"), PluginFormat.Vst3);
                yield return (Path.Combine(root, "VST"), PluginFormat.Vst2);
                yield return (Path.Combine(root, "Components"), PluginFormat.AudioUnit);
            }
        }
    }

    public static PluginCatalog Scan(IEnumerable<(string Folder, PluginFormat Format)>? folders = null)
    {
        var found = new List<InstalledPlugin>();
        var scanned = new List<string>();
        foreach (var (folder, format) in folders ?? DefaultFolders())
        {
            if (!Directory.Exists(folder)) continue;
            scanned.Add(folder);
            Walk(folder, format, found, depth: 0);
        }
        return new PluginCatalog { Plugins = found, ScannedFolders = scanned };
    }

    private static void Walk(string dir, PluginFormat format, List<InstalledPlugin> found, int depth)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }

        foreach (var entry in entries)
        {
            var ext = Path.GetExtension(entry).ToLowerInvariant();
            var isPlugin = format switch
            {
                PluginFormat.Vst3 => ext == ".vst3",
                PluginFormat.Vst2 => ext is ".dll" or ".vst",
                PluginFormat.AudioUnit => ext == ".component",
                _ => false,
            };

            if (isPlugin)
                found.Add(new InstalledPlugin(format, Path.GetFileNameWithoutExtension(entry), entry, ReadVst3ClassIds(entry)));
            else if (depth < 6 && Directory.Exists(entry))
                Walk(entry, format, found, depth + 1);
        }
    }

    private static IReadOnlySet<string> ReadVst3ClassIds(string bundle)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moduleInfo = Path.Combine(bundle, "Contents", "Resources", "moduleinfo.json");
        if (!File.Exists(moduleInfo)) return ids;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(moduleInfo), JsonOptions);
            if (json.RootElement.TryGetProperty("Classes", out var classes))
                foreach (var c in classes.EnumerateArray())
                    if (c.TryGetProperty("CID", out var cid) && cid.GetString() is { } s)
                        ids.Add(s.Replace("-", ""));
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException) { }
        return ids;
    }
}
