using System.Net.Sockets;
using System.Text;

namespace Maltese.Core.Sync;

/// <summary>
/// REAPER のスクリプト（reaper/maltese.lua）とアプリの LiveBridge をつなぐ中継。
/// REAPER の Lua には通信の機能が無いので、スクリプトとはフォルダの中のファイルでやりとりし、
/// こちらで LiveBridge（127.0.0.1:ポート）に TCP でつなぎ直す。LiveBridge からは Live と同じに見える。
///   &lt;AppData&gt;/Maltese/reaper-ipc/&lt;ポート&gt;/in/   こちら → REAPER（1 行 1 JSON のファイル。番号順に読まれる）
///   ipc/&lt;ポート&gt;/out/  REAPER → こちら
///   reaper_alive   REAPER が書く時刻（止まったら REAPER が終了した）
///   app_alive      こちらが書く「時刻 接続ごとの番号」（番号が変わったら、REAPER はつなぎ直しとみなす）
/// </summary>
public sealed class ReaperRelay : IAsyncDisposable
{
    /// <summary>
    /// 相手の印がこれだけ古くなったら止まったとみなす。大きなプロジェクトでは REAPER の画面の処理（スクリプトも
    /// そこで動く）がしばらく止まることがあるので、余裕を持たせる（スクリプト側の ALIVE_TIMEOUT も同じ長さ）。
    /// </summary>
    private static readonly TimeSpan AliveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>つないでいる LiveBridge との接続。別のスレッドが切ることがあるので、1 つの参照でまとめて入れ替える。</summary>
    private sealed record Link(TcpClient Client, Stream Stream);

    private readonly int _port;
    private readonly string _dir;
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);
    private Task? _loop;
    private Link? _link;
    private string _session = "";
    private long _inSeq;
    private DateTime _lastAlive;
    private DateTime _lastSeenReaper = DateTime.MinValue;
    private DateTime _nextConnect = DateTime.MinValue;

    public ReaperRelay(int bridgePort, string? ipcRoot = null)
    {
        _port = bridgePort;
        _dir = Path.Combine(ipcRoot ?? IpcRoot, bridgePort.ToString());
    }

    /// <summary>REAPER のスクリプトとやりとりする場所（スクリプトの ipc_root() と同じ）。</summary>
    public static string IpcRoot
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maltese", "reaper-ipc");
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "Maltese", "reaper-ipc");
            return Path.Combine(home, ".config", "Maltese", "reaper-ipc");
        }
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
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // どんな失敗でもこのループは止めない（止まると、アプリを再起動するまで REAPER とつながらなくなる）
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
            if (_link is not null) Disconnect();
            return;
        }
        var alive = ReaperAlive();
        if (!alive)
        {
            if (_link is not null) Disconnect();
            return;
        }
        // 切れた直後はすぐつなぎ直さない（別の DAW がつながっていて断られたときなど）
        if (_link is null && DateTime.UtcNow >= _nextConnect) await ConnectAsync();
        // 読み取り側のスレッドがいつ切ってもよいように、この回はここで取った接続だけを使う
        var link = _link;
        if (link is null) return;

        if (DateTime.UtcNow - _lastAlive >= TimeSpan.FromSeconds(1))
        {
            // スクリプトが読んでいる・ウイルス対策ソフトが開いているなどで書けないことがある。
            // 切断はせず、書けなければ次の回（20 ミリ秒後）にもう一度書く
            if (TryWriteAtomic(Path.Combine(_dir, "app_alive"), $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {_session}"))
                _lastAlive = DateTime.UtcNow;
        }

        // REAPER → アプリ
        if (!Directory.Exists(OutDir)) return;
        var wrote = false;
        foreach (var file in Directory.GetFiles(OutDir, "*.json").Order(StringComparer.Ordinal))
        {
            // 送ったのに消せなかったもの。もう一度送らないよう、消すのだけやり直す
            if (_sent.Contains(file))
            {
                if (TryDelete(file)) _sent.Remove(file);
                continue;
            }
            string text;
            try { text = await File.ReadAllTextAsync(file, _cts.Token); }
            catch (IOException) { break; }  // まだ書き込み中なら次の回に
            catch (UnauthorizedAccessException) { break; }
            var bytes = Encoding.UTF8.GetBytes(text.EndsWith('\n') ? text : text + "\n");
            // 送れてから消す（送る途中で切れたら、ファイルは残る）
            await link.Stream.WriteAsync(bytes, _cts.Token);
            wrote = true;
            if (!TryDelete(file)) _sent.Add(file);
        }
        if (wrote) await link.Stream.FlushAsync(_cts.Token);
        // 無くなったもの（REAPER が消した）は忘れる
        if (_sent.Count > 0) _sent.RemoveWhere(f => !File.Exists(f));
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
        Stream stream;
        try
        {
            Directory.CreateDirectory(InDir);
            foreach (var file in Directory.GetFiles(InDir)) File.Delete(file);
            // 前の接続で送れなかった out/ の残りは、前の接続あてのもの（REAPER は新しい番号を見て hello から送り直す）
            _sent.Clear();
            if (Directory.Exists(OutDir))
                foreach (var file in Directory.GetFiles(OutDir, "*.json"))
                    if (!TryDelete(file)) _sent.Add(file);
            stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _session = Guid.NewGuid().ToString("N")[..8];
        _lastAlive = DateTime.MinValue;
        _link = new Link(client, stream);
        _ = Task.Run(() => ReadFromBridgeAsync(client, stream));
    }

    /// <summary>アプリ（LiveBridge）→ REAPER: 届いた行をまとめて in/ にファイルで置く。</summary>
    private async Task ReadFromBridgeAsync(TcpClient client, Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                var name = $"{Interlocked.Increment(ref _inSeq):D12}.json";
                var path = Path.Combine(InDir, name);
                // 置けないと、その変更が REAPER に届かない。少し待って何度か試し、だめなら切ってつなぎ直す
                // （つなぎ直すとアプリが状態を合わせ直す）
                var written = false;
                for (var attempt = 0; attempt < 5 && !written; attempt++)
                {
                    if (attempt > 0) await Task.Delay(50, _cts.Token);
                    written = TryWriteAtomic(path, line + "\n");
                }
                if (!written) break;
            }
        }
        catch (Exception)
        {
            // どんな失敗でも、下で切る（切らないと、読む人のいない接続が残る）
        }
        finally
        {
            Disconnect(client);
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static bool TryWriteAtomic(string path, string text)
    {
        try
        {
            WriteAtomic(path, text);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <param name="only">指定したときは、その接続がまだ今の接続のときだけ切る（新しくつなぎ直した接続は切らない）</param>
    private void Disconnect(TcpClient? only = null)
    {
        Link? link;
        if (only is null)
        {
            link = Interlocked.Exchange(ref _link, null);
        }
        else
        {
            link = _link;
            if (link is null || link.Client != only || Interlocked.CompareExchange(ref _link, null, link) != link)
            {
                only.Dispose();  // もう切られている（または入れ替わった）
                return;
            }
        }
        _nextConnect = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        link?.Client.Dispose();
        TryDelete(Path.Combine(_dir, "app_alive"));
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
