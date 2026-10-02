using System.Text;

namespace DawSync.Core.Sync;

/// <summary>
/// REAPER に DawSync のスクリプトを入れる。REAPER の設定フォルダの Scripts/DawSync/ に置き、
/// Scripts/__startup.lua（REAPER が起動するたびに実行される）から読み込むようにする。
/// </summary>
public static class ReaperScriptInstaller
{
    private const string ResourcePrefix = "ReaperScript/";
    // 行の最後に付ける印（この行だけを更新し、他の起動設定は残す）。
    private const string StartupMarker = "-- DawSync";
    // 改名前の自動起動行を置き換えるためだけに使う。
    private static readonly string[] LegacyStartupMarkers = ["-- Maltese", "-- AbletonMulti"];
    // スクリプトのフォルダが消されていても REAPER の起動時にエラーにならないよう、あるときだけ読み込む
    private const string StartupLine =
        "do local f = reaper.GetResourcePath() .. \"/Scripts/DawSync/dawsync.lua\"; " +
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

    private static string ScriptDirectory => Path.Combine(ResourceDirectory, "Scripts", "DawSync");
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
        if (!File.Exists(Path.Combine(ScriptDirectory, "dawsync.lua"))) return ScriptInstallState.NotInstalled;
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
        var path = Path.Combine(ScriptDirectory, "dawsync.lua");
        var embedded = EmbeddedFiles().FirstOrDefault(f => f.Name == "dawsync.lua").Content;
        if (!File.Exists(path) || embedded is null) return false;
        return AppInfo.Compare(AppInfo.FindVersion(File.ReadAllBytes(path), pattern), AppInfo.FindVersion(embedded, pattern)) > 0;
    }

    /// <summary>スクリプトを入れる。REAPER を次に起動したときから自動で動く（起動中なら再起動が必要）。</summary>
    public static string Install()
    {
        MigrateLegacyScriptDirectory();
        Directory.CreateDirectory(ScriptDirectory);
        foreach (var (name, content) in EmbeddedFiles())
            File.WriteAllBytes(Path.Combine(ScriptDirectory, name), content);
        // Shift-JIS などのユーザーの行・改行は元のバイトを保ち、アプリの自動起動行だけ更新する。
        var startup = File.Exists(StartupScript) ? File.ReadAllBytes(StartupScript) : [];
        var updated = UpdateStartupScript(startup);
        if (!startup.AsSpan().SequenceEqual(updated)) File.WriteAllBytes(StartupScript, updated);
        return ScriptDirectory;
    }

    private static bool StartupHasMarker()
    {
        if (!File.Exists(StartupScript)) return false;
        var content = File.ReadAllBytes(StartupScript);
        return ContainsMarker(content, StartupMarker) && LegacyStartupMarkers.All(marker => !ContainsMarker(content, marker));
    }

    private static byte[] UpdateStartupScript(byte[] content)
    {
        var hasCurrent = ContainsMarker(content, StartupMarker);
        using var output = new MemoryStream();
        for (var start = 0; start < content.Length;)
        {
            var newline = Array.IndexOf(content, (byte)'\n', start);
            var end = newline < 0 ? content.Length : newline + 1;
            var line = content.AsSpan(start, end - start);
            var legacyLine = false;
            foreach (var marker in LegacyStartupMarkers)
            {
                if (LineHasMarker(line, marker))
                {
                    legacyLine = true;
                    break;
                }
            }
            if (legacyLine)
            {
                if (!hasCurrent)
                {
                    output.Write(Encoding.ASCII.GetBytes(StartupLine));
                    if (newline >= 0)
                        output.Write(Encoding.ASCII.GetBytes(newline > start && content[newline - 1] == (byte)'\r' ? "\r\n" : "\n"));
                    hasCurrent = true;
                }
            }
            else output.Write(line);
            start = end;
        }
        if (!hasCurrent)
        {
            if (content.Length > 0 && content[^1] != (byte)'\n') output.WriteByte((byte)'\n');
            output.Write(Encoding.ASCII.GetBytes(StartupLine + "\n"));
        }
        return output.ToArray();
    }

    private static void MigrateLegacyScriptDirectory()
    {
        var parent = Path.Combine(ResourceDirectory, "Scripts");
        foreach (var name in new[] { "Maltese", "AbletonMulti" })
        {
            var legacy = Path.Combine(parent, name);
            if (!Directory.Exists(legacy)) continue;
            string backup;
            do backup = legacy + "." + Guid.NewGuid().ToString("N") + ".bak";
            while (Directory.Exists(backup) || File.Exists(backup));
            try { Directory.Move(legacy, backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // 行末の ASCII の印だけを判定し、ユーザーの文字コードには触れない。
    private static bool LineHasMarker(ReadOnlySpan<byte> line, string marker)
    {
        while (!line.IsEmpty && line[^1] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
            line = line[..^1];
        return line.EndsWith(Encoding.ASCII.GetBytes(marker));
    }

    private static bool ContainsMarker(byte[] content, string marker)
    {
        for (var start = 0; start < content.Length;)
        {
            var newline = Array.IndexOf(content, (byte)'\n', start);
            var end = newline < 0 ? content.Length : newline + 1;
            if (LineHasMarker(content.AsSpan(start, end - start), marker)) return true;
            start = end;
        }
        return false;
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
