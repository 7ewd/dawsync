using System.Buffers.Binary;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DawSync.Core.Sync;

/// <summary>
/// ルームをインターネットに公開する（Cloudflare の無料「クイックトンネル」）。
/// アカウント登録は不要。参加する人は何もインストールしなくてよく、表示された
/// xxx.trycloudflare.com のアドレスを入れるだけで参加できる。通信は Cloudflare が暗号化する。
/// Cloudflare 公式の cloudflared を初回だけダウンロードして使う。
/// </summary>
public sealed partial class CloudflareTunnel : IAsyncDisposable
{
    private Process? _process;
    private volatile bool _disposed;

    // 動いている cloudflared（アプリが途中で終わっても残らないように、終了時にまとめて止める）
    private static readonly HashSet<Process> Running = [];
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private const long MinToolSize = 1024 * 1024;

    static CloudflareTunnel()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
    }

    public string? PublicAddress { get; private set; }

    /// <summary>トンネルが止まった（cloudflared が終了した）</summary>
    public event Action? Stopped;

    public static string ToolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DawSync", "bin");

    public static string ToolPath => Path.Combine(ToolDirectory, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    private static string LegacyToolPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbletonMulti", "bin",
        OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    private static string PreviousMalteseToolPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maltese", "bin",
        OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    private static string InstalledToolPath => LooksExecutable(ToolPath) ? ToolPath
        : LooksExecutable(PreviousMalteseToolPath) ? PreviousMalteseToolPath
        : LegacyToolPath;

    /// <summary>cloudflared が入っていて、壊れていなさそうか。</summary>
    public static bool IsInstalled => LooksExecutable(ToolPath) || LooksExecutable(PreviousMalteseToolPath) || LooksExecutable(LegacyToolPath);

    private static string AssetName =>
        OperatingSystem.IsWindows() ? "cloudflared-windows-amd64.exe"
        : OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-darwin-arm64.tgz" : "cloudflared-darwin-amd64.tgz")
        : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-linux-arm64" : "cloudflared-linux-amd64";

    /// <summary>cloudflared が無ければ Cloudflare の GitHub からダウンロードする。</summary>
    public static async Task EnsureInstalledAsync(IProgress<(long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        await InstallLock.WaitAsync(ct);
        try
        {
            if (IsInstalled) return;
            Directory.CreateDirectory(ToolDirectory);
            // 前回のダウンロードが壊れていたら消してやり直す
            TryDelete(ToolPath);
            var url = $"https://github.com/cloudflare/cloudflared/releases/latest/download/{AssetName}";
            var download = Path.Combine(ToolDirectory, AssetName + ".download");
            var temp = ToolPath + ".tmp";
            try
            {
                await DownloadAsync(url, download, progress, ct);
                await ExtractAsync(download, temp, ct);
                if (!LooksExecutable(temp))
                    throw new IOException("ダウンロードしたファイルが壊れていました。もう一度お試しください");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                               | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(temp, ToolPath, overwrite: true);
            }
            finally
            {
                // 途中で失敗しても、次はまた最初からダウンロードする
                TryDelete(download);
                TryDelete(temp);
            }
        }
        finally
        {
            InstallLock.Release();
        }
    }

    private static async Task DownloadAsync(string url, string path, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        // 全体で 5 分、30 秒進まなければあきらめる（回線が止まったまま待ち続けないように）
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(DownloadTimeout);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var output = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await output.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                done += read;
                progress?.Report((done, total));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(overall.IsCancellationRequested
                ? "ダウンロードに時間がかかりすぎたので止めました。回線を確認して、もう一度お試しください"
                : "ダウンロードが途中で止まってしまいました。回線を確認して、もう一度お試しください");
        }
    }

    private static async Task ExtractAsync(string download, string temp, CancellationToken ct)
    {
        TryDelete(temp);
        if (!download.EndsWith(".tgz.download", StringComparison.Ordinal))
        {
            File.Move(download, temp, overwrite: true);
            return;
        }
        try
        {
            await using var file = File.OpenRead(download);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var tar = new TarReader(gzip);
            while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
            {
                if (Path.GetFileName(entry.Name) != "cloudflared" || entry.DataStream is null) continue;
                await using var output = File.Create(temp);
                await entry.DataStream.CopyToAsync(output, ct);
                break;
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            throw new IOException("ダウンロードしたファイルが壊れていました。もう一度お試しください", e);
        }
        if (!File.Exists(temp)) throw new IOException("cloudflared を取り出せませんでした");
    }

    /// <summary>実行ファイルらしいか（途中で切れたファイルや、エラーページを保存したものでないか）。</summary>
    private static bool LooksExecutable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinToolSize) return false;
            Span<byte> head = stackalloc byte[4];
            using (var file = File.OpenRead(path))
                if (file.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4) return false;
            if (OperatingSystem.IsWindows()) return head[0] == 'M' && head[1] == 'Z';
            var magic = BinaryPrimitives.ReadUInt32BigEndian(head);
            if (OperatingSystem.IsMacOS())
                return magic is 0xFEEDFACF or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
            return magic == 0x7F454C46; // ELF
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>トンネルを開いて、公開アドレス（xxx.trycloudflare.com）を返す。</summary>
    public async Task<string> StartAsync(int localPort, TimeSpan timeout, CancellationToken ct = default)
    {
        Stop();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var info = new ProcessStartInfo(InstalledToolPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{localPort}" })
            info.ArgumentList.Add(arg);

        var address = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (log) { if (log.Count < 200) log.Add(line); }
            if (UrlRegex().Match(line) is { Success: true } m) address.TrySetResult(m.Groups[1].Value);
            if (line.Contains("Registered tunnel connection", StringComparison.Ordinal)) registered.TrySetResult();
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) =>
        {
            lock (Running) Running.Remove(process);
            string last;
            lock (log) last = string.Join(" / ", log.TakeLast(3));
            address.TrySetException(new IOException("cloudflared が終了しました: " + last));
            if (Interlocked.CompareExchange(ref _process, null, process) == process)
            {
                PublicAddress = null;
                Stopped?.Invoke();
            }
        };
        lock (Running) Running.Add(process);
        try
        {
            process.Start();
        }
        catch
        {
            lock (Running) Running.Remove(process);
            process.Dispose();
            throw;
        }
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // 起動している間に Dispose されたら、すぐに止める
        if (_disposed) Stop();

        try
        {
            var url = await address.Task.WaitAsync(timeout, ct);
            // アドレスが出ても、Cloudflare との接続ができるまで数秒かかるので待つ
            await Task.WhenAny(registered.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            PublicAddress = url;
            return url;
        }
        catch (TimeoutException)
        {
            Stop();
            throw new TimeoutException("Cloudflare から公開用のアドレスが届きませんでした。しばらくしてからもう一度お試しください");
        }
        catch
        {
            Stop();
            throw;
        }
    }

    /// <summary>cloudflared をその場で（子プロセスごと）止める。</summary>
    public void Stop()
    {
        var process = Interlocked.Exchange(ref _process, null);
        PublicAddress = null;
        if (process is not null) Kill(process);
    }

    public Task StopAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>このアプリが起動した cloudflared をすべて止める（アプリの終了時用）。</summary>
    public static void KillAll()
    {
        Process[] all;
        lock (Running) all = [.. Running];
        foreach (var process in all) Kill(process);
    }

    private static void Kill(Process process)
    {
        lock (Running) Running.Remove(process);
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        process.Dispose();
    }

    // "https://words-words.trycloudflare.com" の部分
    [GeneratedRegex(@"https://([a-z0-9-]+\.trycloudflare\.com)")]
    private static partial Regex UrlRegex();
}
