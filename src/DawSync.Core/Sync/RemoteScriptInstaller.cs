using System.Xml.Linq;

namespace DawSync.Core.Sync;

public enum ScriptInstallState { NotInstalled, Outdated, Installed }

/// <summary>Live の User Library に DawSync Remote Script を入れる。</summary>
public static class RemoteScriptInstaller
{
    public const string ScriptName = "DawSync";
    private const string ResourcePrefix = "RemoteScript/";

    public static string ScriptDirectory => Path.Combine(FindUserLibrary(), "Remote Scripts", ScriptName);

    /// <summary>この PC に Ableton Live がありそうか（Live の設定フォルダがあるか）。</summary>
    public static bool IsLivePresent()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton")
                : Path.Combine(home, "Library", "Preferences", "Ableton");
            return Directory.Exists(root) && Directory.GetDirectories(root, "Live *").Length > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 調べられないときは「ありそう」とする（入れるボタンが出るだけなので害はない）
            return true;
        }
    }

    public static ScriptInstallState GetState()
    {
        try
        {
            var dir = ScriptDirectory;
            if (!File.Exists(Path.Combine(dir, "__init__.py"))) return ScriptInstallState.NotInstalled;
            foreach (var (name, content) in EmbeddedFiles())
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                    return InstalledIsNewer(dir) ? ScriptInstallState.Installed : ScriptInstallState.Outdated;
            }
            return ScriptInstallState.Installed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 読めないときは入れ直してもらう（ボタンから入れ直せば直ることが多い）
            return ScriptInstallState.Outdated;
        }
    }

    /// <summary>入っているスクリプトの方が、このアプリに入っているものより新しい（古いアプリで上書きして戻さない）。</summary>
    private static bool InstalledIsNewer(string dir)
    {
        const string pattern = "SCRIPT_VERSION\\s*=\\s*\"([^\"]+)\"";
        var path = Path.Combine(dir, "multi.py");
        var embedded = EmbeddedFiles().FirstOrDefault(f => f.Name == "multi.py").Content;
        if (!File.Exists(path) || embedded is null) return false;
        return AppInfo.Compare(AppInfo.FindVersion(File.ReadAllBytes(path), pattern), AppInfo.FindVersion(embedded, pattern)) > 0;
    }

    public static string Install()
    {
        MigrateLegacyScriptDirectories();
        var dir = ScriptDirectory;
        Directory.CreateDirectory(dir);
        foreach (var (name, content) in EmbeddedFiles())
            File.WriteAllBytes(Path.Combine(dir, name), content);
        // Live が作ったキャッシュが古いままだと新しいコードが読まれないことがあるので消す
        var cache = Path.Combine(dir, "__pycache__");
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        return dir;
    }

    /// <summary>旧名の Remote Script は削除せずに退避し、Live に旧版を二重登録させない。</summary>
    private static void MigrateLegacyScriptDirectories()
    {
        var parent = Path.Combine(FindUserLibrary(), "Remote Scripts");
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

    private static IEnumerable<(string Name, byte[] Content)> EmbeddedFiles()
    {
        var assembly = typeof(RemoteScriptInstaller).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            yield return (resource[ResourcePrefix.Length..], buffer.ToArray());
        }
    }

    /// <summary>
    /// Live の設定（Library.cfg）から User Library の場所を探す。
    /// 見つからなければ OS ごとの既定の場所を返す。
    /// </summary>
    public static string FindUserLibrary()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configs = new List<FileInfo>();
        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
            if (Directory.Exists(root))
                configs.AddRange(Directory.GetDirectories(root, "Live *")
                    .Select(d => new FileInfo(Path.Combine(d, "Preferences", "Library.cfg"))));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var root = Path.Combine(home, "Library", "Preferences", "Ableton");
            if (Directory.Exists(root))
                configs.AddRange(Directory.GetDirectories(root, "Live *")
                    .SelectMany(d => new[] { Path.Combine(d, "Library.cfg"), Path.Combine(d, "Preferences", "Library.cfg") })
                    .Select(p => new FileInfo(p)));
        }

        foreach (var cfg in configs.Where(f => f.Exists).OrderByDescending(f => f.LastWriteTimeUtc))
        {
            try
            {
                var project = XDocument.Load(cfg.FullName).Descendants("UserLibrary").Descendants("LibraryProject").FirstOrDefault();
                var path = (string?)project?.Element("ProjectPath")?.Attribute("Value");
                var name = (string?)project?.Element("ProjectName")?.Attribute("Value");
                if (path is { Length: > 0 } && name is { Length: > 0 })
                {
                    var library = Path.Combine(path, name);
                    if (Directory.Exists(library)) return library;
                }
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException) { }
        }

        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Music", "Ableton", "User Library")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ableton", "User Library");
    }
}
