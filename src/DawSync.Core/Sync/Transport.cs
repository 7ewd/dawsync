using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DawSync.Core.Sync;

/// <summary>JSON メッセージをやり取りする接続（TCP の 1 行 1 JSON か、WebSocket）。</summary>
public interface IMessageConnection : IAsyncDisposable
{
    void Send(JsonObject message);
    IAsyncEnumerable<JsonObject> ReadAllAsync(CancellationToken ct = default);

    /// <summary>1 つのメッセージの大きさの上限（超えたら切る）。参加を認める前は小さくしておく。</summary>
    int MaxMessageBytes { get; set; }

    /// <summary>送信待ちが maxQueued 個以下になるまで待つ（大きなファイルを送るとき、メモリに全部積まないように）。</summary>
    ValueTask WaitForSpaceAsync(int maxQueued, CancellationToken ct);
}

/// <summary>
/// WebSocket の接続。インターネット越し（Cloudflare のトンネル経由）ではこちらを使う。
/// Cloudflare は HTTP / WebSocket しか通さないため。
/// </summary>
public sealed class WebSocketConnection : IMessageConnection
{
    private readonly WebSocket _socket;
    private readonly IDisposable? _owner;
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private int _queued;
    private int _disposed;

    public WebSocketConnection(WebSocket socket, IDisposable? owner = null)
    {
        _socket = socket;
        _owner = owner;
        _writer = Task.Run(WriteLoopAsync);
    }

    public int MaxMessageBytes { get; set; } = int.MaxValue;

    public void Send(JsonObject message)
    {
        if (Interlocked.Increment(ref _queued) > Transport.MaxQueuedMessages)
        {
            Abort();  // 相手が受け取れていない。溜め続けるとメモリを食うので切る
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

    private void Abort()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async IAsyncEnumerable<JsonObject> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(), linked.Token);
            }
            catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                yield break;
            }
            if (result.MessageType == WebSocketMessageType.Close) yield break;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) yield break;  // 大きすぎるメッセージでメモリを食いつぶさない
            if (!result.EndOfMessage) continue;

            JsonObject? parsed = null;
            try { parsed = JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) as JsonObject; }
            catch (JsonException) { }
            message.SetLength(0);
            if (parsed is not null) yield return parsed;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var text in _outbox.Reader.ReadAllAsync(_cts.Token))
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cts.Token);
                Interlocked.Decrement(ref _queued);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            Abort();  // 送れなくなったら読む方も止める（相手からは切断に見える）
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _outbox.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(500));
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
        Abort();
        _socket.Dispose();
        _owner?.Dispose();
    }
}

public static class Transport
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    /// <summary>送信待ちがこれを超えたら、相手が受け取れていないとみなして切る</summary>
    public const int MaxQueuedMessages = 50_000;

    /// <summary>
    /// TCP のキープアライブ。相手の PC がスリープした・Wi-Fi が変わったなどで無言で切れた接続に、
    /// 30 秒ほどで気づけるようにする（気づかないと、つなぎ直しが始まらない）。
    /// </summary>
    public static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception e) when (e is SocketException or PlatformNotSupportedException or ObjectDisposedException) { }
    }

    /// <summary>
    /// アドレスに接続する。
    ///   192.168.1.10 / 100.x.x.x:47401      → TCP（同じ Wi-Fi、Tailscale）
    ///   xxx.trycloudflare.com / wss://...   → WebSocket（インターネットに公開したルーム）
    /// </summary>
    public static async Task<IMessageConnection> ConnectAsync(string address, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        if (ToWebSocketUri(address) is { } uri)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = KeepAlive;
            try
            {
                await socket.ConnectAsync(uri, cts.Token);
            }
            catch
            {
                socket.Dispose();  // つなぎ直しで何度も失敗しても、ソケットを残さない
                throw;
            }
            return new WebSocketConnection(socket);
        }

        var (host, port) = SplitHostPort(address);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new JsonLineConnection(tcp);
    }

    public static Uri? ToWebSocketUri(string address)
    {
        address = address.Trim();
        if (address.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || address.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            return new Uri(address);
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Uri("wss://" + address[8..]);
        if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return new Uri("ws://" + address[7..]);
        if (address.Contains(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
            return new Uri("wss://" + address.TrimEnd('/') + "/");
        return null;
    }

    public static (string Host, int Port) SplitHostPort(string address)
    {
        address = address.Trim();
        // [IPv6]:ポート
        if (address.StartsWith('[') && address.IndexOf(']') is var close and > 0)
        {
            var host = address[1..close];
            return address.Length > close + 2 && address[close + 1] == ':' && int.TryParse(address[(close + 2)..], out var p)
                ? (host, p)
                : (host, RoomServer.DefaultPort);
        }
        // コロンが 2 つ以上なら IPv6 のアドレスだけ（ポートなし）
        if (address.Count(c => c == ':') > 1) return (address, RoomServer.DefaultPort);
        var colon = address.LastIndexOf(':');
        return colon > 0 && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and < 65536
            ? (address[..colon], port)
            : (address, RoomServer.DefaultPort);
    }

    /// <summary>
    /// サーバー側: 受け付けた接続が WebSocket（"GET ..." で始まる）か、1 行 1 JSON の TCP かを見分ける。
    /// </summary>
    public static async Task<IMessageConnection?> AcceptAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var stream = client.GetStream();
        var first = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        if (await stream.ReadAsync(first, timeout.Token) == 0)
        {
            client.Dispose();
            return null;
        }
        if (first[0] != (byte)'G')
            return new JsonLineConnection(client, new PrefixStream(first, stream));

        // HTTP のヘッダーを読む
        var header = new List<byte>(first);
        var one = new byte[1];
        while (header.Count < 16 * 1024 && !EndsWithBlankLine(header))
        {
            if (await stream.ReadAsync(one, timeout.Token) == 0) break;
            header.Add(one[0]);
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
        var key = lines
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .FirstOrDefault();

        if (key is null)
        {
            // ブラウザでアドレスを開いた場合など
            var body = "DAW Sync のルームです。DAW Sync アプリの「ホストのアドレス」にこのアドレスを入れてください。"u8.ToArray();
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
            await stream.WriteAsync(body, ct);
            client.Dispose();
            return null;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n"), ct);
        var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = KeepAlive });
        return new WebSocketConnection(socket, client);
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';
}

/// <summary>先に読んでしまった数バイトを先頭に戻して読めるようにするストリーム。</summary>
internal sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
{
    private int _position;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsSpan(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        return inner.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return n;
        }
        return await inner.ReadAsync(buffer, ct);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
