using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// 中継サーバー。参加者から届いた変更に通し番号順を付け、全員（送った本人も含む）に配る。
/// 全員が同じ順番で適用するので、同時に同じものを変えても最後は必ず同じ状態に揃う。
/// ホストの PC のアプリ内で動かすほか、将来はクラウドでもそのまま動かせる。
/// </summary>
public sealed class RoomServer : IAsyncDisposable
{
    public const int DefaultPort = 47401;
    public const int ProtocolVersion = 2;

    private sealed class Client(int id, string name, IMessageConnection connection)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public IMessageConnection Connection { get; } = connection;
    }

    private sealed class Room
    {
        // キーごとの最新値。新しく参加した人にはこれを丸ごと渡す
        public Dictionary<string, JsonNode?> State { get; } = [];
        public List<Client> Clients { get; } = [];
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Room> _rooms = [];
    private TcpListener? _listener;
    private int _nextClientId;
    // 参加者が送ってきたサンプルなどのファイル（ハッシュ名で保存）
    private readonly string _blobDirectory = Path.Combine(Path.GetTempPath(), "AbletonMulti-blobs", Guid.NewGuid().ToString("N"));

    public int Port { get; private set; }

    /// <summary>参加に必要な合言葉（null なら不要）。インターネット越しに知らない人が入れないようにする。</summary>
    public string? Key { get; init; }

    public static string GenerateKey()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 見間違えやすい 0/O、1/I は使わない
        return string.Concat(Enumerable.Range(0, 6).Select(_ => letters[RandomNumberGenerator.GetInt32(letters.Length)]));
    }

    public static string NormalizeKey(string? key) => (key ?? "").Trim().ToUpperInvariant();

    public void Start(int port = DefaultPort)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _ = AcceptLoopAsync(_listener);
    }

    /// <summary>同じネットワークの相手に伝えるアドレス候補（192.168.x.x など）</summary>
    public static IEnumerable<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Select(a => a.ToString())
            .Where(a => !a.StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(a => a.StartsWith("192.168.", StringComparison.Ordinal) ? 0 : 1);

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = AcceptAsync(tcp);
        }
    }

    private async Task AcceptAsync(TcpClient tcp)
    {
        IMessageConnection? connection;
        try { connection = await Transport.AcceptAsync(tcp, _cts.Token); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            tcp.Dispose();
            return;
        }
        if (connection is not null) await HandleAsync(connection);
    }

    private async Task HandleAsync(IMessageConnection connection)
    {
        Client? client = null;
        Room? room = null;

        var isFiles = false;
        FileStream? upload = null;
        string? uploadPath = null;

        await foreach (var msg in connection.ReadAllAsync(_cts.Token))
        {
            var kind = (string?)msg["t"];
            if (isFiles)
            {
                (upload, uploadPath) = await HandleFileMessageAsync(connection, msg, upload, uploadPath);
                continue;
            }
            if (client is null)
            {
                if (kind != "hello") break;
                if (Key is not null && NormalizeKey((string?)msg["key"]) != Key)
                {
                    connection.Send(new JsonObject { ["t"] = "error", ["msg"] = "合言葉が違います。" });
                    break;
                }
                if ((string?)msg["role"] == "files" && (int?)msg["proto"] == ProtocolVersion)
                {
                    isFiles = true;
                    Directory.CreateDirectory(_blobDirectory);
                    continue;
                }
                if ((int?)msg["proto"] != ProtocolVersion)
                {
                    connection.Send(new JsonObject { ["t"] = "error", ["msg"] = "アプリのバージョンが違います。全員同じバージョンを使ってください。" });
                    break;
                }
                var name = ((string?)msg["name"])?.Trim() is { Length: > 0 } n ? n : "ゲスト";
                var roomName = (string?)msg["room"] ?? "default";
                lock (_gate)
                {
                    room = _rooms.TryGetValue(roomName, out var r) ? r : _rooms[roomName] = new Room();
                    client = new Client(++_nextClientId, name, connection);
                    room.Clients.Add(client);
                    connection.Send(new JsonObject
                    {
                        ["t"] = "welcome",
                        ["id"] = client.Id,
                        ["state"] = Op.ToArray(room.State.Select(kv => new Op(kv.Key, kv.Value))),
                    });
                    BroadcastPeers(room);
                }
                continue;
            }

            if (kind == "ops")
            {
                var ops = Op.FromArray(msg["ops"]);
                lock (_gate)
                {
                    foreach (var op in ops)
                    {
                        if (op.Value is null) room!.State.Remove(op.Key);
                        else room!.State[op.Key] = ClipDelta.Apply(room!.State.GetValueOrDefault(op.Key), op.Value)!.DeepClone();
                    }
                    // ロックの中で全員の送信キューに積むので、全員が同じ順番で受け取る
                    var message = new JsonObject
                    {
                        ["t"] = "ops",
                        ["from"] = client.Id,
                        ["name"] = client.Name,
                        ["ops"] = Op.ToArray(ops),
                    };
                    foreach (var c in room!.Clients) c.Connection.Send((JsonObject)message.DeepClone());
                }
            }
        }

        if (upload is not null)
        {
            await upload.DisposeAsync();
            File.Delete(uploadPath!);
        }
        if (client is not null && room is not null)
        {
            lock (_gate)
            {
                room.Clients.Remove(client);
                BroadcastPeers(room);
            }
        }
        await connection.DisposeAsync();
    }

    private async Task<(FileStream?, string?)> HandleFileMessageAsync(IMessageConnection connection, JsonObject msg, FileStream? upload, string? uploadPath)
    {
        var sha = (string?)msg["sha"] ?? "";
        if (sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower))
        {
            connection.Send(new JsonObject { ["t"] = "error", ["msg"] = "bad hash" });
            return (upload, uploadPath);
        }
        var path = Path.Combine(_blobDirectory, sha);

        switch ((string?)msg["t"])
        {
            case "has":
                connection.Send(new JsonObject { ["t"] = "has", ["sha"] = sha, ["yes"] = File.Exists(path) });
                break;

            case "put":
                if ((long?)msg["offset"] == 0 || upload is null)
                {
                    if (upload is not null) await upload.DisposeAsync();
                    uploadPath = path + "." + Guid.NewGuid().ToString("N") + ".part";
                    upload = File.Create(uploadPath);
                }
                await upload.WriteAsync(Convert.FromBase64String((string?)msg["data"] ?? ""));
                if ((bool?)msg["final"] == true)
                {
                    upload.Position = 0;
                    var ok = Convert.ToHexStringLower(await SHA256.HashDataAsync(upload)) == sha;
                    await upload.DisposeAsync();
                    upload = null;
                    if (ok) File.Move(uploadPath!, path, overwrite: true);
                    else File.Delete(uploadPath!);
                    connection.Send(new JsonObject { ["t"] = "put_done", ["sha"] = sha, ["ok"] = ok });
                }
                break;

            case "get":
                if (!File.Exists(path))
                {
                    connection.Send(new JsonObject { ["t"] = "missing", ["sha"] = sha });
                    break;
                }
                await using (var stream = File.OpenRead(path))
                {
                    var buffer = new byte[BlobClient.ChunkSize];
                    long offset = 0;
                    var total = stream.Length;
                    while (true)
                    {
                        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                        var final = offset + read >= total;
                        connection.Send(new JsonObject
                        {
                            ["t"] = "chunk", ["sha"] = sha, ["offset"] = offset, ["size"] = total,
                            ["data"] = Convert.ToBase64String(buffer, 0, read), ["final"] = final,
                        });
                        offset += read;
                        if (final) break;
                    }
                }
                break;
        }
        return (upload, uploadPath);
    }

    private static void BroadcastPeers(Room room)
    {
        var peers = new JsonArray(room.Clients.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name }).ToArray());
        foreach (var c in room.Clients)
            c.Connection.Send(new JsonObject { ["t"] = "peers", ["peers"] = peers.DeepClone() });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener?.Stop();
        List<Client> all;
        lock (_gate) all = _rooms.Values.SelectMany(r => r.Clients).ToList();
        foreach (var c in all) await c.Connection.DisposeAsync();
        try
        {
            if (Directory.Exists(_blobDirectory)) Directory.Delete(_blobDirectory, recursive: true);
        }
        catch (IOException) { }
    }
}
