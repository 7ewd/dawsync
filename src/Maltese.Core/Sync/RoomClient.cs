using System.Text.Json.Nodes;

namespace Maltese.Core.Sync;

/// <summary>
/// 中継サーバーへの接続。
///
/// サーバーは自分の変更も含めて全員に同じ順番で配り直す。自分の変更が戻ってきたこと（Echoed）を
/// Live 側に伝えると、Live 側は「自分の変更が戻ってくるまでに届いた他人の変更は、サーバー上では
/// 自分より前なので無視する」ことができ、全員が最後にサーバーで一番後だった値に揃う。
/// この判定は Live 側（Remote Script）で行う。自分の操作と届いた操作の順番を正確に知っているのは Live だけだから。
/// </summary>
public sealed class RoomClient : IAsyncDisposable
{
    private readonly Lock _gate = new();
    // 送った順に「戻ってきたとき Live に知らせるか」と、送ったキー。サーバーは送った順に 1 つずつ戻すので FIFO で対応が取れる
    private readonly Queue<(bool FromLive, string[] Keys)> _inFlight = new();
    private readonly CancellationTokenSource _cts = new();
    private IMessageConnection? _connection;
    private TaskCompletionSource<List<Op>>? _welcome;

    public int MyId { get; private set; }
    public IReadOnlyList<Peer> Peers { get; private set; } = [];
    /// <summary>サーバー（ルーム）を立ち上げるたびに変わる ID。つなぎ直した先が同じルームかを確かめるのに使う</summary>
    public string? RoomSession { get; private set; }
    /// <summary>サーバーに断られて切れた（合言葉が違う等）。つなぎ直しても無駄</summary>
    public bool Refused { get; private set; }
    /// <summary>接続が終わった（Disconnected を出した後も含む）</summary>
    public bool Closed { get; private set; }

    // 生存確認: ping に pong を返すサーバーなら、しばらく何も届かないときは切れたとみなす
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SilentTimeout = TimeSpan.FromSeconds(45);
    private long _lastReceived = Environment.TickCount64;
    private bool _pongSeen;

    /// <summary>送ったがサーバーから戻ってこなかった変更のキー（切れたとき、届いていないかもしれないもの）</summary>
    public IReadOnlyList<string> UnackedKeys
    {
        get { lock (_gate) return _inFlight.SelectMany(f => f.Keys).Distinct().ToList(); }
    }

    /// <summary>他の人の変更</summary>
    public event Action<string, IReadOnlyList<Op>>? RemoteOps;
    /// <summary>Live から来た自分の変更がサーバーを一周して戻ってきた</summary>
    public event Action<IReadOnlyList<Op>>? Echoed;
    public event Action<IReadOnlyList<Peer>>? PeersChanged;
    /// <summary>
    /// 参加が認められ、ルームの今の状態が届いた。この後に届く変更（RemoteOps）より必ず先に呼ばれるので、
    /// ここで状態を受け取れば、参加の途中で届いた変更を取りこぼさない。
    /// </summary>
    public event Action<IReadOnlyList<Op>>? Welcomed;
    public event Action<string>? Disconnected;

    /// <summary>接続して、ルームの現在の状態を返す。</summary>
    public async Task<List<Op>> ConnectAsync(string address, string room, string name, string? key, TimeSpan timeout)
    {
        _connection = await Transport.ConnectAsync(address, timeout);
        _welcome = new TaskCompletionSource<List<Op>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ReadLoopAsync(_connection);
        _ = PingLoopAsync(_connection);
        _connection.Send(new JsonObject { ["t"] = "hello", ["proto"] = RoomServer.ProtocolVersion, ["room"] = room, ["name"] = name, ["key"] = key, ["app"] = AppInfo.Version });
        // 大きなプロジェクトの状態を遅い回線で受け取ることもあるので、つなぐ時間より長めに待つ
        return await _welcome.Task.WaitAsync(timeout > TimeSpan.FromSeconds(60) ? timeout : TimeSpan.FromSeconds(60));
    }

    private async Task PingLoopAsync(IMessageConnection connection)
    {
        while (!_cts.IsCancellationRequested && !Closed)
        {
            try { await Task.Delay(PingInterval, _cts.Token); }
            catch (OperationCanceledException) { return; }
            if (_pongSeen && Environment.TickCount64 - Interlocked.Read(ref _lastReceived) > SilentTimeout.TotalMilliseconds)
            {
                // 相手の PC がスリープした・回線が無言で切れた。切って、つなぎ直しを始めてもらう
                await connection.DisposeAsync();
                return;
            }
            connection.Send(new JsonObject { ["t"] = "ping" });
        }
    }

    /// <param name="fromLive">Live での操作なら true（戻ってきたら Echoed で知らせる）</param>
    public void Send(IReadOnlyList<Op> ops, bool fromLive)
    {
        if (_connection is null || ops.Count == 0) return;
        lock (_gate)
        {
            _inFlight.Enqueue((fromLive, ops.Select(o => o.Key).ToArray()));
            _connection.Send(new JsonObject { ["t"] = "ops", ["ops"] = Op.ToArray(ops) });
        }
    }

    private async Task ReadLoopAsync(IMessageConnection connection)
    {
        var reason = "サーバーとの接続が切れました";
        await foreach (var msg in connection.ReadAllAsync(_cts.Token))
        {
            Interlocked.Exchange(ref _lastReceived, Environment.TickCount64);
            try
            {
                Handle(msg, ref reason);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // 壊れたメッセージや、受け取った側（アプリ）の処理の失敗で、受信を止めない
                System.Diagnostics.Debug.WriteLine(e);
            }
        }
        Closed = true;
        _welcome?.TrySetException(new IOException(reason));
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke(reason);
    }

    private void Handle(JsonObject msg, ref string reason)
    {
        switch ((string?)msg["t"])
        {
            case "pong":
                _pongSeen = true;
                break;
            case "welcome":
                MyId = (int?)msg["id"] ?? 0;
                RoomSession = (string?)msg["sid"];
                var state = Op.FromArray(msg["state"]);
                Welcomed?.Invoke(state);
                _welcome?.TrySetResult(state);
                break;
            case "peers":
                Peers = (msg["peers"] as JsonArray ?? [])
                    .OfType<JsonNode>()
                    .Select(p => new Peer((int?)p["id"] ?? 0, (string?)p["name"] ?? "?", (string?)p["app"]))
                    .ToList();
                PeersChanged?.Invoke(Peers);
                break;
            case "ops":
                var ops = Op.FromArray(msg["ops"]);
                if ((int?)msg["from"] == MyId)
                {
                    bool fromLive;
                    lock (_gate) fromLive = _inFlight.TryDequeue(out var f) && f.FromLive;
                    if (fromLive) Echoed?.Invoke(ops);
                }
                else
                {
                    RemoteOps?.Invoke((string?)msg["name"] ?? "?", ops);
                }
                break;
            case "error":
                reason = (string?)msg["msg"] ?? reason;
                Refused = true;
                _welcome?.TrySetException(new RoomRefusedException(reason));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}

/// <summary>サーバーに断られた（合言葉やバージョンが違う）。</summary>
public sealed class RoomRefusedException(string message) : InvalidOperationException(message);
