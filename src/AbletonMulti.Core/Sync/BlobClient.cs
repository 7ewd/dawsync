using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// サンプルなどのファイルをサーバーとやり取りする接続。
/// 大きなファイルの転送で同期が遅れないよう、ルームの接続とは別の接続を使う。
/// </summary>
public sealed class BlobClient : IAsyncDisposable
{
    public const int ChunkSize = 256 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _uploaded = [];
    private readonly Channel<JsonObject> _inbox = Channel.CreateUnbounded<JsonObject>();
    private IMessageConnection? _connection;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>転送の進み具合（ファイル名, 送受信, 済みバイト数, 全体）</summary>
    public event Action<string, bool, long, long>? Progress;

    public async Task ConnectAsync(string address, string room, string? key, TimeSpan timeout)
    {
        _connection = await Transport.ConnectAsync(address, timeout);
        _connection.Send(new JsonObject { ["t"] = "hello", ["proto"] = RoomServer.ProtocolVersion, ["role"] = "files", ["room"] = room, ["key"] = key });
        _ = Task.Run(async () =>
        {
            await foreach (var msg in _connection.ReadAllAsync(_cts.Token))
                await _inbox.Writer.WriteAsync(msg);
            _inbox.Writer.TryComplete();
        });
    }

    /// <summary>サーバーに無ければ送る。</summary>
    public async Task EnsureUploadedAsync(string sha, string path)
    {
        lock (_uploaded)
            if (_uploaded.Contains(sha)) return;
        await _gate.WaitAsync();
        try
        {
            var connection = _connection ?? throw new InvalidOperationException("ファイル転送の接続がありません");
            connection.Send(new JsonObject { ["t"] = "has", ["sha"] = sha });
            var reply = await ReceiveAsync();
            if ((bool?)reply["yes"] != true)
            {
                var name = Path.GetFileName(path);
                await using var stream = File.OpenRead(path);
                var buffer = new byte[ChunkSize];
                long offset = 0;
                var total = stream.Length;
                while (true)
                {
                    var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                    var final = offset + read >= total;
                    connection.Send(new JsonObject
                    {
                        ["t"] = "put", ["sha"] = sha, ["offset"] = offset,
                        ["data"] = Convert.ToBase64String(buffer, 0, read), ["final"] = final,
                    });
                    offset += read;
                    Progress?.Invoke(name, true, offset, total);
                    if (final) break;
                }
                var done = await ReceiveAsync();
                if ((bool?)done["ok"] != true) throw new IOException($"{name} を送れませんでした");
            }
            lock (_uploaded) _uploaded.Add(sha);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>サーバーから取ってきて target に保存する。</summary>
    public async Task<bool> DownloadAsync(string sha, string target)
    {
        await _gate.WaitAsync();
        try
        {
            var connection = _connection ?? throw new InvalidOperationException("ファイル転送の接続がありません");
            connection.Send(new JsonObject { ["t"] = "get", ["sha"] = sha });
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            await using (var output = File.Create(part))
            {
                while (true)
                {
                    var msg = await ReceiveAsync();
                    if ((string?)msg["t"] == "missing") return false;
                    var data = Convert.FromBase64String((string?)msg["data"] ?? "");
                    await output.WriteAsync(data);
                    Progress?.Invoke(Path.GetFileName(target), false, output.Length, (long?)msg["size"] ?? output.Length);
                    if ((bool?)msg["final"] == true) break;
                }
            }
            await using (var check = File.OpenRead(part))
            {
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(check)) != sha)
                {
                    File.Delete(part);
                    return false;
                }
            }
            File.Move(part, target, overwrite: true);
            lock (_uploaded) _uploaded.Add(sha);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> ReceiveAsync()
    {
        try
        {
            return await _inbox.Reader.ReadAsync(_cts.Token).AsTask().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (ChannelClosedException)
        {
            throw new IOException("ファイル転送の接続が切れました");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
