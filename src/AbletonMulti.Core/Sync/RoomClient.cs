using System.Text.Json.Nodes;

namespace AbletonMulti.Core.Sync;

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
    // 送った順に「戻ってきたとき Live に知らせるか」。サーバーは送った順に 1 つずつ戻すので FIFO で対応が取れる
    private readonly Queue<bool> _inFlight = new();
    private readonly CancellationTokenSource _cts = new();
    private IMessageConnection? _connection;
    private TaskCompletionSource<List<Op>>? _welcome;

    public int MyId { get; private set; }
    public IReadOnlyList<Peer> Peers { get; private set; } = [];

    /// <summary>他の人の変更</summary>
    public event Action<string, IReadOnlyList<Op>>? RemoteOps;
    /// <summary>Live から来た自分の変更がサーバーを一周して戻ってきた</summary>
    public event Action<IReadOnlyList<Op>>? Echoed;
    public event Action<IReadOnlyList<Peer>>? PeersChanged;
    public event Action<string>? Disconnected;

    /// <summary>接続して、ルームの現在の状態を返す。</summary>
    public async Task<List<Op>> ConnectAsync(string address, string room, string name, string? key, TimeSpan timeout)
    {
        _connection = await Transport.ConnectAsync(address, timeout);
        _welcome = new TaskCompletionSource<List<Op>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ReadLoopAsync(_connection);
        _connection.Send(new JsonObject { ["t"] = "hello", ["proto"] = RoomServer.ProtocolVersion, ["room"] = room, ["name"] = name, ["key"] = key });
        return await _welcome.Task.WaitAsync(timeout);
    }

    /// <param name="fromLive">Live での操作なら true（戻ってきたら Echoed で知らせる）</param>
    public void Send(IReadOnlyList<Op> ops, bool fromLive)
    {
        if (_connection is null || ops.Count == 0) return;
        lock (_gate)
        {
            _inFlight.Enqueue(fromLive);
            _connection.Send(new JsonObject { ["t"] = "ops", ["ops"] = Op.ToArray(ops) });
        }
    }

    private async Task ReadLoopAsync(IMessageConnection connection)
    {
        var reason = "サーバーとの接続が切れました";
        await foreach (var msg in connection.ReadAllAsync(_cts.Token))
        {
            switch ((string?)msg["t"])
            {
                case "welcome":
                    MyId = (int?)msg["id"] ?? 0;
                    _welcome?.TrySetResult(Op.FromArray(msg["state"]));
                    break;
                case "peers":
                    Peers = (msg["peers"] as JsonArray ?? [])
                        .OfType<JsonNode>()
                        .Select(p => new Peer((int?)p["id"] ?? 0, (string?)p["name"] ?? "?"))
                        .ToList();
                    PeersChanged?.Invoke(Peers);
                    break;
                case "ops":
                    var ops = Op.FromArray(msg["ops"]);
                    if ((int?)msg["from"] == MyId)
                    {
                        bool fromLive;
                        lock (_gate) fromLive = _inFlight.TryDequeue(out var f) && f;
                        if (fromLive) Echoed?.Invoke(ops);
                    }
                    else
                    {
                        RemoteOps?.Invoke((string?)msg["name"] ?? "?", ops);
                    }
                    break;
                case "error":
                    reason = (string?)msg["msg"] ?? reason;
                    _welcome?.TrySetException(new InvalidOperationException(reason));
                    break;
            }
        }
        _welcome?.TrySetException(new IOException(reason));
        if (!_cts.IsCancellationRequested) Disconnected?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
