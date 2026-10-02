using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// サンプルなどのファイルを「中身のハッシュ（SHA-256）」で扱う。
/// Live 側はローカルのパス、ルーム側はハッシュで表すので、その変換をここで行う。
///   Live → ルーム: {"file": "C:/.../kick.wav"} → {"file": {"sha256": "...", "name": "kick.wav", "size": 1234}}
///   ルーム → Live: 逆。手元に無ければダウンロードして Samples フォルダに置く。
/// </summary>
public sealed class FileStore
{
    private readonly ConcurrentDictionary<(string Path, long Size, DateTime Time), string> _hashCache = new();
    private readonly ConcurrentDictionary<string, string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);

    private volatile string _cacheDirectory = "";

    public FileStore(string? cacheDirectory = null) => ChangeDirectory(cacheDirectory ?? DefaultCacheDirectory());

    /// <summary>届いたサンプルを置く場所</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// 届いたサンプルを置く場所を変える。前の場所に置いたファイルはそのまま（Live のクリップが使っているので）。
    /// </summary>
    public void ChangeDirectory(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        // 前にダウンロードしたものを覚えておく（<保存先>/<sha256>/<元のファイル名>）
        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            var sha = Path.GetFileName(dir);
            var file = Directory.EnumerateFiles(dir).FirstOrDefault(f => !f.EndsWith(".part", StringComparison.Ordinal));
            if (sha.Length == 64 && file is not null) _knownPaths[sha] = file;
        }
        _cacheDirectory = directory;
    }

    public static string DefaultCacheDirectory()
    {
        var documents = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(documents, "AbletonMulti", "Samples");
    }

    public async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc);
        if (_hashCache.TryGetValue(key, out var cached)) return cached;
        await using var stream = File.OpenRead(path);
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        _hashCache[key] = sha;
        _knownPaths.TryAdd(sha, info.FullName);
        return sha;
    }

    /// <summary>このハッシュのファイルが手元にあればそのパス</summary>
    public string? Find(string sha) =>
        _knownPaths.TryGetValue(sha, out var path) && File.Exists(path) ? path : null;

    /// <summary>SHA-256 の 16 進（小文字 64 文字）か。フォルダ名に使うので、それ以外（"..\\" など）は受け付けない。</summary>
    public static bool IsHash(string? sha) => sha is { Length: 64 } && sha.All(char.IsAsciiHexDigitLower);

    public string PathForDownload(string sha, string name)
    {
        if (!IsHash(sha)) throw new ArgumentException("bad hash", nameof(sha));
        var safeName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (safeName.Length == 0) safeName = "sample";
        return Path.Combine(CacheDirectory, sha, safeName);
    }

    public void Register(string sha, string path) => _knownPaths[sha] = path;

    /// <summary>
    /// ファイルが書き終わっているか。バウンスや録音の直後は Live がまだ書いている途中なので、
    /// 少し前から更新が止まっていて、他のプログラムが書き込み中でないときだけ true。
    /// </summary>
    public static bool IsReady(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return false;
            if (DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(1.5)) return false;
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Live 側の値に含まれるローカルのファイルパス</summary>
    public static List<string> LocalPaths(JsonNode? value)
    {
        var result = new List<string>();
        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["file"] is JsonValue v && v.TryGetValue<string>(out var path) && path.Length > 0) result.Add(path);
                    foreach (var (_, child) in o) Visit(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) Visit(child);
                    break;
            }
        }
        Visit(value);
        return result;
    }

    // ---- JSON の変換 ----

    /// <summary>値の中の "file"（ローカルのパス）をハッシュ表現に置き換える。upload が渡されればファイルを送る。</summary>
    public async Task<JsonNode?> ToRoomAsync(JsonNode? value, Func<string, string, Task>? upload, CancellationToken ct = default)
    {
        if (value is null) return null;
        var copy = value.DeepClone();
        await WalkAsync(copy, async (obj, path) =>
        {
            var name = path.Replace('\\', '/').Split('/')[^1];
            if (!File.Exists(path))
            {
                obj["file"] = new JsonObject { ["name"] = name, ["missing"] = true };
                return;
            }
            var sha = await HashAsync(path, ct);
            if (upload is not null) await upload(sha, path);
            obj["file"] = new JsonObject { ["sha256"] = sha, ["name"] = name, ["size"] = new FileInfo(path).Length };
        });
        return copy;
    }

    /// <summary>値の中の "file"（ハッシュ表現）をローカルのパスに置き換える。無ければ download で取ってくる。</summary>
    public async Task<JsonNode?> ToLocalAsync(JsonNode? value, Func<string, string, Task<bool>>? download, CancellationToken ct = default)
    {
        if (value is null) return null;
        var copy = value.DeepClone();
        await WalkRoomAsync(copy, async obj =>
        {
            var file = obj["file"]!.AsObject();
            var sha = (string?)file["sha256"];
            if (!IsHash(sha)) sha = null;  // 相手から届いたものなので、ファイルのパスに使う前に形を確かめる
            var name = (string?)file["name"] ?? "sample";
            string? path = null;
            if (sha is not null)
            {
                path = Find(sha);
                if (path is null && download is not null)
                {
                    var target = PathForDownload(sha, name);
                    if (await download(sha, target))
                    {
                        Register(sha, target);
                        path = target;
                    }
                }
            }
            obj["file"] = path;
        });
        return copy;
    }

    /// <summary>ルーム側の値に含まれるファイルのハッシュ一覧</summary>
    public static IEnumerable<string> ReferencedHashes(JsonNode? value)
    {
        var result = new List<string>();
        void Visit(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["file"] is JsonObject f && (string?)f["sha256"] is { } sha && IsHash(sha)) result.Add(sha);
                    foreach (var (_, child) in o) Visit(child);
                    break;
                case JsonArray a:
                    foreach (var child in a) Visit(child);
                    break;
            }
        }
        Visit(value);
        return result;
    }

    private static async Task WalkAsync(JsonNode node, Func<JsonObject, string, Task> onFile)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["file"] is JsonValue v && v.TryGetValue<string>(out var path) && path.Length > 0)
                    await onFile(o, path);
                foreach (var child in o.Select(kv => kv.Value).OfType<JsonNode>().ToList())
                    if (child is JsonObject or JsonArray) await WalkAsync(child, onFile);
                break;
            case JsonArray a:
                foreach (var child in a.OfType<JsonNode>().ToList())
                    await WalkAsync(child, onFile);
                break;
        }
    }

    private static async Task WalkRoomAsync(JsonNode node, Func<JsonObject, Task> onFile)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["file"] is JsonObject) await onFile(o);
                foreach (var child in o.Select(kv => kv.Value).OfType<JsonNode>().ToList())
                    if (child is JsonObject or JsonArray) await WalkRoomAsync(child, onFile);
                break;
            case JsonArray a:
                foreach (var child in a.OfType<JsonNode>().ToList())
                    await WalkRoomAsync(child, onFile);
                break;
        }
    }
}
