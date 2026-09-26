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

    public LiveBridge Bridge { get; } = new();
    public string DataDirectory { get; }
    public FileStore Files { get; }
    public bool IsHost => _server is not null;
    public bool InRoom => _client is not null;
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
            _dawName = name;
            Log(ActivityKind.System, "", $"{name} と接続しました");
            Changed?.Invoke();
            _ = SyncWithLiveAsync();
        };
        Bridge.Disconnected += () =>
        {
            Log(ActivityKind.System, "", $"{_dawName} との接続が切れました");
            Changed?.Invoke();
        };
        Bridge.Warning += message => Log(ActivityKind.Warning, "", message);
        Bridge.ApiReported += SaveApiReport;
        Bridge.LocalOps += OnLocalOps;
        _ = Task.Run(OutgoingLoopAsync);
        _ = Task.Run(DeferredLoopAsync);
        _ = Task.Run(IncomingLoopAsync);
    }

    public bool ScriptOutdated => Bridge.IsConnected && Bridge.ScriptProtocolVersion != LiveBridge.ScriptProtocol;

    public void StartBridge(int port = LiveBridge.DefaultPort) => Bridge.Start(port);

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
        var client = new RoomClient();
        client.RemoteOps += OnRemoteOps;
        client.Echoed += ops => _incoming.Writer.TryWrite(new AckBatch(ops.Select(o => o.Key).ToList()));
        client.PeersChanged += _ => Changed?.Invoke();
        client.Disconnected += reason => _ = OnRoomLostAsync(client, reason);

        var blobs = new BlobClient();
        try
        {
            var state = await client.ConnectAsync(address, room, name, key, TimeSpan.FromSeconds(15));
            await blobs.ConnectAsync(address, room, key, TimeSpan.FromSeconds(15));
            lock (_gate)
            {
                _roomState.Clear();
                foreach (var op in state) _roomState[op.Key] = op.Value;
            }
        }
        catch
        {
            await client.DisposeAsync();
            await blobs.DisposeAsync();
            throw;
        }
        _blobs = blobs;
        _client = client;
        MyName = name;
        RoomAddress = address;
        PausedReason = null;
        _waitingForHost = false;
        Log(ActivityKind.System, "", IsHost ? "ルームを作りました" : "ルームに参加しました");
        Changed?.Invoke();
        await SyncWithLiveAsync();
    }

    /// <summary>ホストしているルームをインターネットに公開する（初回は cloudflared をダウンロードする）。</summary>
    public async Task<string> PublishAsync(IProgress<(long Done, long? Total)>? downloadProgress = null)
    {
        if (_server is not { } server) throw new InvalidOperationException("ルームを作ってから公開してください");
        if (!CloudflareTunnel.IsInstalled)
        {
            Log(ActivityKind.System, "", "インターネット公開用のツール（Cloudflare 公式 cloudflared）をダウンロードしています…");
            await CloudflareTunnel.EnsureInstalledAsync(downloadProgress, _cts.Token);
        }
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
        var address = await tunnel.StartAsync(server.Port, TimeSpan.FromSeconds(60), _cts.Token);
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

    public async Task LeaveAsync()
    {
        await UnpublishAsync();
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null) await client.DisposeAsync();
        var blobs = Interlocked.Exchange(ref _blobs, null);
        if (blobs is not null) await blobs.DisposeAsync();
        Bridge.ResetPending();
        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null) await server.DisposeAsync();
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
        await client.DisposeAsync();
        var blobs = Interlocked.Exchange(ref _blobs, null);
        if (blobs is not null) await blobs.DisposeAsync();
        Bridge.ResetPending();
        RoomAddress = null;
        Log(ActivityKind.Warning, "", reason);
        Changed?.Invoke();
    }

    // ---- ルームの状態に合わせる ----

    /// <summary>自分の Live をルームの状態に合わせ直す（ルームが空なら自分の状態をルームに入れる）。</summary>
    /// <param name="overwrite">
    /// トラック数などが違っていても、このセットをルームの内容で上書きする（ユーザーが確認したとき）。
    /// 空のセットから参加して、ルームの内容を丸ごと受け取ることもできる。
    /// </param>
    public async Task SyncWithLiveAsync(bool overwrite = false)
    {
        if (_client is not { } client || !Bridge.IsConnected) return;
        if (ScriptOutdated)
        {
            Pause("Live のスクリプトが古いバージョンです。アプリの「スクリプトを更新する」を押してから Live を再起動してください。");
            return;
        }
        await _syncLock.WaitAsync();
        try
        {
            if (_blankPending)
            {
                await Bridge.MakeBlankAsync(TimeSpan.FromSeconds(30));
                _blankPending = false;
                if (!IsHost) _overwriteNext = true;
                Log(ActivityKind.System, "", Bridge.Daw == "bitwig"
                    ? "Bitwig のプロジェクトをまっさら（楽器トラック 1 本）にしました"
                    : "Live のセットをまっさら（MIDI トラック 1 本）にしました");
            }
            if (_overwriteNext) overwrite = true;

            Dictionary<string, JsonNode?> room;
            lock (_gate) room = new Dictionary<string, JsonNode?>(_roomState);

            if (room.Count == 0 && !IsHost)
            {
                // ルームの最初の状態はホストのセットから作る。ゲストはホストの Live がつながるのを待つ
                _waitingForHost = true;
                Log(ActivityKind.System, "", "ホストの DAW（Live か Bitwig）が接続されるのを待っています");
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
                Log(ActivityKind.System, "", "このセットの状態をルームに共有しました");
                Changed?.Invoke();
                return;
            }

            var liveRoom = ToDictionary(await ToRoomAsync(live, upload: false));
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
                live = await Bridge.RequestSnapshotAsync(TimeSpan.FromSeconds(30));
                liveRoom = ToDictionary(await ToRoomAsync(live, upload: false));
            }

            var diff = liveRoom.Keys
                .Where(k => !room.ContainsKey(k) && IsStructural(k))
                .Select(k => new Op(k, null))
                .Concat(room
                    .Where(kv => !liveRoom.TryGetValue(kv.Key, out var current) || !JsonNode.DeepEquals(current, kv.Value))
                    .Select(kv => new Op(kv.Key, kv.Value)))
                .ToList();
            Bridge.Apply(await ToLocalAsync(diff), force: true);
            _waitingForHost = false;
            _overwriteNext = false;
            PausedReason = null;
            CanOverwrite = false;
            Log(ActivityKind.System, "", diff.Count == 0 ? "ルームと同じ状態です" : $"ルームの状態に合わせました（{diff.Count} か所）");
            Changed?.Invoke();
        }
        catch (Exception e) when (e is TimeoutException or InvalidOperationException or OperationCanceledException or IOException)
        {
            Log(ActivityKind.Warning, "", $"ルームの状態に合わせられませんでした: {e.Message}");
        }
        finally
        {
            _syncLock.Release();
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
        if (!CanSend)
        {
            // 送らない変更も「処理済み」と Live に伝えないと、Live が相手の変更を無視し続けてしまう
            Bridge.Ack(ops.Select(o => o.Key));
            return;
        }
        _outgoing.Writer.TryWrite(ops);
    }

    private async Task OutgoingLoopAsync()
    {
        await foreach (var ops in _outgoing.Reader.ReadAllAsync(_cts.Token))
        {
            var client = _client;
            if (client is null || !CanSend)
            {
                Bridge.Ack(ops.Select(o => o.Key));
                continue;
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
            if (_client is { } client && CanSend) await SendLocalAsync(client, ops);
            else Bridge.Ack(ops.Select(o => o.Key));
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
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Log(ActivityKind.Warning, "", $"ファイルを送れませんでした: {e.Message}");
            Bridge.Ack(ops.Select(o => o.Key));
            return;
        }
        Describe(ActivityKind.Self, MyName, translated);
        UpdateRoomState(translated);
        client.Send(translated, fromLive: true);
    }

    // ---- 受け取る ----

    private void OnRemoteOps(string who, IReadOnlyList<Op> ops)
    {
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
                case RemoteBatch batch when PausedReason is null && !_waitingForHost && Bridge.IsConnected:
                    try
                    {
                        Bridge.Apply(await ToLocalAsync(batch.Ops));
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        Log(ActivityKind.Warning, "", $"ファイルを受け取れませんでした: {e.Message}");
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
                if (op.Value is null) _roomState.Remove(op.Key);
                else _roomState[op.Key] = ClipDelta.Apply(_roomState.GetValueOrDefault(op.Key), op.Value)!;
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
        foreach (var op in ops)
        {
            string text;
            lock (_gate) text = OpDescriber.Describe(op, _roomState);
            Log(kind, who, text, op.Key);
        }
    }

    private void Log(ActivityKind kind, string who, string text, string? key = null) =>
        Activity?.Invoke(new ActivityEntry(DateTime.Now, kind, who, text, key));

    private void SaveApiReport(JsonObject report)
    {
        try
        {
            var dir = DataDirectory;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, Bridge.Daw == "bitwig" ? "bitwig-api.json" : "live-api.json"), report.ToJsonString(new() { WriteIndented = true }));
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
        await Bridge.DisposeAsync();
    }
}
