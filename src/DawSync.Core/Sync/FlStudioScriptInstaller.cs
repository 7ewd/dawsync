namespace DawSync.Core.Sync;

/// <summary>
/// FL Studio に DawSync の MIDI スクリプトを入れる。FL Studio のユーザーデータの Settings/Hardware/DawSync/ に置くと、
/// MIDI 設定の Controller type に「DAW Sync」が出る（FL Studio を起動し直すか、MIDI 設定の「MIDI スクリプト更新」で読み込まれる）。
/// </summary>
public static class FlStudioScriptInstaller
{
    private const string ResourcePrefix = "FlStudioScript/";
    private const string ScriptFile = "device_DawSync.py";

    /// <summary>FL Studio のユーザーデータのフォルダ（File settings の User data folder。既定は Documents/Image-Line/FL Studio）。</summary>
    public static string UserDataDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                // FL Studio は場所を変えられるので、FL Studio 自身が書いている場所を先に見る
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Image-Line\Shared\Paths");
                    if (key?.GetValue("Shared data") is string shared && shared.Length > 0)
                        return Path.Combine(shared, "FL Studio");
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Image-Line", "FL Studio");
        }
    }

    public static string ScriptDirectory => Path.Combine(UserDataDirectory, "Settings", "Hardware", "DawSync");

    /// <summary>この PC に FL Studio がありそうか。</summary>
    public static bool IsFlStudioPresent()
    {
        if (Directory.Exists(Path.Combine(UserDataDirectory, "Settings"))) return true;
        if (OperatingSystem.IsWindows())
        {
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Image-Line");
            return Directory.Exists(programs) && Directory.GetDirectories(programs, "FL Studio*").Length > 0;
        }
        if (OperatingSystem.IsMacOS())
            return Directory.Exists("/Applications") && Directory.GetDirectories("/Applications", "FL Studio*.app").Length > 0;
        return false;  // Linux 版の FL Studio は無い
    }

    public static ScriptInstallState GetState()
    {
        if (!File.Exists(Path.Combine(ScriptDirectory, ScriptFile))) return ScriptInstallState.NotInstalled;
        foreach (var (name, content) in EmbeddedFiles())
        {
            var path = Path.Combine(ScriptDirectory, name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                return InstalledIsNewer() ? ScriptInstallState.Installed : ScriptInstallState.Outdated;
        }
        return ScriptInstallState.Installed;
    }

    /// <summary>入っているスクリプトの方が、このアプリに入っているものより新しい（古いアプリで上書きして戻さない）。</summary>
    private static bool InstalledIsNewer()
    {
        const string pattern = "SCRIPT_VERSION\\s*=\\s*\"([^\"]+)\"";
        var path = Path.Combine(ScriptDirectory, ScriptFile);
        var embedded = EmbeddedFiles().FirstOrDefault(f => f.Name == ScriptFile).Content;
        if (!File.Exists(path) || embedded is null) return false;
        return AppInfo.Compare(AppInfo.FindVersion(File.ReadAllBytes(path), pattern), AppInfo.FindVersion(embedded, pattern)) > 0;
    }

    /// <summary>スクリプトを入れる。FL Studio を起動し直すと、MIDI 設定の Controller type に「DAW Sync」が出る。</summary>
    public static string Install()
    {
        var dir = ScriptDirectory;
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in EmbeddedFiles())
            File.WriteAllBytes(Path.Combine(dir, name), content);
        // FL Studio が作ったキャッシュが古いままだと新しいコードが読まれないことがあるので消す
        var cache = Path.Combine(dir, "__pycache__");
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        // スクリプトはフォルダを作れないので、やりとりの場所（このフォルダの ipc/）もここで作っておく
        Directory.CreateDirectory(Path.Combine(FlStudioRelay.IpcRoot, LiveBridge.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return dir;
    }

    private static IEnumerable<(string Name, byte[] Content)> EmbeddedFiles()
    {
        var assembly = typeof(FlStudioScriptInstaller).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            yield return (resource[ResourcePrefix.Length..], buffer.ToArray());
        }
    }
}
