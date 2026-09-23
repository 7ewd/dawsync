using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbletonMulti.Core;

/// <summary>
/// プロジェクトの依存関係を JSON（マニフェスト）として書き出す。
/// 後で同期サーバーが「どのファイルを送ればいいか」を判断するのに使う。
/// </summary>
public static class ManifestWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<string> BuildJsonAsync(AnalysisResult result, bool hashFiles,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var p = result.Project;
        var files = p.Samples.Concat(p.MaxDevices).ToList();
        var hashes = new Dictionary<FileDependency, string?>();
        for (var i = 0; i < files.Count; i++)
        {
            hashes[files[i]] = hashFiles && files[i].Exists ? await HashAsync(files[i].ResolvedPath, ct) : null;
            progress?.Report((i + 1, files.Count));
        }

        object FileEntry(FileDependency d) => new
        {
            name = d.FileName,
            location = d.Location,
            relativePath = d.Location == FileLocation.Project ? Path.GetRelativePath(p.ProjectDirectory, d.ResolvedPath).Replace('\\', '/') : null,
            storedPath = d.StoredPath,
            livePack = d.LivePackName.Length > 0 ? d.LivePackName : null,
            exists = d.Exists,
            size = d.Exists ? new FileInfo(d.ResolvedPath).Length : d.OriginalFileSize,
            sha256 = hashes[d],
            tracks = d.Tracks,
        };

        var manifest = new
        {
            schema = 1,
            project = p.Name,
            liveVersion = p.LiveVersion,
            tempo = p.Tempo,
            generatedAt = DateTimeOffset.Now,
            generatedOn = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "other",
            plugins = p.Plugins.Select(x => new
            {
                x.Name, x.Vendor, x.Format, x.Uid, x.IsInstrument, x.InstanceCount, x.Tracks,
                installedHere = x.InstallState,
            }),
            samples = p.Samples.Select(FileEntry),
            maxDevices = p.MaxDevices.Select(FileEntry),
            packs = p.RequiredPacks,
            tracks = p.Tracks.Select(t => new { t.Name, t.Kind, t.Depth, t.Devices, t.ClipCount, t.MidiNoteCount }),
            issues = result.Issues.Select(i => new { i.Severity, i.Title }),
        };
        return JsonSerializer.Serialize(manifest, Options);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
