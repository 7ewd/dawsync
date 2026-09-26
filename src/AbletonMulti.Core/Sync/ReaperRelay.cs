using System.Net.Sockets;
using System.Text;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// REAPER のスクリプト（reaper/AbletonMulti/abletonmulti.lua）とアプリの LiveBridge をつなぐ中継。
/// REAPER の Lua には通信の機能が無いので、スクリプトとはフォルダの中のファイルでやりとりし、
/// こちらで LiveBridge（127.0.0.1:ポート）に TCP でつなぎ直す。LiveBridge からは Live と同じに見える。
///   ipc/&lt;ポート&gt;/in/   こちら → REAPER（1 行 1 JSON のファイル。番号順に読まれる）
///   ipc/&lt;ポート&gt;/out/  REAPER → こちら
///   reaper_alive   REAPER が書く時刻（止まったら REAPER が終了した）
///   app_alive      こちらが書く「時刻 接続ごとの番号」（番号が変わったら、REAPER はつなぎ直しとみなす）
/// </summary>
public sealed class ReaperRelay : IAsyncDisposable
{
    private static readonly TimeSpan AliveTimeout = TimeSpan.FromSeconds(3);

    private readonly int _port;
    private readonly string _dir;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private TcpClient? _client;
    private Stream? _stream;
    private string _session = "";
    private long _inSeq;
    private DateTime _lastAlive;
    private DateTime _lastSeenReaper = DateTime.MinValue;

    public ReaperRelay(int bridgePort, string? resourceDirectory = null)
    {
        _port = bridgePort;
        _dir = Path.Combine(resourceDirectory ?? ReaperScriptInstaller.ResourceDirectory, "AbletonMulti", "ipc", bridgePort.ToString());
    }

    public void Start() => _loop ??= Task.Run(LoopAsync);

    private string InDir => Path.Combine(_dir, "in");
    private string OutDir => Path.Combine(_dir, "out");

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await StepAsync();
            }
            catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Disconnect();
            }
            try { await Task.Delay(20, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        Disconnect();
    }

    /// <summary>
    /// REAPER が動いているか。スクリプトは印のファイルを「消してから置き直す」ので、一瞬読めないことがある。
    /// そのたびに切断しないよう、最後に新しい印を読めてから AliveTimeout のあいだは動いているとみなす。
    /// </summary>
    private bool ReaperAlive()
    {
        var path = Path.Combine(_dir, "reaper_alive");
        try
        {
            if (File.Exists(path)
                && long.TryParse(File.ReadAllText(path).Trim(), out var t)
                && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(t) <= AliveTimeout)
                _lastSeenReaper = DateTime.UtcNow;
        }
        catch (IOException) { }  // 書き込み中
        catch (UnauthorizedAccessException) { }
        return DateTime.UtcNow - _lastSeenReaper <= AliveTimeout;
    }

    private async Task StepAsync()
    {
        if (!Directory.Exists(_dir))
        {
            if (_client is not null) Disconnect();
            return;
        }
        var alive = ReaperAlive();
        if (!alive)
        {
            if (_client is not null) Disconnect();
            return;
        }
        if (_client is null) await ConnectAsync();
        if (_client is null) return;

        if (DateTime.UtcNow - _lastAlive >= TimeSpan.FromSeconds(1))
        {
            _lastAlive = DateTime.UtcNow;
            WriteAtomic(Path.Combine(_dir, "app_alive"), $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {_session}");
        }

        // REAPER → アプリ
        if (Directory.Exists(OutDir))
        {
            foreach (var file in Directory.GetFiles(OutDir, "*.json").Order(StringComparer.Ordinal))
            {
                string text;
                try { text = await File.ReadAllTextAsync(file, _cts.Token); }
                catch (IOException) { break; }  // まだ書き込み中なら次の回に
                File.Delete(file);
                var bytes = Encoding.UTF8.GetBytes(text.EndsWith('\n') ? text : text + "\n");
                await _stream!.WriteAsync(bytes, _cts.Token);
            }
            await _stream!.FlushAsync(_cts.Token);
        }
    }

    private async Task ConnectAsync()
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync("127.0.0.1", _port, _cts.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            client.Dispose();
            return;  // アプリの LiveBridge がまだ（またはポートを使えなかった）
        }
        Directory.CreateDirectory(InDir);
        foreach (var file in Directory.GetFiles(InDir)) File.Delete(file);
        _client = client;
        _stream = client.GetStream();
        _session = Guid.NewGuid().ToString("N")[..8];
        _lastAlive = DateTime.MinValue;
        _ = Task.Run(() => ReadFromBridgeAsync(client));
    }

    /// <summary>アプリ（LiveBridge）→ REAPER: 届いた行をまとめて in/ にファイルで置く。</summary>
    private async Task ReadFromBridgeAsync(TcpClient client)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                var name = $"{Interlocked.Increment(ref _inSeq):D12}.json";
                WriteAtomic(Path.Combine(InDir, name), line + "\n");
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
        if (_client == client) Disconnect();
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private void Disconnect()
    {
        var client = Interlocked.Exchange(ref _client, null);
        _stream = null;
        client?.Dispose();
        try { File.Delete(Path.Combine(_dir, "app_alive")); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
