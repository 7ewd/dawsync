using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace DawSync.Core.Sync;

/// <summary>
/// FL Studio のスクリプト（flstudio/device_DawSync.py）とアプリの LiveBridge をつなぐ中継。
/// FL Studio 2025 以降のスクリプトは通信もスレッドも使えず、ファイルもスクリプトのフォルダの中しか読み書きできない
/// （フォルダを作る・ファイルを消す・置き換えることもできない）。そのため、スクリプトのフォルダの中の決まった
/// 2 つのファイルを書き直し合ってやりとりし、こちらで LiveBridge（127.0.0.1:ポート）に TCP でつなぎ直す。
/// LiveBridge からは Live と同じに見える。
///   &lt;FL Studio のユーザーデータ&gt;/Settings/Hardware/DawSync/ipc/&lt;ポート&gt;/to_fl.txt    こちら → FL Studio（こちらだけが書く）
///                                                                    from_fl.txt  FL Studio → こちら（スクリプトだけが書く）
/// どちらも「見出しの行」「番号 JSON」の行…「end」で、相手がまだ受け取っていない（ack の来ていない）メッセージを
/// 毎回全部書く。読む側は前に読んだ番号より後のものだけを扱う。
///   to_fl.txt   の見出し: dawsync-app 1 &lt;接続ごとの番号&gt; &lt;時刻&gt; &lt;読んだ FL の番号の持ち主&gt; &lt;その番号&gt;
///   from_fl.txt の見出し: dawsync-fl 1 &lt;スクリプトの番号&gt; &lt;時刻&gt; &lt;こちらの接続ごとの番号&gt; &lt;読んだ番号&gt;
/// </summary>
public sealed class FlStudioRelay : IAsyncDisposable
{
    /// <summary>
    /// 相手の印がこれだけ古くなったら止まったとみなす。FL Studio はダイアログを開いている間などに
    /// スクリプトを呼ばないことがあるので、余裕を持たせる（スクリプト側の ALIVE_TIMEOUT も同じ長さ）。
    /// </summary>
    private static readonly TimeSpan AliveTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(1);

    /// <summary>つないでいる LiveBridge との接続。別のスレッドが切ることがあるので、1 つの参照でまとめて入れ替える。</summary>
    private sealed record Link(TcpClient Client, Stream Stream, string Session);

    /// <summary>from_fl.txt を読んだもの。</summary>
    private sealed record FlFile(string Instance, long Time, string Session, long Ack, List<(long Seq, string Body)> Messages);

    private readonly int _port;
    private readonly string _dir;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly List<(long Seq, string Line)> _outbox = [];
    private long _outSeq;
    private bool _outboxChanged;
    private Task? _loop;
    private Link? _link;
    private string? _flInstance;
    private long _flSeq;
    private long _writtenFlSeq = -1;
    private DateTime _lastSeenFl = DateTime.MinValue;
    private DateTime _lastWrite = DateTime.MinValue;
    private DateTime _nextConnect = DateTime.MinValue;
    private DateTime _ackProgress = DateTime.UtcNow;

    public FlStudioRelay(int bridgePort, string? ipcRoot = null)
    {
        _port = bridgePort;
        _dir = Path.Combine(ipcRoot ?? IpcRoot, bridgePort.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// FL Studio のスクリプトとやりとりする場所（スクリプトの _ipc_root() と同じ）。スクリプトはこのフォルダの外を
    /// 読み書きできないので、スクリプトを入れたフォルダの中に置く。テストでは環境変数 DAWSYNC_FL_IPC で変えられる。
    /// </summary>
    public static string IpcRoot =>
        Environment.GetEnvironmentVariable("DAWSYNC_FL_IPC") is { Length: > 0 } test
            ? test
            : Path.Combine(FlStudioScriptInstaller.ScriptDirectory, "ipc");

    private string ToFlPath => Path.Combine(_dir, "to_fl.txt");
    private string FromFlPath => Path.Combine(_dir, "from_fl.txt");

    public void Start()
    {
        if (_loop is not null) return;
        // FL Studio のスクリプトはフォルダを作れないので、FL Studio のある PC ではこちらで作っておく
        // （スクリプトがつなぐのはいつものポートだけ。テスト用のポートのフォルダを FL Studio のフォルダに増やさない）
        try
        {
            if (_port == LiveBridge.DefaultPort && FlStudioScriptInstaller.IsFlStudioPresent()) Directory.CreateDirectory(_dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _loop = Task.Run(LoopAsync);
    }

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
                // どんな失敗でもこのループは止めない（止まると、アプリを再起動するまで FL Studio とつながらなくなる）
                Disconnect();
            }
            try { await Task.Delay(20, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
        Disconnect();
    }

    private async Task StepAsync()
    {
        if (!Directory.Exists(_dir))
        {
            if (_link is not null) Disconnect();
            return;
        }
        var file = ReadFromFl();
        if (file is { Time: 0 })
            _lastSeenFl = DateTime.MinValue;  // スクリプトが止まった（FL Studio の終了・スクリプトの切り替え）
        else if (file is not null && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - file.Time) <= AliveTimeout.TotalSeconds)
            _lastSeenFl = DateTime.UtcNow;
        if (DateTime.UtcNow - _lastSeenFl > AliveTimeout)
        {
            if (_link is not null) Disconnect();
            return;
        }
        // 切れた直後はすぐつなぎ直さない（別の DAW がつながっていて断られたときなど）
        if (_link is null && DateTime.UtcNow >= _nextConnect) await ConnectAsync();
        // 読み取り側のスレッドがいつ切ってもよいように、この回はここで取った接続だけを使う
        var link = _link;
        if (link is null) return;

        if (file is not null && file.Session == link.Session)
        {
            if (_flInstance is not null && file.Instance != _flInstance)
            {
                // FL Studio のスクリプトが読み込み直された（OnDeInit の印を書けずに止まった）。前のスクリプトあての
                // メッセージが残っているので、つなぎ直して新しい番号から始める（DAW 側からは切れてつながったように見える）
                Disconnect();
                return;
            }
            _flInstance = file.Instance;
            lock (_gate)
            {
                if (_outbox.Count > 0 && _outbox[0].Seq <= file.Ack)
                {
                    _outbox.RemoveAll(m => m.Seq <= file.Ack);
                    _outboxChanged = true;
                    _ackProgress = DateTime.UtcNow;
                }
            }
            // FL Studio → アプリ
            var bytes = new StringBuilder();
            var last = _flSeq;
            foreach (var (seq, body) in file.Messages)
            {
                if (seq <= last) continue;
                bytes.Append(body).Append('\n');
                last = seq;
            }
            if (bytes.Length > 0)
            {
                await link.Stream.WriteAsync(Encoding.UTF8.GetBytes(bytes.ToString()), _cts.Token);
                await link.Stream.FlushAsync(_cts.Token);
                _flSeq = last;
            }
        }

        bool stalled;
        lock (_gate)
        {
            if (_outbox.Count == 0) _ackProgress = DateTime.UtcNow;
            stalled = DateTime.UtcNow - _ackProgress > AliveTimeout;
        }
        if (stalled)
        {
            // FL Studio は動いているのに受け取らない（スクリプトの不具合など）。つなぎ直すとアプリが状態を合わせ直す
            Disconnect();
            return;
        }
        WriteToFl(link.Session);
    }

    /// <summary>from_fl.txt を読む。書いている途中（end が無い）・形の違うものは null（次の回に読み直す）。</summary>
    private FlFile? ReadFromFl()
    {
        string text;
        try
        {
            // FL Studio が書いている途中でも開けるように、書き込み・削除も許して開く
            using var stream = new FileStream(FromFlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return ParseFromFl(text);
    }

    private static FlFile? ParseFromFl(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count < 2 || lines[^1] != "end") return null;
        var header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (header.Length < 6 || header[0] != "dawsync-fl"
            || !long.TryParse(header[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var time)
            || !long.TryParse(header[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ack))
            return null;
        var messages = new List<(long, string)>();
        foreach (var line in lines.Skip(1).Take(lines.Count - 2))
        {
            var space = line.IndexOf(' ');
            if (space > 0 && long.TryParse(line.AsSpan(0, space), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                messages.Add((seq, line[(space + 1)..]));
        }
        return new FlFile(header[2], time, header[4], ack, messages);
    }

    /// <summary>to_fl.txt を書き直す（変わったとき、または 1 秒ごと。FL Studio はこれでこちらが動いているとわかる）。</summary>
    private void WriteToFl(string session)
    {
        string text;
        lock (_gate)
        {
            if (!_outboxChanged && _writtenFlSeq == _flSeq && DateTime.UtcNow - _lastWrite < Heartbeat) return;
            var builder = new StringBuilder();
            builder.Append(CultureInfo.InvariantCulture,
                $"dawsync-app 1 {session} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {_flInstance ?? "-"} {_flSeq}\n");
            foreach (var (seq, line) in _outbox) builder.Append(CultureInfo.InvariantCulture, $"{seq} {line}\n");
            builder.Append("end\n");
            text = builder.ToString();
            _outboxChanged = false;
        }
        // FL Studio が読んでいる・ウイルス対策ソフトが開いているなどで書けないことがある。
        // 切断はせず、書けなければ次の回（20 ミリ秒後）にもう一度書く
        if (TryWriteAtomic(ToFlPath, text))
        {
            _lastWrite = DateTime.UtcNow;
            _writtenFlSeq = _flSeq;
        }
        else
        {
            lock (_gate) _outboxChanged = true;
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
        var session = Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
        {
            _outbox.Clear();
            _outSeq = 0;
            _outboxChanged = true;
            _ackProgress = DateTime.UtcNow;
        }
        // 前の接続の FL Studio の番号は使わない（FL Studio は新しい番号を見て hello から送り直す）
        _flInstance = null;
        _flSeq = 0;
        _writtenFlSeq = -1;
        var stream = client.GetStream();
        _link = new Link(client, stream, session);
        _ = Task.Run(() => ReadFromBridgeAsync(client, stream));
    }

    /// <summary>アプリ（LiveBridge）→ FL Studio: 届いた行を、FL Studio に渡す列に積む（次の回に to_fl.txt に書く）。</summary>
    private async Task ReadFromBridgeAsync(TcpClient client, Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (!_cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                if (line.Length == 0) continue;
                lock (_gate)
                {
                    _outbox.Add((++_outSeq, line));
                    _outboxChanged = true;
                }
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

    private static bool TryWriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
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
        lock (_gate)
        {
            _outbox.Clear();
            _outboxChanged = false;
        }
        // FL Studio にすぐ切れたとわかるよう印を消す（消せなくても、時刻が古くなれば切れたとわかる）
        try { File.Delete(ToFlPath); }
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
