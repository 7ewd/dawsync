using Avalonia;
using System;

namespace DawSync.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 落ちたときに原因がわかるように、拾えなかったエラーはファイルに残す
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ErrorLog.Write("UnhandledException", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            ErrorLog.Write("Main", e);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
