namespace AbletonMulti.Core.Sync;

/// <summary>
/// REAPER に AbletonMulti のスクリプトを入れる。REAPER の設定フォルダの Scripts/AbletonMulti/ に置き、
/// Scripts/__startup.lua（REAPER が起動するたびに実行される）から読み込むようにする。
/// </summary>
public static class ReaperScriptInstaller
{
    private const string ResourcePrefix = "ReaperScript/";
    private const string StartupMarker = "-- AbletonMulti";
    private const string StartupLine =
        "dofile(reaper.GetResourcePath() .. \"/Scripts/AbletonMulti/abletonmulti.lua\") " + StartupMarker;

    /// <summary>REAPER の設定フォルダ（Options → Show REAPER resource path で開く場所）。</summary>
    public static string ResourceDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "Application Support", "REAPER");
            if (OperatingSystem.IsLinux()) return Path.Combine(home, ".config", "REAPER");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "REAPER");
        }
    }

    private static string ScriptDirectory => Path.Combine(ResourceDirectory, "Scripts", "AbletonMulti");
    private static string StartupScript => Path.Combine(ResourceDirectory, "Scripts", "__startup.lua");

    /// <summary>この PC に REAPER がありそうか。</summary>
    public static bool IsReaperPresent()
    {
        if (Directory.Exists(ResourceDirectory)) return true;
        if (OperatingSystem.IsWindows())
            return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "REAPER (x64)", "reaper.exe"))
                || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "REAPER", "reaper.exe"));
        if (OperatingSystem.IsMacOS()) return Directory.Exists("/Applications/REAPER.app");
        return false;
    }

    public static ScriptInstallState GetState()
    {
        if (!File.Exists(Path.Combine(ScriptDirectory, "abletonmulti.lua"))) return ScriptInstallState.NotInstalled;
        if (!File.Exists(StartupScript) || !File.ReadAllText(StartupScript).Contains(StartupMarker)) return ScriptInstallState.Outdated;
        foreach (var (name, content) in EmbeddedFiles())
        {
            var path = Path.Combine(ScriptDirectory, name);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return ScriptInstallState.Outdated;
        }
        return ScriptInstallState.Installed;
    }

    /// <summary>スクリプトを入れる。REAPER を次に起動したときから自動で動く（起動中なら再起動が必要）。</summary>
    public static string Install()
    {
        Directory.CreateDirectory(ScriptDirectory);
        foreach (var (name, content) in EmbeddedFiles())
            File.WriteAllBytes(Path.Combine(ScriptDirectory, name), content);
        var startup = File.Exists(StartupScript) ? File.ReadAllText(StartupScript) : "";
        if (!startup.Contains(StartupMarker))
        {
            if (startup.Length > 0 && !startup.EndsWith('\n')) startup += "\n";
            File.WriteAllText(StartupScript, startup + StartupLine + "\n");
        }
        return ScriptDirectory;
    }

    private static IEnumerable<(string Name, byte[] Content)> EmbeddedFiles()
    {
        var assembly = typeof(ReaperScriptInstaller).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            yield return (resource[ResourcePrefix.Length..], buffer.ToArray());
        }
    }
}
