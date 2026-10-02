using System.Text;

namespace AbletonMulti.Core.Sync;

/// <summary>
/// REAPER に AbletonMulti のスクリプトを入れる。REAPER の設定フォルダの Scripts/AbletonMulti/ に置き、
/// Scripts/__startup.lua（REAPER が起動するたびに実行される）から読み込むようにする。
/// </summary>
public static class ReaperScriptInstaller
{
    private const string ResourcePrefix = "ReaperScript/";
    // 行の最後に付ける印（入っているかはこれで見る。前の版の「dofile(...) -- AbletonMulti」も同じ印で見つかる）
    private const string StartupMarker = "-- AbletonMulti";
    // スクリプトのフォルダが消されていても REAPER の起動時にエラーにならないよう、あるときだけ読み込む
    private const string StartupLine =
        "do local f = reaper.GetResourcePath() .. \"/Scripts/AbletonMulti/abletonmulti.lua\"; " +
        "if reaper.file_exists(f) then dofile(f) end end " + StartupMarker;

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
        if (!StartupHasMarker()) return ScriptInstallState.Outdated;
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
        const string pattern = "local VERSION\\s*=\\s*\"([^\"]+)\"";
        var path = Path.Combine(ScriptDirectory, "abletonmulti.lua");
        var embedded = EmbeddedFiles().FirstOrDefault(f => f.Name == "abletonmulti.lua").Content;
        if (!File.Exists(path) || embedded is null) return false;
        return AppInfo.Compare(AppInfo.FindVersion(File.ReadAllBytes(path), pattern), AppInfo.FindVersion(embedded, pattern)) > 0;
    }

    /// <summary>スクリプトを入れる。REAPER を次に起動したときから自動で動く（起動中なら再起動が必要）。</summary>
    public static string Install()
    {
        Directory.CreateDirectory(ScriptDirectory);
        foreach (var (name, content) in EmbeddedFiles())
            File.WriteAllBytes(Path.Combine(ScriptDirectory, name), content);
        // __startup.lua はユーザーのファイル（Shift-JIS などのこともある）なので、文字として読み書きし直さず、
        // 末尾にバイトのまま 1 行足すだけにする
        var startup = File.Exists(StartupScript) ? File.ReadAllBytes(StartupScript) : [];
        if (!ContainsMarker(startup))
        {
            var line = (startup.Length > 0 && startup[^1] != (byte)'\n' ? "\n" : "") + StartupLine + "\n";
            using var stream = new FileStream(StartupScript, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
        return ScriptDirectory;
    }

    private static bool StartupHasMarker() => File.Exists(StartupScript) && ContainsMarker(File.ReadAllBytes(StartupScript));

    // 印は ASCII だけなので、文字コードに関係なくバイトで探せる
    private static bool ContainsMarker(byte[] content) =>
        content.AsSpan().IndexOf(Encoding.ASCII.GetBytes(StartupMarker)) >= 0;

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
