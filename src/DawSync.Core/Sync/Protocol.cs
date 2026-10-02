using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DawSync.Core.Sync;

/// <summary>
/// 同期の最小単位。「キー」と「値」の組で、同じキーは後から来た値で上書きされる（Last-Writer-Wins）。
/// 例: ("tempo", 140) / ("sig", [4, 4]) /
/// ("locators", [[16, "Chorus"]]) / ("tempo_map", [[0, 120], [64, 128]]) /
/// ("sig_map", [[0, 4, 4], [64, 3, 4]]) /
/// ("t/3/vol", 0.85) / ("c/s/2/0", {len, n:[[pitch,start,dur,vel,mute],...]})
/// 値が null のときは削除（クリップの削除など）。
/// </summary>
public sealed record Op(string Key, JsonNode? Value)
{
    public JsonObject ToJson() => new() { ["k"] = Key, ["v"] = Value?.DeepClone() };

    public static Op FromJson(JsonNode node) =>
        new((string?)node["k"] ?? throw new JsonException("op に k がありません"), node["v"]?.DeepClone());

    public static JsonArray ToArray(IEnumerable<Op> ops) => new(ops.Select(o => (JsonNode)o.ToJson()).ToArray());

    public static List<Op> FromArray(JsonNode? array) =>
        array is JsonArray a ? a.OfType<JsonNode>().Select(FromJson).ToList() : [];
}

/// <param name="Version">その人のアプリのバージョン（古いアプリは送ってこないので null）</param>
public sealed record Peer(int Id, string Name, string? Version = null);

/// <summary>TCP の上で「1 行 = 1 つの JSON」をやり取りする接続。送信は順番を保ってキューに積む。</summary>
public sealed class JsonLineConnection : IMessageConnection
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private int _queued;
    private int _disposed;

    public JsonLineConnection(TcpClient client, Stream? stream = null)
    {
        _client = client;
        _client.NoDelay = true;
        Transport.EnableKeepAlive(client.Client);
        _stream = stream ?? client.GetStream();
        _writer = Task.Run(WriteLoopAsync);
    }

    public string RemoteEndPoint => _client.Client.RemoteEndPoint?.ToString() ?? "?";

    public int MaxMessageBytes { get; set; } = int.MaxValue;

    public void Send(JsonObject message)
    {
        if (Interlocked.Increment(ref _queued) > Transport.MaxQueuedMessages)
        {
            // 相手が受け取れていない（回線が極端に遅い・止まっている）。溜め続けるとメモリを食うので切る
            Abort();
            return;
        }
        if (!_outbox.Writer.TryWrite(message.ToJsonString())) Interlocked.Decrement(ref _queued);
    }

    public async ValueTask WaitForSpaceAsync(int maxQueued, CancellationToken ct)
    {
        while (Volatile.Read(ref _queued) > maxQueued)
        {
            if (_cts.IsCancellationRequested) throw new IOException("接続が切れました");
            await Task.Delay(5, ct);
        }
    }

    public async IAsyncEnumerable<JsonObject> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        var ready = new List<JsonObject>();
        while (true)
        {
            int read;
            try { read = await _stream.ReadAsync(buffer, linked.Token); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { yield break; }
            if (read == 0) yield break;

            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                start = i + 1;
                if (Parse(line) is { } message) ready.Add(message);
                line.SetLength(0);
            }
            line.Write(buffer, start, read - start);
            // 改行の来ないとても長い行（壊れたデータ・嫌がらせ）で、メモリを食いつぶさないようにする
            var tooLong = line.Length > MaxMessageBytes;
            foreach (var message in ready) yield return message;
            ready.Clear();
            if (tooLong) yield break;
        }
    }

    private static JsonObject? Parse(MemoryStream line)
    {
        var span = line.GetBuffer().AsSpan(0, (int)line.Length).TrimEnd((byte)'\r');
        if (span.IsEmpty) return null;
        try { return JsonNode.Parse(span) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.WriteAsync(bytes, _cts.Token);
                Interlocked.Decrement(ref _queued);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // 送れなくなったら読む方も止める（相手からは切断に見える。つなぎ直しが始まる）
            Abort();
        }
    }

    private void Abort()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _outbox.Writer.TryComplete();
        // 残っている送信を少しだけ待ってから閉じる
        await Task.WhenAny(_writer, Task.Delay(500));
        Abort();
        _client.Dispose();
    }
}
