namespace AbletonMulti.Core.Sync;

/// <summary>Bitwig Studio の Extensions フォルダに AbletonMulti の拡張（.bwextension）を入れる。</summary>
public static class BitwigExtensionInstaller
{
    public const string FileName = "AbletonMulti.bwextension";
    private const string ResourceName = "BitwigExtension/" + FileName;

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
        if (Directory.Exists(BitwigUserDirectory)) return true;
        if (OperatingSystem.IsWindows())
            return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bitwig Studio"));
        if (OperatingSystem.IsMacOS()) return Directory.Exists("/Applications/Bitwig Studio.app");
        return false;
    }

    public static ScriptInstallState GetState()
    {
        var path = ExtensionPath;
        if (!File.Exists(path)) return ScriptInstallState.NotInstalled;
        var embedded = Embedded();
        if (embedded is null) return ScriptInstallState.Installed;
        return File.ReadAllBytes(path).AsSpan().SequenceEqual(embedded) ? ScriptInstallState.Installed : ScriptInstallState.Outdated;
    }

    /// <summary>拡張を入れる。Bitwig は Extensions フォルダを見張っているので、起動中でもすぐ読み込まれる。</summary>
    public static string Install()
    {
        var content = Embedded() ?? throw new InvalidOperationException("このビルドには Bitwig 用の拡張が入っていません");
        Directory.CreateDirectory(ExtensionsDirectory);
        File.WriteAllBytes(ExtensionPath, content);
        return ExtensionPath;
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
