using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Maltese.Core.Sync;

/// <summary>
/// 同じ PC の Ableton Live（Maltese Remote Script）との接続。
/// アプリ側が 127.0.0.1 で待ち受け、Live 側のスクリプトが接続してくる。
/// </summary>
public sealed class LiveBridge : IAsyncDisposable
{
    public const int DefaultPort = 47400;
    /// <summary>このアプリが対応している Remote Script の通信バージョン</summary>
    public const int ScriptProtocol = 2;

    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private JsonLineConnection? _connection;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<List<Op>>> _snapshotRequests = [];
    private readonly Dictionary<int, TaskCompletionSource> _adoptRequests = [];
    private int _nextRequestId;

    /// <summary>Live が接続してきた（引数は Live のバージョン）</summary>
    public event Action<string>? Connected;
    public event Action? Disconnected;
    /// <summary>Live 上でユーザーが何かを変更した</summary>
    public event Action<IReadOnlyList<Op>>? LocalOps;
    public event Action<string>? Warning;

    public bool IsConnected => _connection is not null;
    public string? LiveVersion { get; private set; }

    /// <summary>つながっている DAW（"live"・"bitwig"・"reaper"）。</summary>
    public string Daw { get; private set; } = "live";

    /// <summary>画面に出す DAW の名前（「Ableton Live 12.4.6」「Bitwig Studio 6.0」など）。</summary>
    public string DawName => Daw == "live" ? $"Ableton Live {LiveVersion}".TrimEnd() : LiveVersion ?? Daw;
    public string? ScriptVersion { get; private set; }
    public int ScriptProtocolVersion { get; private set; }
    /// <summary>Live で使える機能の報告（不具合の調査用）</summary>
    public JsonObject? ApiReport { get; private set; }
    public event Action<JsonObject>? ApiReported;
    public int Port { get; private set; }

    /// <summary>
    /// 先に別の DAW がつながっていたので待ってもらっている DAW の名前（最後に来てから 10 秒たったら null）。
    /// 同じ PC で Live と REAPER などを両方開いていても、取り合いにならないようにするため。
    /// </summary>
    public string? WaitingDaw => DateTime.UtcNow - _waitingSince < TimeSpan.FromSeconds(10) ? _waitingDaw : null;
    private string? _waitingDaw;
    private DateTime _waitingSince = DateTime.MinValue;
    public event Action? WaitingChanged;

    public void Start(int port = DefaultPort)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = AcceptLoopAsync(_listener);
    }

    /// <param name="force">
    /// false: 他の人の変更。Live 側で、自分の変更がまだサーバーから戻っていないキーは無視される。
    /// true: ルームの状態に合わせるとき。必ず反映する。
    /// </param>
    public void Apply(IReadOnlyList<Op> ops, bool force = false)
    {
        if (ops.Count > 0)
            _connection?.Send(new JsonObject { ["t"] = "apply", ["force"] = force, ["ops"] = Op.ToArray(ops) });
    }

    /// <summary>Live から来た変更の処理が終わった（サーバーから戻ってきた、または送らずに捨てた）ことを知らせる。</summary>
    public void Ack(IEnumerable<string> keys) =>
        _connection?.Send(new JsonObject { ["t"] = "ack", ["keys"] = new JsonArray(keys.Select(k => (JsonNode)k).ToArray()) });

    /// <summary>ルームから抜けたので、戻ってくるのを待っている変更はもう戻ってこない。</summary>
    public void ResetPending() => _connection?.Send(new JsonObject { ["t"] = "reset" });

    /// <summary>Live の現在の状態（同期対象の全キー）を取得する。</summary>
    public async Task<List<Op>> RequestSnapshotAsync(TimeSpan timeout)
    {
        var connection = _connection ?? throw new InvalidOperationException("Live が接続されていません");
        var tcs = new TaskCompletionSource<List<Op>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            id = ++_nextRequestId;
            _snapshotRequests[id] = tcs;
        }
        connection.Send(new JsonObject { ["t"] = "snapshot_req", ["id"] = id });
        try
        {
            return await tcs.Task.WaitAsync(timeout, _cts.Token);
        }
        finally
        {
            lock (_gate) _snapshotRequests.Remove(id);
        }
    }

    /// <summary>Live 側のトラックやデバイスの ID を、ルームの ID に付け替えてもらう。</summary>
    public Task AdoptAsync(IReadOnlyDictionary<string, string> mapping, TimeSpan timeout)
    {
        var map = new JsonObject();
        foreach (var (from, to) in mapping) map[from] = to;
        return RequestAsync(new JsonObject { ["t"] = "adopt", ["map"] = map }, timeout);
    }

    /// <summary>Live で開いているセットを「まっさら」（MIDI トラック 1 本だけ）にしてもらう。</summary>
    public Task MakeBlankAsync(TimeSpan timeout) => RequestAsync(new JsonObject { ["t"] = "blank" }, timeout);

    /// <summary>Live にお願いを送り、終わった（"adopted" / "blanked" が返ってきた）のを待つ。</summary>
    private async Task RequestAsync(JsonObject message, TimeSpan timeout)
    {
        var connection = _connection ?? throw new InvalidOperationException("Live が接続されていません");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (_gate)
        {
            id = ++_nextRequestId;
            _adoptRequests[id] = tcs;
        }
        message["id"] = id;
        connection.Send(message);
        try
        {
            await tcs.Task.WaitAsync(timeout, _cts.Token);
        }
        finally
        {
            lock (_gate) _adoptRequests.Remove(id);
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = HandleAsync(new JsonLineConnection(client));
        }
    }

    private async Task HandleAsync(JsonLineConnection connection)
    {
        var helloReceived = false;
        await foreach (var msg in connection.ReadAllAsync(_cts.Token))
        {
            try
            {
                switch ((string?)msg["t"])
                {
                    case "hello":
                        var daw = (string?)msg["daw"] ?? "live";
                        if (_connection is { } current && current != connection && daw != Daw)
                        {
                            // 別の DAW がもうつながっている。先の方を使い続け、後から来た方には待ってもらう
                            // （DAW 側は 2 秒ごとにつなぎ直してくるので、先の方が終われば自然に代わる）
                            var waiting = _waitingDaw;
                            _waitingDaw = (string?)msg["live"] ?? daw;
                            _waitingSince = DateTime.UtcNow;
                            if (waiting != _waitingDaw) WaitingChanged?.Invoke();
                            connection.Send(new JsonObject { ["t"] = "busy", ["daw"] = DawName });
                            await Task.Delay(200);
                            await connection.DisposeAsync();
                            return;
                        }
                        // 同じ DAW（Live はセットを開き直すたびにスクリプトを作り直す）なら、新しい接続が来たら古い方は捨てる
                        var old = Interlocked.Exchange(ref _connection, connection);
                        if (old is not null && old != connection) await old.DisposeAsync();
                        LiveVersion = (string?)msg["live"];
                        Daw = (string?)msg["daw"] ?? "live";
                        ScriptVersion = (string?)msg["script"];
                        ScriptProtocolVersion = (int?)msg["proto"] ?? 1;
                        helloReceived = true;
                        Connected?.Invoke(DawName);
                        break;
                    case "ops" when helloReceived:
                        LocalOps?.Invoke(Op.FromArray(msg["ops"]));
                        break;
                    case "snapshot" when helloReceived:
                        TaskCompletionSource<List<Op>>? tcs;
                        lock (_gate) _snapshotRequests.TryGetValue((int?)msg["id"] ?? -1, out tcs);
                        tcs?.TrySetResult(Op.FromArray(msg["ops"]));
                        break;
                    case "adopted" or "blanked" when helloReceived:
                        TaskCompletionSource? adopted;
                        lock (_gate) _adoptRequests.TryGetValue((int?)msg["id"] ?? -1, out adopted);
                        adopted?.TrySetResult();
                        break;
                    case "api" when helloReceived:
                        ApiReport = msg["info"] as JsonObject;
                        if (ApiReport is not null) ApiReported?.Invoke(ApiReport);
                        break;
                    case "warn" when helloReceived:
                        Warning?.Invoke((string?)msg["msg"] ?? "");
                        break;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                // 1 つのメッセージの処理の失敗で、DAW との接続を止めない（止まると、つながったまま何も届かなくなる）
                Warning?.Invoke($"DAW からのメッセージを処理できませんでした: {e.Message}");
            }
        }

        if (Interlocked.CompareExchange(ref _connection, null, connection) == connection)
        {
            LiveVersion = null;
            Disconnected?.Invoke();
        }
        await connection.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener?.Stop();
        if (_connection is { } c) await c.DisposeAsync();
        _cts.Dispose();
    }
}
