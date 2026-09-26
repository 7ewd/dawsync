using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// 同期の最小単位。「キー」と「値」の組で、同じキーは後から来た値で上書きされる（Last-Writer-Wins）。
/// 例: ("tempo", 140) / ("t/3/vol", 0.85) / ("c/s/2/0", {len, n:[[pitch,start,dur,vel,mute],...]})
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

public sealed record Peer(int Id, string Name);

/// <summary>TCP の上で「1 行 = 1 つの JSON」をやり取りする接続。送信は順番を保ってキューに積む。</summary>
public sealed class JsonLineConnection : IMessageConnection
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;

    public JsonLineConnection(TcpClient client, Stream? stream = null)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = stream ?? client.GetStream();
        _writer = Task.Run(WriteLoopAsync);
    }

    public string RemoteEndPoint => _client.Client.RemoteEndPoint?.ToString() ?? "?";

    public void Send(JsonObject message) => _outbox.Writer.TryWrite(message.ToJsonString());

    public async IAsyncEnumerable<JsonObject> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        using var reader = new StreamReader(_stream, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
        while (true)
        {
            string? line;
            try { line = await reader.ReadLineAsync(linked.Token); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { yield break; }
            if (line is null) yield break;
            if (line.Length == 0) continue;

            JsonObject? message = null;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { }
            if (message is not null) yield return message;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stream.WriteAsync(bytes, _cts.Token);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        // 残っている送信を少しだけ待ってから閉じる
        await Task.WhenAny(_writer, Task.Delay(500));
        await _cts.CancelAsync();
        _client.Dispose();
        _cts.Dispose();
    }
}
