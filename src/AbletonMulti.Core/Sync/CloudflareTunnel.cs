using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// ルームをインターネットに公開する（Cloudflare の無料「クイックトンネル」）。
/// アカウント登録は不要。参加する人は何もインストールしなくてよく、表示された
/// xxx.trycloudflare.com のアドレスを入れるだけで参加できる。通信は Cloudflare が暗号化する。
/// Cloudflare 公式の cloudflared を初回だけダウンロードして使う。
/// </summary>
public sealed partial class CloudflareTunnel : IAsyncDisposable
{
    private Process? _process;

    public string? PublicAddress { get; private set; }

    /// <summary>トンネルが止まった（cloudflared が終了した）</summary>
    public event Action? Stopped;

    public static string ToolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbletonMulti", "bin");

    public static string ToolPath => Path.Combine(ToolDirectory, OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");

    public static bool IsInstalled => File.Exists(ToolPath);

    private static string AssetName =>
        OperatingSystem.IsWindows() ? "cloudflared-windows-amd64.exe"
        : OperatingSystem.IsMacOS() ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-darwin-arm64.tgz" : "cloudflared-darwin-amd64.tgz")
        : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "cloudflared-linux-arm64" : "cloudflared-linux-amd64";

    /// <summary>cloudflared が無ければ Cloudflare の GitHub からダウンロードする。</summary>
    public static async Task EnsureInstalledAsync(IProgress<(long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled) return;
        Directory.CreateDirectory(ToolDirectory);
        var url = $"https://github.com/cloudflare/cloudflared/releases/latest/download/{AssetName}";
        var download = Path.Combine(ToolDirectory, AssetName + ".download");

        using (var http = new HttpClient())
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(download);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress?.Report((done, total));
            }
        }

        var temp = ToolPath + ".tmp";
        if (AssetName.EndsWith(".tgz", StringComparison.Ordinal))
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
            File.Delete(download);
        }
        else
        {
            File.Move(download, temp, overwrite: true);
        }
        if (!File.Exists(temp)) throw new IOException("cloudflared を取り出せませんでした");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Move(temp, ToolPath, overwrite: true);
    }

    /// <summary>トンネルを開いて、公開アドレス（xxx.trycloudflare.com）を返す。</summary>
    public async Task<string> StartAsync(int localPort, TimeSpan timeout, CancellationToken ct = default)
    {
        await StopAsync();
        var info = new ProcessStartInfo(ToolPath)
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
            address.TrySetException(new IOException("cloudflared が終了しました: " + string.Join(" / ", log.TakeLast(3))));
            if (_process == process)
            {
                _process = null;
                PublicAddress = null;
                Stopped?.Invoke();
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        try
        {
            var url = await address.Task.WaitAsync(timeout, ct);
            // アドレスが出ても、Cloudflare との接続ができるまで数秒かかるので待つ
            await Task.WhenAny(registered.Task, Task.Delay(TimeSpan.FromSeconds(15), ct));
            PublicAddress = url;
            return url;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public Task StopAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        PublicAddress = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    // "https://words-words.trycloudflare.com" の部分
    [GeneratedRegex(@"https://([a-z0-9-]+\.trycloudflare\.com)")]
    private static partial Regex UrlRegex();
}
