using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DawSync.Core.Sync;

/// <summary>
/// サンプルなどのファイルをサーバーとやり取りする接続。
/// 大きなファイルの転送で同期が遅れないよう、ルームの接続とは別の接続を使う。
/// 1 度に 1 つのファイルだけ送受信する（返事の順番で対応を取るため）。
/// </summary>
public sealed class BlobClient : IAsyncDisposable
{
    public const int ChunkSize = 256 * 1024;
    // これだけ待っても次の返事（チャンク）が来なければ、止まったとみなす（ファイル全体ではなく、進みが無い時間）
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _uploaded = [];
    private readonly CancellationTokenSource _cts = new();
    private IMessageConnection? _connection;
    private Channel<JsonObject> _inbox = Channel.CreateUnbounded<JsonObject>();
    private (string Address, string Room, string? Key)? _target;

    /// <summary>転送の進み具合（ファイル名, 送受信, 済みバイト数, 全体）</summary>
    public event Action<string, bool, long, long>? Progress;

    public async Task ConnectAsync(string address, string room, string? key, TimeSpan timeout)
    {
        _target = (address, room, key);
        await OpenAsync(timeout);
    }

    private async Task OpenAsync(TimeSpan timeout)
    {
        var (address, room, key) = _target ?? throw new InvalidOperationException("ファイル転送の接続がありません");
        var connection = await Transport.ConnectAsync(address, timeout);
        var inbox = Channel.CreateUnbounded<JsonObject>();
        connection.Send(new JsonObject { ["t"] = "hello", ["proto"] = RoomServer.ProtocolVersion, ["role"] = "files", ["room"] = room, ["key"] = key });
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var msg in connection.ReadAllAsync(_cts.Token))
                    await inbox.Writer.WriteAsync(msg);
            }
            catch (Exception e) when (e is OperationCanceledException or ChannelClosedException) { }
            inbox.Writer.TryComplete();
        });
        _inbox = inbox;
        _connection = connection;
    }

    /// <summary>
    /// 途中で止まった・切れた接続は捨てて、次の転送のときにつなぎ直す
    /// （残ったままだと、遅れて届いた返事を次の転送の返事と取り違える）。
    /// </summary>
    private async Task ResetAsync()
    {
        var old = Interlocked.Exchange(ref _connection, null);
        if (old is not null) await old.DisposeAsync();
    }

    private async Task<IMessageConnection> ConnectionAsync()
    {
        if (_connection is { } connection && !_inbox.Reader.Completion.IsCompleted) return connection;
        await ResetAsync();
        await OpenAsync(TimeSpan.FromSeconds(15));
        return _connection!;
    }

    /// <summary>サーバーに無ければ送る。</summary>
    public async Task EnsureUploadedAsync(string sha, string path)
    {
        lock (_uploaded)
            if (_uploaded.Contains(sha)) return;
        await _gate.WaitAsync(_cts.Token);
        try
        {
            var connection = await ConnectionAsync();
            connection.Send(new JsonObject { ["t"] = "has", ["sha"] = sha });
            var reply = await ReceiveAsync(sha, "has");
            if ((bool?)reply["yes"] != true)
            {
                var name = Path.GetFileName(path);
                await using var stream = File.OpenRead(path);
                var buffer = new byte[ChunkSize];
                long offset = 0;
                var total = stream.Length;
                while (true)
                {
                    // 相手に届いた分だけ次を積む（大きなファイルをまるごとメモリに積まない。進み具合も本当の値になる）
                    await connection.WaitForSpaceAsync(4, _cts.Token).AsTask().WaitAsync(StallTimeout, _cts.Token);
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
                var done = await ReceiveAsync(sha, "put_done");
                if ((bool?)done["ok"] != true) throw new IOException($"{name} を送れませんでした");
            }
            lock (_uploaded) _uploaded.Add(sha);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            await ResetAsync();
            throw new IOException(e is TimeoutException ? "ファイルの送信が止まりました（回線が遅いか、切れています）" : e.Message, e);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>サーバーから取ってきて target に保存する。</summary>
    public async Task<bool> DownloadAsync(string sha, string target)
    {
        await _gate.WaitAsync(_cts.Token);
        var part = target + ".part";
        try
        {
            var connection = await ConnectionAsync();
            connection.Send(new JsonObject { ["t"] = "get", ["sha"] = sha });
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var output = File.Create(part))
            {
                while (true)
                {
                    var msg = await ReceiveAsync(sha, "chunk", "missing");
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
        catch (Exception e) when (e is TimeoutException or IOException or FormatException or UnauthorizedAccessException)
        {
            // 受け取れなかった（切れた・止まった・壊れたデータ）。次の転送はつなぎ直して行う
            await ResetAsync();
            try { File.Delete(part); }
            catch (Exception) { }
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>このファイルへの返事（kinds のどれか）を待つ。関係ないもの（前の転送の残り）は読み捨てる。</summary>
    private async Task<JsonObject> ReceiveAsync(string sha, params string[] kinds)
    {
        while (true)
        {
            JsonObject msg;
            try
            {
                msg = await _inbox.Reader.ReadAsync(_cts.Token).AsTask().WaitAsync(StallTimeout, _cts.Token);
            }
            catch (ChannelClosedException)
            {
                throw new IOException("ファイル転送の接続が切れました");
            }
            var kind = (string?)msg["t"];
            if (kind == "error") throw new IOException((string?)msg["msg"] ?? "ファイル転送でエラーが起きました");
            if ((string?)msg["sha"] == sha && kinds.Contains(kind)) return msg;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await ResetAsync();
    }
}
