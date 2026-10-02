namespace DawSync.App;

/// <summary>
/// 思わぬエラーをファイルに書いておく（アプリが落ちたときに原因を調べられるように）。
/// Windows: %LOCALAPPDATA%\DawSync\error.log、Mac: ~/Library/Application Support/DawSync/error.log
/// </summary>
public static class ErrorLog
{
    private const long MaxSize = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DawSync", "error.log");

    public static void Write(string source, object? error)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // 大きくなりすぎたら古い分は 1 つだけ残して新しく始める
                if (new FileInfo(path) is { Exists: true, Length: > MaxSize })
                    File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} (v{Core.AppInfo.Version}, {Environment.OSVersion})\n{error}\n\n");
            }
        }
        catch (Exception)
        {
            // ログが書けなくても、それでアプリを止めない
        }
    }
}
