using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DawSync.App;

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
            // ボタンの処理などで拾えなかったエラーでもアプリを落とさず、ログとお知らせに出す
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                try
                {
                    window.SessionPage.ReportError(e.Exception);
                }
                catch (Exception)
                {
                    ErrorLog.Write("UIThread", e.Exception);
                }
            };
            // 開発用: --host / --publish / --join アドレス --key 合言葉 / --screenshot 画像 [--delay 秒]
            var args = desktop.Args ?? [];
            string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            window.Opened += async (_, _) =>
            {
                if (args.Contains("--host")) await window.SessionPage.HostAsync();
                if (args.Contains("--publish")) await window.SessionPage.PublishForDevAsync();
                if (Option("--join") is { } address) await window.SessionPage.JoinAsync(address, Option("--key"));
                if (Option("--screenshot") is { } shot)
                {
                    if (Option("--delay") is { } delay)
                        await Task.Delay(TimeSpan.FromSeconds(double.Parse(delay, System.Globalization.CultureInfo.InvariantCulture)));
                    await window.SaveScreenshotAsync(shot);
                    desktop.Shutdown();
                }
            };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}