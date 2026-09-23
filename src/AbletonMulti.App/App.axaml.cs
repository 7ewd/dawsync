using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AbletonMulti.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            // .als をアプリにドロップして起動した場合や「このアプリで開く」の場合
            var args = desktop.Args ?? [];
            var als = args.FirstOrDefault(a => a.EndsWith(".als", StringComparison.OrdinalIgnoreCase));
            var shotIndex = Array.IndexOf(args, "--screenshot"); // 開発用: 画面を PNG に書き出して終了
            window.Opened += async (_, _) =>
            {
                if (als is not null) await window.LoadAsync(als);
                if (shotIndex >= 0 && shotIndex + 1 < args.Length)
                {
                    var tabIndex = Array.IndexOf(args, "--tab") is var ti and >= 0 && ti + 1 < args.Length ? int.Parse(args[ti + 1]) : 0;
                    await window.SaveScreenshotAsync(args[shotIndex + 1], tabIndex);
                    desktop.Shutdown();
                }
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}