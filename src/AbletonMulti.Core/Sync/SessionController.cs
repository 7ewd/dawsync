using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// 自分の Live（LiveBridge）とルーム（RoomClient）をつなぐ。
/// - Live での変更 → ファイルをハッシュに置き換え（必要ならアップロード）→ ルームへ送る
/// - ルームから届いた他人の変更 → ファイルをダウンロードしてパスに置き換え → Live に反映
/// - ルームに入った時 / Live が接続された時 → Live をルームの状態に合わせる
/// </summary>
public sealed class SessionController : IAsyncDisposable
{
    private abstract record Incoming;
    private sealed record RemoteBatch(List<Op> Ops) : Incoming;
    private sealed record AckBatch(List<string> Keys) : Incoming;
    // 「送ったがまだ戻ってきていない」印を消す。前の接続の ack がまだ列に残っていることがあるので、同じ列に並べる
    private sealed record ResetBatch : Incoming;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, JsonNode?> _roomState = [];
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Channel<IReadOnlyList<Op>> _outgoing = Channel.CreateUnbounded<IReadOnlyList<Op>>(new() { SingleReader = true });
    // 他人の変更と「自分の変更が戻ってきた」知らせは、サーバーから届いた順番のまま Live に渡す必要があるので同じ列に並べる
    private readonly Channel<Incoming> _incoming = Channel.CreateUnbounded<Incoming>(new() { SingleReader = true });
    // 書き込み中のファイル（バウンス・録音）を含む変更は、書き終わるまで別の列で待たせる
    private readonly Channel<IReadOnlyList<Op>> _deferred = Channel.CreateUnbounded<IReadOnlyList<Op>>(new() { SingleReader = true });
    private readonly Dictionary<string, int> _deferredKeys = [];
    private readonly CancellationTokenSource _cts = new();
    private RoomServer? _server;
    private RoomClient? _client;
    private CloudflareTunnel? _tunnel;
    private BlobClient? _blobs;
    private volatile bool _waitingForHost;
    // ルームを作る／入るときに「まっさらなセットで始める」を選んだ（Live がつながったらまっさらにする）
    private volatile bool _blankPending;
    // まっさらにしてから入ったので、次にルームに合わせるときは確認なしで上書きしてよい
    private volatile bool _overwriteNext;
    // DAW がつながってから、ルームの状態に合わせ終わったか。合わせる前は ID の対応が付いていないので、
    // 相手の変更をそのまま DAW に渡すと別のトラックとして作られてしまう（合わせるときにまとめて反映される）
    private volatile bool _liveSynced;
    // つなぎ直すのに使う（最後に入ったルーム）
    private (string Name, string? Key, string Room, string? Session)? _joined;
    private CancellationTokenSource? _reconnect;
    // つなぎ直している間に自分が変えたところ（つながったら、その時点の値をルームに送る）
    private readonly HashSet<string> _offlineKeys = [];
    // ルームの状態に合わせている間に DAW から届いた自分の変更（合わせ終わってから、送るか捨てるかを決める）
    private readonly List<(IReadOnlyList<Op> Ops, bool BeforeAdopt)> _held = [];
    private bool _syncing;
    private bool _adopted;

    public LiveBridge Bridge { get; } = new();
    public string DataDirectory { get; }
    public FileStore Files { get; }
    public bool IsHost => _server is not null;
    public bool InRoom => _client is not null || Reconnecting || _server is not null;
    /// <summary>ルームとの通信が切れて、つなぎ直している最中</summary>
    public bool Reconnecting => _reconnect is not null;
    public int? HostPort => _server?.Port;
    public string? RoomAddress { get; private set; }
    public string MyName { get; private set; } = "";
    public IReadOnlyList<Peer> Peers => _client?.Peers ?? [];
    public int MyId => _client?.MyId ?? 0;
    public bool WaitingForHost => _waitingForHost;
    /// <summary>ホストのとき、参加に必要な合言葉</summary>
    public string? RoomKey { get; private set; }
    /// <summary>インターネットに公開中のアドレス（xxx.trycloudflare.com）</summary>
    public string? PublicAddress => _tunnel?.PublicAddress;

    /// <summary>参加する人に送る招待コード（アドレス#合言葉）</summary>
    public string? InviteCode => PublicAddress is { } a && RoomKey is { } k ? $"{a}#{k}" : null;

    /// <summary>一時停止中に「ルームの内容でこのセットを上書き」できるか</summary>
    public bool CanOverwrite { get; private set; }

    private string _dawName = "DAW";
    private readonly HashSet<string> _versionWarned = [];

    /// <summary>
    /// ルームの人とアプリのバージョンが違ったら一度だけ知らせる（違うと、同時に編集したときの
    /// 合わせ方などが食い違って、うまく同期しないことがある）。
    /// </summary>
    private void CheckPeerVersions(int myId, IReadOnlyList<Peer> peers)
    {
        foreach (var peer in peers)
        {
            if (peer.Id == myId || AppInfo.Compatible(peer.Version, AppInfo.Version)) continue;
            lock (_versionWarned)
                if (!_versionWarned.Add($"{peer.Name}\n{peer.Version}")) continue;
            var theirs = peer.Version is null ? "古いバージョン" : $"バージョン {peer.Version}";
            var fix = AppInfo.Compare(peer.Version, AppInfo.Version) > 0
                ? "あなたのアプリを新しくしてください"
                : $"{peer.Name} さんにアプリを新しくしてもらってください";
            Log(ActivityKind.Warning, "", $"{peer.Name} さんのアプリは{theirs}で、あなた（{AppInfo.Version}）と違います。" +
                $"{fix}（違うままだと、うまく同期しないことがあります）");
        }
    }

    /// <summary>同期を止めている理由（開いているセットが違う等）。null なら同期中。</summary>
    public string? PausedReason { get; private set; }

    /// <summary>接続状態・参加者などが変わった（UI の更新用）</summary>
    public event Action? Changed;
    public event Action<ActivityEntry>? Activity;

    /// <param name="dataDirectory">Live の機能の報告などを置く場所（テストで複数立ち上げるとき用）</param>
    public SessionController(FileStore? files = null, string? dataDirectory = null)
    {
        Files = files ?? new FileStore();
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbletonMulti");
        Bridge.Connected += name =>
        {
            _liveSynced = false;
            _dawName = name;
            Log(ActivityKind.System, "", $"{name} と接続しました");
            Changed?.Invoke();
            _ = SyncWithLiveAsync();
        };
        Bridge.Disconnected += () =>
        {
            _liveSynced = false;
            Log(ActivityKind.System, "", $"{_dawName} との接続が切れました");
            Changed?.Invoke();
        };
        Bridge.Warning += message => Log(ActivityKind.Warning, "", message);
        Bridge.WaitingChanged += () =>
        {
            if (Bridge.WaitingDaw is { } waiting)
                Log(ActivityKind.Warning, "", $"{waiting} も開いていますが、先につながった {Bridge.DawName} を使っています" +
                    $"（{waiting} で使うときは、{Bridge.DawName} を閉じてください）");
            Changed?.Invoke();
        };
        Bridge.ApiReported += SaveApiReport;
        Bridge.LocalOps += OnLocalOps;
        _ = Task.Run(OutgoingLoopAsync);
        _ = Task.Run(DeferredLoopAsync);
        _ = Task.Run(FileWatchLoopAsync);
        _ = Task.Run(IncomingLoopAsync);
    }

    public bool ScriptOutdated => Bridge.IsConnected && Bridge.ScriptProtocolVersion != LiveBridge.ScriptProtocol;

    public void StartBridge(int port = LiveBridge.DefaultPort)
    {
        Bridge.Start(port);
        // REAPER のスクリプトはファイルでやりとりするので、中継して同じポートにつなぐ
        _reaper ??= new ReaperRelay(port);
        _reaper.Start();
    }

    private ReaperRelay? _reaper;

    // ---- ルーム ----

    /// <param name="startBlank">Live のセットをまっさら（MIDI トラック 1 本）にしてから始める</param>
    public async Task HostAsync(string name, int port = RoomServer.DefaultPort, string? key = null, bool startBlank = false)
    {
        _blankPending = startBlank;
        var server = new RoomServer { Key = RoomServer.NormalizeKey(key ?? RoomServer.GenerateKey()) };
        server.Start(port);
        _server = server;
        try
        {
            RoomKey = server.Key;
            await JoinAsync($"127.0.0.1:{port}", name, server.Key);
        }
        catch
        {
            _server = null;
            RoomKey = null;
            await server.DisposeAsync();
            throw;
        }
    }

    /// <param name="address">192.168.1.10、100.x.x.x:47401、xxx.trycloudflare.com など</param>
    public async Task JoinAsync(string address, string name, string? key, string room = "default", bool startBlank = false)
    {
        key = RoomServer.NormalizeKey(key);
        if (_server is null) _blankPending = startBlank;
        try
        {
            await ConnectRoomAsync(address, name, key, room, rejoin: false, CancellationToken.None);
        }
        catch when (_server is null)
        {
            // 入れなかったのに「まっさらにする」予定が残ると、次に Live がつながったときに困るので取り消す
            _blankPending = false;
            throw;
        }
        await SyncWithLiveAsync();
    }

    /// <param name="rejoin">切れたルームにつなぎ直す（切れている間の自分の変更をルームに送る）</param>
    /// <returns>つなぎ直したとき、ルームに送り直すキー（切れている間に自分が変えたところ）</returns>
    private async Task<HashSet<string>?> ConnectRoomAsync(string address, string name, string? key, string room, bool rejoin, CancellationToken ct)
    {
        // 別のルーム（つなぎ直しでも）の状態に合わせ終わるまでは、届いた変更を DAW に渡さない
        _liveSynced = false;
        var client = new RoomClient();
        client.RemoteOps += OnRemoteOps;
        client.Echoed += ops =>
        {
            Trace("echo", ops);
            _incoming.Writer.TryWrite(new AckBatch(ops.Select(o => o.Key).ToList()));
        };
        client.PeersChanged += peers =>
        {
            CheckPeerVersions(client.MyId, peers);
            Changed?.Invoke();
        };
        client.Disconnected += reason => _ = OnRoomLostAsync(client, reason);
        // ルームの状態は届いたその場で入れ替える（この後すぐ届く変更が、入れ替えで消えないように）
        client.Welcomed += state =>
        {
            lock (_gate)
            {
                _roomState.Clear();
                foreach (var op in state) _roomState[op.Key] = op.Value;
            }
        };

        var blobs = new BlobClient();
        try
        {
            await client.ConnectAsync(address, room, name, key, TimeSpan.FromSeconds(15));
            await blobs.ConnectAsync(address, room, key, TimeSpan.FromSeconds(15));
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            await client.DisposeAsync();
            await blobs.DisposeAsync();
            throw;
        }

        HashSet<string>? keep = null;
        if (rejoin)
        {
            lock (_offlineKeys)
            {
                // 同じルームに戻れたときだけ、切れている間の変更を送る（ホストがルームを作り直していたら ID が合わない）
                if (client.RoomSession is not null && client.RoomSession == _joined?.Session) keep = [.. _offlineKeys];
                else if (_offlineKeys.Count > 0)
                    Log(ActivityKind.Warning, "", "ホストがルームを作り直したので、切れている間の変更はルームに送りませんでした");
                _offlineKeys.Clear();
            }
        }
        _blobs = blobs;
        _client = client;
        if (ct.IsCancellationRequested)
        {
            // つないでいる間に「退出」が押された
            if (Interlocked.CompareExchange(ref _client, null, client) == client) _blobs = null;
            await client.DisposeAsync();
            await blobs.DisposeAsync();
            throw new OperationCanceledException(ct);
        }
        if (rejoin) _reconnect = null;
        _joined = (name, key, room, client.RoomSession);
        MyName = name;
        RoomAddress = address;
        PausedReason = null;
        _waitingForHost = false;
        Log(ActivityKind.System, "", rejoin ? "ルームにつなぎ直しました" : IsHost ? "ルームを作りました" : "ルームに参加しました");
        Changed?.Invoke();
        // 参加が認められた直後（_client に入れる前）に切れていたら、切れたときの処理が空振りしているのでやり直す
        if (client.Closed) _ = OnRoomLostAsync(client, "サーバーとの接続が切れました");
        return keep;
    }

    /// <summary>ホストしているルームをインターネットに公開する（初回は cloudflared をダウンロードする）。</summary>
    public async Task<string> PublishAsync(IProgress<(long Done, long? Total)>? downloadProgress = null)
    {
        if (_server is not { } server) throw new InvalidOperationException("ルームを作ってから公開してください");
        // 退出したら、ダウンロードや公開の途中でもやめる（閉じたルームを公開しない）
        using var publish = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _publishCts = publish;
        if (!CloudflareTunnel.IsInstalled)
        {
            Log(ActivityKind.System, "", "インターネット公開用のツール（Cloudflare 公式 cloudflared）をダウンロードしています…");
            await CloudflareTunnel.EnsureInstalledAsync(downloadProgress, publish.Token);
        }
        if (_server != server) throw new OperationCanceledException("ルームを閉じたので公開をやめました");
        var tunnel = _tunnel;
        if (tunnel is null)
        {
            tunnel = _tunnel = new CloudflareTunnel();
            tunnel.Stopped += () =>
            {
                Log(ActivityKind.Warning, "", "インターネットへの公開が止まりました");
                Changed?.Invoke();
            };
        }
        var address = await tunnel.StartAsync(server.Port, TimeSpan.FromSeconds(60), publish.Token);
        Log(ActivityKind.System, "", $"インターネットに公開しました: {address}");
        Changed?.Invoke();
        return address;
    }

    public async Task UnpublishAsync()
    {
        var tunnel = Interlocked.Exchange(ref _tunnel, null);
        if (tunnel is null) return;
        await tunnel.DisposeAsync();
        Log(ActivityKind.System, "", "インターネットへの公開をやめました");
        Changed?.Invoke();
    }

    private CancellationTokenSource? _publishCts;

    public async Task LeaveAsync()
    {
        try { _publishCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (Interlocked.Exchange(ref _reconnect, null) is { } reconnect) await reconnect.CancelAsync();
        lock (_offlineKeys) _offlineKeys.Clear();
        lock (_fileWatches) _fileWatches.Clear();
        _joined = null;
        _liveSynced = false;
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null) await client.DisposeAsync();
        var blobs = Interlocked.Exchange(ref _blobs, null);
        if (blobs is not null) await blobs.DisposeAsync();
        _incoming.Writer.TryWrite(new ResetBatch());
        // 公開をやめる前にルームを閉じる（インターネットから参加している人にも「閉じた」と届くように）
        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null) await server.DisposeAsync();
        await UnpublishAsync();
        RoomAddress = null;
        RoomKey = null;
        PausedReason = null;
        _waitingForHost = false;
        _blankPending = false;
        _overwriteNext = false;
        lock (_gate) _roomState.Clear();
        Log(ActivityKind.System, "", "ルームから退出しました");
        Changed?.Invoke();
    }

    private async Task OnRoomLostAsync(RoomClient client, string reason)
    {
        if (Interlocked.CompareExchange(ref _client, null, client) != client) return;
        // つなぎ直すかどうかは、待ち（切断の後片付け）より先に決める。
        // 先に決めないと、その間に DAW で変えたところが「切れている間の変更」として覚えられずに消える
        var canRejoin = !client.Refused && _joined is not null && RoomAddress is not null;
        CancellationTokenSource? cts = null;
        if (canRejoin)
        {
            lock (_offlineKeys) _offlineKeys.UnionWith(client.UnackedKeys);
            cts = new CancellationTokenSource();
            _reconnect = cts;
        }
        await client.DisposeAsync();
        var blobs = Interlocked.Exchange(ref _blobs, null);
        if (blobs is not null) await blobs.DisposeAsync();
        _incoming.Writer.TryWrite(new ResetBatch());
        Log(ActivityKind.Warning, "", reason);

        if (cts is null || _joined is not { } joined || RoomAddress is not { } address)
        {
            RoomAddress = null;
            if (IsHost) Pause("ルームとの接続が切れました。「退出」してから、もう一度ルームを作ってください。");
            Changed?.Invoke();
            return;
        }

        // 回線が一瞬切れただけのことが多いので、同じルームにつなぎ直す。届いたか分からない変更も、つながったら送り直す
        Changed?.Invoke();
        Log(ActivityKind.System, "", "ルームにつなぎ直しています…", "reconnect");
        HashSet<string>? keep = null;
        var delay = TimeSpan.FromSeconds(1);
        var giveUp = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                keep = await ConnectRoomAsync(address, joined.Name, joined.Key, joined.Room, rejoin: true, cts.Token);
                break;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (RoomRefusedException e)
            {
                // ホストがルームを作り直して合言葉が変わった等
                Log(ActivityKind.Warning, "", $"ルームにつなぎ直せませんでした: {e.Message}");
                break;
            }
            catch (Exception)
            {
                if (DateTime.UtcNow > giveUp)
                {
                    Log(ActivityKind.Warning, "", "ルームにつなぎ直せませんでした。ホストがルームを開いているか確かめて、もう一度参加してください");
                    break;
                }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
            }
        }
        if (_client is not null && !cts.IsCancellationRequested)
        {
            await SyncWithLiveAsync(keep: keep);
            return;
        }
        if (Interlocked.CompareExchange(ref _reconnect, null, cts) == cts)
        {
            RoomAddress = null;
            _joined = null;
            lock (_offlineKeys) _offlineKeys.Clear();
            if (IsHost) Pause("ルームとの接続が切れました。「退出」してから、もう一度ルームを作ってください。");
            Changed?.Invoke();
        }
    }

    // ---- ルームの状態に合わせる ----

    /// <summary>自分の Live をルームの状態に合わせ直す（ルームが空なら自分の状態をルームに入れる）。</summary>
    /// <param name="overwrite">
    /// トラック数などが違っていても、このセットをルームの内容で上書きする（ユーザーが確認したとき）。
    /// 空のセットから参加して、ルームの内容を丸ごと受け取ることもできる。
    /// </param>
    /// <param name="keep">
    /// ルームの値ではなく、このセットの今の値をルームに送るキー（つなぎ直したとき、切れている間に自分が変えたところ）
    /// </param>
    public async Task SyncWithLiveAsync(bool overwrite = false, IReadOnlySet<string>? keep = null)
    {
        if (_client is not { } client || !Bridge.IsConnected) return;
        if (ScriptOutdated)
        {
            Pause(Bridge.Daw switch
            {
                "bitwig" => "Bitwig の拡張が古いバージョンです。アプリの「Bitwig の拡張を更新する」を押してください。",
                "reaper" => "REAPER のスクリプトが古いバージョンです。アプリの「REAPER のスクリプトを更新する」を押してから REAPER を再起動してください。",
                _ => "Live のスクリプトが古いバージョンです。アプリの「Live のスクリプトを更新する」を押してから Live を再起動してください。",
            });
            return;
        }
        await _syncLock.WaitAsync();
        lock (_held)
        {
            _syncing = true;
            _adopted = false;
        }
        // 合わせ終わったあと、預かった自分の変更のうちどれを送るか（null なら全部捨てる）
        Func<Op, bool, bool>? release = null;
        try
        {
            if (_blankPending)
            {
                await Bridge.MakeBlankAsync(TimeSpan.FromSeconds(30));
                _blankPending = false;
                if (!IsHost) _overwriteNext = true;
                Log(ActivityKind.System, "", Bridge.Daw switch
                {
                    "bitwig" => "Bitwig のプロジェクトをまっさら（楽器トラック 1 本）にしました",
                    "reaper" => "REAPER のプロジェクトをまっさら（トラック 1 本）にしました",
                    _ => "Live のセットをまっさら（MIDI トラック 1 本）にしました",
                });
            }
            if (_overwriteNext) overwrite = true;

            Dictionary<string, JsonNode?> room;
            lock (_gate) room = new Dictionary<string, JsonNode?>(_roomState);

            if (room.Count == 0 && !IsHost)
            {
                // ルームの最初の状態はホストのセットから作る。ゲストはホストの Live がつながるのを待つ
                _waitingForHost = true;
                Log(ActivityKind.System, "", "ホストの DAW（Live・Bitwig・REAPER）が接続されるのを待っています");
                Changed?.Invoke();
                return;
            }

            var live = await Bridge.RequestSnapshotAsync(TimeSpan.FromSeconds(30));

            if (room.Count == 0)
            {
                var files = live.Count(o => o.Value?.ToJsonString().Contains("\"file\"", StringComparison.Ordinal) == true);
                if (files > 0) Log(ActivityKind.System, "", $"サンプルなどのファイル（{files} 個）を共有しています…");
                var seeded = await ToRoomAsync(live, upload: true);
                lock (_gate)
                    foreach (var op in seeded) _roomState[op.Key] = op.Value;
                client.Send(seeded, fromLive: false);
                PausedReason = null;
                _liveSynced = true;
                release = (_, _) => true;  // このセットの ID のままルームを作ったので、合わせている間の変更もそのまま送れる
                Log(ActivityKind.System, "", "このセットの状態をルームに共有しました");
                Changed?.Invoke();
                return;
            }

            var liveRoom = ToDictionary(await ToRoomAsync(live, upload: false));
            if (keep is { Count: > 0 })
            {
                // 切れている間の変更は、今のこのセットの値でルームを上書きする（消したものは消す）
                var mine = await ToRoomAsync(keep
                    .Where(k => liveRoom.ContainsKey(k) || room.ContainsKey(k))
                    .Select(k => new Op(k, liveRoom.ContainsKey(k) ? live.LastOrDefault(o => o.Key == k)?.Value : null))
                    .Where(op => op.Value is not null || room.ContainsKey(op.Key))
                    .ToList(), upload: true);
                if (mine.Count > 0)
                {
                    UpdateRoomState(mine);
                    lock (_gate) room = new Dictionary<string, JsonNode?>(_roomState);
                    client.Send(mine, fromLive: false);
                    Log(ActivityKind.System, "", $"切れている間の変更（{mine.Count} か所）をルームに送りました");
                }
            }
            if (!overwrite && CheckSameSet(room, liveRoom) is { } problem)
            {
                Pause(problem, canOverwrite: true);
                return;
            }

            // 同じセットでも ID は別々に付いているので、並びと種類が同じものをルームの ID に付け替えてもらう
            var mapping = BuildAdoptMap(room, liveRoom);
            if (mapping.Any(kv => kv.Key != kv.Value) || liveRoom.Keys.Any(k => IsStructural(k) && !room.ContainsKey(k)))
            {
                await Bridge.AdoptAsync(mapping, TimeSpan.FromSeconds(10));
                lock (_held) _adopted = true;
                live = await Bridge.RequestSnapshotAsync(TimeSpan.FromSeconds(30));
                liveRoom = ToDictionary(await ToRoomAsync(live, upload: false));
            }
            else
            {
                lock (_held) _adopted = true;
            }

            var diff = liveRoom.Keys
                .Where(k => !room.ContainsKey(k) && IsStructural(k))
                .Select(k => new Op(k, null))
                .Concat(room
                    .Where(kv => !liveRoom.TryGetValue(kv.Key, out var current) || !JsonNode.DeepEquals(current, kv.Value))
                    .Select(kv => new Op(kv.Key, kv.Value)))
                .ToList();
            Bridge.Apply(await ToLocalAsync(diff), force: true);
            _liveSynced = true;
            // 合わせている間の自分の変更は、ID を付け替えた後のもので、ルームの値で上書きしなかったところだけ送る
            var overwritten = diff.Select(o => o.Key).ToHashSet();
            release = (op, beforeAdopt) => !beforeAdopt && !overwritten.Contains(op.Key);
            _waitingForHost = false;
            _overwriteNext = false;
            PausedReason = null;
            CanOverwrite = false;
            Log(ActivityKind.System, "", diff.Count == 0 ? "ルームと同じ状態です" : $"ルームの状態に合わせました（{diff.Count} か所）");
            Changed?.Invoke();
        }
        catch (Exception e) when (!_cts.IsCancellationRequested)
        {
            // 合わせ終わっていないまま相手の変更を反映すると壊れるので、止めて「もう一度合わせる」を出す
            Pause($"ルームの状態に合わせられませんでした（{e.Message}）。「もう一度合わせる」を押してください。");
        }
        finally
        {
            ReleaseHeld(release);
            _syncLock.Release();
        }
    }

    /// <summary>合わせている間に預かった自分の変更を、送るものは送る列へ、それ以外は処理済みとして DAW に伝える。</summary>
    private void ReleaseHeld(Func<Op, bool, bool>? release)
    {
        List<(IReadOnlyList<Op> Ops, bool BeforeAdopt)> held;
        lock (_held)
        {
            _syncing = false;
            held = [.. _held];
            _held.Clear();
        }
        foreach (var (ops, beforeAdopt) in held)
        {
            var send = release is null ? [] : ops.Where(o => release(o, beforeAdopt)).ToList();
            var drop = ops.Where(o => !send.Contains(o)).Select(o => o.Key).ToList();
            if (drop.Count > 0) Bridge.Ack(drop);
            if (send.Count > 0) _outgoing.Writer.TryWrite(send);
        }
    }

    private static Dictionary<string, JsonNode?> ToDictionary(IEnumerable<Op> ops) =>
        ops.GroupBy(o => o.Key).ToDictionary(g => g.Key, g => g.Last().Value);

    /// <summary>トラック・デバイス・クリップなど「あるかないか」のキー（ルームに無ければ消すべきもの）</summary>
    private static bool IsStructural(string key)
    {
        var parts = key.Split('/');
        return (parts.Length == 2 && parts[0] is "t" or "r" or "s" or "d") || parts[0] == "c";
    }

    /// <summary>"t" などの並びを、並び順 o の順に並べた ID の一覧</summary>
    public static List<string> Ordered(IReadOnlyDictionary<string, JsonNode?> state, string prefix, string? at = null) =>
        state
            .Where(kv =>
            {
                var parts = kv.Key.Split('/');
                return parts.Length == 2 && parts[0] == prefix && kv.Value is JsonObject
                       && (at is null || (string?)kv.Value["at"] == at);
            })
            .OrderBy(kv => (double?)kv.Value!["o"] ?? 0)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key.Split('/')[1])
            .ToList();

    /// <summary>ルームと Live で開いているセットが同じかを、トラックの数と名前でざっくり確かめる。</summary>
    private static string? CheckSameSet(Dictionary<string, JsonNode?> room, Dictionary<string, JsonNode?> live)
    {
        var roomTracks = Ordered(room, "t");
        var liveTracks = Ordered(live, "t");
        if (roomTracks.Count != liveTracks.Count)
            return $"ルームとトラック数が違います（ルーム: {roomTracks.Count}、このセット: {liveTracks.Count}）。同じセットを開いてから「もう一度合わせる」を押してください。";
        var same = roomTracks.Zip(liveTracks).Count(p =>
            (string?)room.GetValueOrDefault($"t/{p.First}/name") == (string?)live.GetValueOrDefault($"t/{p.Second}/name"));
        if (roomTracks.Count > 0 && same * 2 < roomTracks.Count)
            return "ルームと違うセットを開いているようです（トラック名がほとんど一致しません）。同じセットを開いてから「もう一度合わせる」を押してください。";
        return null;
    }

    /// <summary>並びと種類が同じものを、Live の ID → ルームの ID として対応付ける。</summary>
    public static Dictionary<string, string> BuildAdoptMap(IReadOnlyDictionary<string, JsonNode?> room, IReadOnlyDictionary<string, JsonNode?> live)
    {
        var map = new Dictionary<string, string>();

        List<(string Live, string Room)> Pair(List<string> l, List<string> r)
        {
            if (l.Count != r.Count) return [];
            var pairs = l.Zip(r).ToList();
            // 同じ ID でも入れる（ここに無いものは Live 側で新しい ID に振り直される）
            foreach (var (a, b) in pairs) map[a] = b;
            return pairs;
        }

        // トラックの数が違う（途中参加でホストが増やした等）ときは、名前と種類が同じものを前から順に対応付ける
        List<(string Live, string Room)> PairByName(string prefix)
        {
            var l = Ordered(live, prefix);
            var r = Ordered(room, prefix);
            if (l.Count == r.Count) return Pair(l, r);
            var pairs = new List<(string, string)>();
            var next = 0;
            foreach (var a in l)
            {
                var name = (string?)live.GetValueOrDefault($"{prefix}/{a}/name");
                var kind = (string?)live[$"{prefix}/{a}"]?["k"];
                for (var j = next; j < r.Count; j++)
                {
                    if ((string?)room.GetValueOrDefault($"{prefix}/{r[j]}/name") != name || (string?)room[$"{prefix}/{r[j]}"]?["k"] != kind)
                        continue;
                    pairs.Add((a, r[j]));
                    map[a] = r[j];
                    next = j + 1;
                    break;
                }
            }
            return pairs;
        }

        void MatchDevices(string liveAt, string roomAt)
        {
            var l = Ordered(live, "d", liveAt);
            var r = Ordered(room, "d", roomAt);
            if (l.Count != r.Count) return;
            for (var i = 0; i < l.Count; i++)
            {
                var a = live[$"d/{l[i]}"]!;
                var b = room[$"d/{r[i]}"]!;
                if ((string?)a["k"] != (string?)b["k"] || (string?)a["n"] != (string?)b["n"]) return;
            }
            foreach (var (a, b) in Pair(l, r))
                for (var ci = 0; live.ContainsKey($"d/{a}/c/{ci}/name"); ci++)
                    MatchDevices($"d/{a}/c/{ci}", $"d/{b}/c/{ci}");
        }

        foreach (var (a, b) in PairByName("t"))
            MatchDevices($"t/{a}", $"t/{b}");
        foreach (var (a, b) in PairByName("r"))
            MatchDevices($"r/{a}", $"r/{b}");
        // シーンは名前が無いことが多いので、前から少ない方の数だけ対応付ける（そのシーンのクリップが残る）
        var ls = Ordered(live, "s");
        var rs = Ordered(room, "s");
        var n = Math.Min(ls.Count, rs.Count);
        Pair(ls.Take(n).ToList(), rs.Take(n).ToList());
        MatchDevices("m", "m");
        return map;
    }

    // ---- 送る ----

    private bool CanSend => _client is not null && PausedReason is null && !_waitingForHost;

    private void OnLocalOps(IReadOnlyList<Op> ops)
    {
        Trace("local", ops);
        lock (_held)
        {
            if (_syncing)
            {
                // ルームの状態に合わせている最中。今送ると、合わせる前の ID のまま・合わせる前の古い状態の上に届いてしまう
                _held.Add((ops, !_adopted));
                return;
            }
        }
        if (!CanSend)
        {
            // 送らない変更も「処理済み」と Live に伝えないと、Live が相手の変更を無視し続けてしまう
            Skip(ops);
            return;
        }
        _outgoing.Writer.TryWrite(ops);
    }

    /// <summary>送れない変更。Live には処理済みと伝え、つなぎ直している最中なら、つながってから送れるように覚えておく。</summary>
    private void Skip(IReadOnlyList<Op> ops)
    {
        Bridge.Ack(ops.Select(o => o.Key));
        if (Reconnecting && PausedReason is null && !_waitingForHost)
            lock (_offlineKeys) _offlineKeys.UnionWith(ops.Select(o => o.Key));
    }

    private async Task OutgoingLoopAsync()
    {
        await foreach (var ops in _outgoing.Reader.ReadAllAsync(_cts.Token))
        {
            try
            {
                await SendOrDeferAsync(ops);
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                // 1 つの変更の失敗で、この後の変更が送られなくならないように
                Log(ActivityKind.Warning, "", $"変更を送れませんでした: {e.Message}");
                Skip(ops);
            }
        }
    }

    private async Task SendOrDeferAsync(IReadOnlyList<Op> ops)
    {
        var client = _client;
        if (client is null || !CanSend)
        {
            Skip(ops);
            return;
        }
        // バウンス・録音でまだ書き込み中のファイルがある変更（と、同じキーのその後の変更）は後回しにする
        var now = new List<Op>();
        var later = new List<Op>();
        lock (_deferredKeys)
            foreach (var op in ops)
            {
                if (_deferredKeys.ContainsKey(op.Key) || FileStore.LocalPaths(op.Value).Any(p => !FileStore.IsReady(p)))
                {
                    later.Add(op);
                    _deferredKeys[op.Key] = _deferredKeys.GetValueOrDefault(op.Key) + 1;
                }
                else now.Add(op);
            }
        if (later.Count > 0) _deferred.Writer.TryWrite(later);
        if (now.Count > 0) await SendLocalAsync(client, now);
    }

    private async Task DeferredLoopAsync()
    {
        await foreach (var ops in _deferred.Reader.ReadAllAsync(_cts.Token))
        {
            var paths = ops.SelectMany(o => FileStore.LocalPaths(o.Value)).Distinct().ToList();
            var names = string.Join("、", paths.Select(Path.GetFileName));
            if (paths.Any(p => !FileStore.IsReady(p)))
                Log(ActivityKind.System, "", $"「{names}」の書き込み（バウンス・録音）が終わるのを待っています…", "wait:" + names);
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            var lastSeen = DateTime.UtcNow;
            while (paths.Any(p => !FileStore.IsReady(p)) && DateTime.UtcNow < deadline)
            {
                if (paths.All(File.Exists)) lastSeen = DateTime.UtcNow;
                else if (DateTime.UtcNow - lastSeen > TimeSpan.FromMinutes(1)) break; // ファイルが現れない
                await Task.Delay(500, _cts.Token);
            }
            try
            {
                if (_client is { } client && CanSend) await SendLocalAsync(client, ops);
                else Skip(ops);
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                Log(ActivityKind.Warning, "", $"変更を送れませんでした: {e.Message}");
                Skip(ops);
            }
            lock (_deferredKeys)
                foreach (var op in ops)
                    if (_deferredKeys.GetValueOrDefault(op.Key) <= 1) _deferredKeys.Remove(op.Key);
                    else _deferredKeys[op.Key]--;
        }
    }

    private async Task SendLocalAsync(RoomClient client, IReadOnlyList<Op> ops)
    {
        List<Op> translated;
        try
        {
            translated = await ToRoomAsync(ops, upload: true);
        }
        catch (Exception e) when (!_cts.IsCancellationRequested)
        {
            Log(ActivityKind.Warning, "", $"ファイルを送れませんでした: {e.Message}");
            Bridge.Ack(ops.Select(o => o.Key));
            return;
        }
        Describe(ActivityKind.Self, MyName, translated);
        UpdateRoomState(translated);
        client.Send(translated.Select(o => o with { Value = ClipDelta.WithoutNotes(o.Value) }).ToList(), fromLive: true);
        WatchFiles(ops);
    }

    // ---- 送ったあとで書き換わったファイル ----
    //
    // ファイルは「1.5 秒書き込みが止まったら書き終わり」とみなして送る。重いプラグインのバウンスなどで
    // 書き込みが途中で止まると、書きかけのものを送ってしまうので、送ったあとも少しのあいだ見張り、
    // 中身が変わったら送り直す（受け取った側は、新しい中身を別のファイルとして受け取ってクリップを差し替える）。

    private sealed record FileWatch(JsonNode Value, Dictionary<string, (long Size, DateTime Time)> Files, DateTime Until);
    private readonly Dictionary<string, FileWatch> _fileWatches = [];
    private static readonly TimeSpan FileWatchTime = TimeSpan.FromMinutes(3);

    private void WatchFiles(IReadOnlyList<Op> ops)
    {
        lock (_fileWatches)
            foreach (var op in ops)
            {
                var paths = FileStore.LocalPaths(op.Value);
                if (op.Value is null || paths.Count == 0)
                {
                    _fileWatches.Remove(op.Key);  // 消した・ファイルでなくなった
                    continue;
                }
                var files = new Dictionary<string, (long, DateTime)>();
                foreach (var path in paths.Distinct())
                {
                    var info = new FileInfo(path);
                    if (info.Exists) files[path] = (info.Length, info.LastWriteTimeUtc);
                }
                _fileWatches[op.Key] = new FileWatch(op.Value.DeepClone(), files, DateTime.UtcNow + FileWatchTime);
            }
    }

    /// <summary>ほかの人が同じクリップを変えたら、こちらの古い値で送り直さない。</summary>
    private void ForgetFileWatches(IEnumerable<string> keys)
    {
        lock (_fileWatches)
            foreach (var key in keys) _fileWatches.Remove(key);
    }

    private async Task FileWatchLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            List<(string Key, FileWatch Watch)> changed = [];
            lock (_fileWatches)
                foreach (var (key, watch) in _fileWatches.ToList())
                {
                    if (DateTime.UtcNow > watch.Until)
                    {
                        _fileWatches.Remove(key);
                        continue;
                    }
                    var modified = watch.Files.Any(f =>
                    {
                        var info = new FileInfo(f.Key);
                        return info.Exists && (info.Length != f.Value.Size || info.LastWriteTimeUtc != f.Value.Time);
                    });
                    if (modified && watch.Files.Keys.All(FileStore.IsReady)) changed.Add((key, watch));
                }
            if (changed.Count == 0 || _client is not { } client || !CanSend) continue;
            try
            {
                // ファイルだけ差し替える（同じクリップのほかの項目は、その後の相手の変更を消さないように送らない）
                var ops = changed.Select(c => new Op(c.Key, c.Watch.Value is JsonObject o && o.ContainsKey("file")
                    ? WithFileDelta(o)
                    : StripDelta(c.Watch.Value))).ToList();
                var translated = await ToRoomAsync(ops, upload: true);
                // 送る前に、その間に新しい値で送り直された・相手が変えたものは外す（古いファイルで上書きしない）
                lock (_fileWatches)
                {
                    var current = changed.Where(c => _fileWatches.TryGetValue(c.Key, out var w) && ReferenceEquals(w, c.Watch))
                        .Select(c => c.Key).ToHashSet();
                    translated = translated.Where(o => current.Contains(o.Key)).ToList();
                    ops = ops.Where(o => current.Contains(o.Key)).ToList();
                }
                if (translated.Count == 0) continue;
                UpdateRoomState(translated);
                client.Send(translated, fromLive: false);
                WatchFiles(ops);
                var names = string.Join("、", ops.SelectMany(c => FileStore.LocalPaths(c.Value)).Distinct().Select(Path.GetFileName));
                Log(ActivityKind.System, "", $"「{names}」があとから書き換わったので、送り直しました");
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                Log(ActivityKind.Warning, "", $"書き換わったファイルを送り直せませんでした: {e.Message}");
            }
        }
    }

    private static JsonNode WithFileDelta(JsonObject value)
    {
        var copy = (JsonObject)value.DeepClone();
        copy["delta"] = new JsonObject { ["f"] = new JsonArray("file") };
        return copy;
    }

    private static JsonNode StripDelta(JsonNode value)
    {
        var copy = value.DeepClone();
        if (copy is JsonObject o) o.Remove("delta");
        return copy;
    }

    // ---- 受け取る ----

    private void OnRemoteOps(string who, IReadOnlyList<Op> ops)
    {
        Trace("remote:" + who, ops);
        // 相手がクリップを消した・ファイルを差し替えたら、こちらの書き換わったファイルで上書きしない
        ForgetFileWatches(ops.Where(o => o.Value is not JsonObject v || v["delta"] is not JsonObject d
                                         || (d["f"] as JsonArray ?? []).Any(f => (string?)f == "file")).Select(o => o.Key));
        // 名前の変更などは変更前の名前で説明したいので、状態を更新する前に文章にする
        Describe(ActivityKind.Remote, who, ops);
        UpdateRoomState(ops);
        if (_waitingForHost)
        {
            // ホストの状態が届いた。同じセットか確かめてから合わせる
            _ = SyncWithLiveAsync();
            return;
        }
        _incoming.Writer.TryWrite(new RemoteBatch(ops.ToList()));
    }

    private async Task IncomingLoopAsync()
    {
        await foreach (var item in _incoming.Reader.ReadAllAsync(_cts.Token))
        {
            switch (item)
            {
                case AckBatch ack:
                    Bridge.Ack(ack.Keys);
                    break;
                case ResetBatch:
                    Bridge.ResetPending();
                    break;
                case RemoteBatch batch:
                    // ルームの状態に合わせている最中に届いた変更は、合わせ終わってから反映する。
                    // 先に反映すると、合わせ始めたときの（古い）状態で上書きされてしまう（遅い回線で途中参加したとき）
                    await _syncLock.WaitAsync(_cts.Token);
                    try
                    {
                        if (PausedReason is null && !_waitingForHost && _liveSynced && Bridge.IsConnected)
                            Bridge.Apply(await ToLocalAsync(batch.Ops));
                    }
                    catch (Exception e) when (!_cts.IsCancellationRequested)
                    {
                        // 1 つの失敗で、この後の変更が反映されなくならないように
                        Log(ActivityKind.Warning, "", $"相手の変更を反映できませんでした: {e.Message}");
                    }
                    finally
                    {
                        _syncLock.Release();
                    }
                    break;
            }
        }
    }

    private void UpdateRoomState(IReadOnlyList<Op> ops)
    {
        lock (_gate)
            foreach (var op in ops)
            {
                if (ClipDelta.Apply(_roomState.GetValueOrDefault(op.Key), op.Value) is { } merged) _roomState[op.Key] = merged;
                else _roomState.Remove(op.Key);
            }
    }

    // ---- ファイルの変換 ----

    private async Task<List<Op>> ToRoomAsync(IReadOnlyList<Op> ops, bool upload)
    {
        var blobs = _blobs;
        Func<string, string, Task>? uploader = upload && blobs is not null ? blobs.EnsureUploadedAsync : null;
        var result = new List<Op>(ops.Count);
        foreach (var op in ops)
            result.Add(op.Value is null ? op : op with { Value = await Files.ToRoomAsync(op.Value, uploader, _cts.Token) });
        return result;
    }

    private async Task<List<Op>> ToLocalAsync(IReadOnlyList<Op> ops)
    {
        var blobs = _blobs;
        Func<string, string, Task<bool>>? downloader = blobs is null ? null : async (sha, target) =>
        {
            var name = Path.GetFileName(target);
            Log(ActivityKind.System, "", $"サンプル「{name}」を受け取っています…", "file:" + sha);
            var ok = await blobs.DownloadAsync(sha, target);
            Log(ok ? ActivityKind.System : ActivityKind.Warning, "",
                ok ? $"サンプル「{name}」を受け取りました" : $"サンプル「{name}」を受け取れませんでした", "file:" + sha);
            return ok;
        };
        var result = new List<Op>(ops.Count);
        foreach (var op in ops)
            result.Add(op.Value is null ? op : op with { Value = await Files.ToLocalAsync(op.Value, downloader, _cts.Token) });
        return result;
    }

    // ---- 表示・記録 ----

    private void Pause(string reason, bool canOverwrite = false)
    {
        PausedReason = reason;
        CanOverwrite = canOverwrite;
        Log(ActivityKind.Warning, "", "同期を一時停止しました: " + reason);
        Changed?.Invoke();
    }

    private void Describe(ActivityKind kind, string who, IReadOnlyList<Op> ops)
    {
        if (ops.Count > 8)
        {
            Log(kind, who, $"{ops.Count} か所を変更");
            return;
        }
        List<(string Text, string Key)> lines;
        lock (_gate) lines = OpDescriber.DescribeAll(ops, _roomState).ToList();
        foreach (var (text, key) in lines) Log(kind, who, text, key);
    }

    // 調査用: 環境変数 ABLETONMULTI_TRACE にファイルのパスを入れると、やりとりした変更をそのまま書き出す
    private static readonly string? TracePath = Environment.GetEnvironmentVariable("ABLETONMULTI_TRACE");
    private static readonly Lock TraceGate = new();

    private static void Trace(string what, IReadOnlyList<Op> ops)
    {
        if (TracePath is null) return;
        try
        {
            var line = new JsonObject { ["t"] = DateTime.Now.ToString("HH:mm:ss.fff"), ["what"] = what, ["ops"] = Op.ToArray(ops) };
            lock (TraceGate) File.AppendAllText(TracePath, line.ToJsonString() + Environment.NewLine);
        }
        catch (Exception) { }  // 調べるための書き出しの失敗で、受信などを止めない
    }

    private void Log(ActivityKind kind, string who, string text, string? key = null) =>
        Activity?.Invoke(new ActivityEntry(DateTime.Now, kind, who, text, key));

    private void SaveApiReport(JsonObject report)
    {
        try
        {
            var dir = DataDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{Bridge.Daw}-api.json"), report.ToJsonString(new() { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_client is not null) await _client.DisposeAsync();
        if (_blobs is not null) await _blobs.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        if (_tunnel is not null) await _tunnel.DisposeAsync();
        if (_reaper is not null) await _reaper.DisposeAsync();
        await Bridge.DisposeAsync();
    }
}
