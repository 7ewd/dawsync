namespace Maltese.Core.Sync;

/// <summary>Bitwig Studio の Extensions フォルダに Maltese の拡張（.bwextension）を入れる。</summary>
public static class BitwigExtensionInstaller
{
    public const string FileName = "Maltese.bwextension";
    private const string ResourceName = "BitwigExtension/" + FileName;
    // 同じ UUID を持つ改名前の拡張を退避するためだけに使う。
    private const string LegacyFileName = "AbletonMulti.bwextension";

    /// <summary>Bitwig の拡張の置き場所（Bitwig の設定で変えていなければここ）。</summary>
    public static string ExtensionsDirectory => Path.Combine(BitwigUserDirectory, "Extensions");

    public static string ExtensionPath => Path.Combine(ExtensionsDirectory, FileName);

    private static string BitwigUserDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsLinux()) return Path.Combine(home, "Bitwig Studio");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Bitwig Studio");
        }
    }

    /// <summary>この PC に Bitwig Studio がありそうか（一度でも起動すると Documents/Bitwig Studio ができる）。</summary>
    public static bool IsBitwigPresent()
    {
        try
        {
            if (Directory.Exists(BitwigUserDirectory)) return true;
            if (OperatingSystem.IsWindows())
                return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bitwig Studio"));
            if (OperatingSystem.IsMacOS()) return Directory.Exists("/Applications/Bitwig Studio.app");
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static ScriptInstallState GetState()
    {
        try
        {
            var path = ExtensionPath;
            var legacyPath = Path.Combine(ExtensionsDirectory, LegacyFileName);
            if (!File.Exists(path)) return File.Exists(legacyPath) ? ScriptInstallState.Outdated : ScriptInstallState.NotInstalled;
            if (File.Exists(legacyPath)) return ScriptInstallState.Outdated;
            var embedded = Embedded();
            if (embedded is null) return ScriptInstallState.Installed;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(embedded) ? ScriptInstallState.Installed : ScriptInstallState.Outdated;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 読めないときは入れ直してもらう（ボタンから入れ直せば直ることが多い）
            return ScriptInstallState.Outdated;
        }
    }

    /// <summary>拡張を入れる。Bitwig は Extensions フォルダを見張っているので、起動中でもすぐ読み込まれる。</summary>
    public static string Install()
    {
        var content = Embedded() ?? throw new InvalidOperationException("このビルドには Bitwig 用の拡張が入っていません");
        Directory.CreateDirectory(ExtensionsDirectory);
        File.WriteAllBytes(ExtensionPath, content);
        BackupLegacyExtension(ExtensionsDirectory);
        return ExtensionPath;
    }

    private static void BackupLegacyExtension(string directory)
    {
        var legacy = Path.Combine(directory, LegacyFileName);
        if (!File.Exists(legacy)) return;
        // .bak にして Bitwig の読み込み対象から外し、既存のバックアップも上書きしない。
        string backup;
        do backup = legacy + "." + Guid.NewGuid().ToString("N") + ".bak";
        while (File.Exists(backup));
        File.Move(legacy, backup);
    }

    private static byte[]? Embedded()
    {
        using var stream = typeof(BitwigExtensionInstaller).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
