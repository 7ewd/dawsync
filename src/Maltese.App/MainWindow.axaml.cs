using Avalonia.Controls;
using Maltese.Core.Sync;

namespace Maltese.App;

public partial class MainWindow : Window
{
    // ルームや公開の後片付けを待つのは最大でこれだけ（固まっても閉じられるように）
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(4);

    private bool _shuttingDown;
    private bool _readyToClose;

    public MainWindow()
    {
        InitializeComponent();
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (LanguageBox.SelectedIndex < 0) return;
            SessionPage.SetLanguage(LanguageBox.SelectedIndex == 1 ? AppLanguage.Japanese : AppLanguage.English);
            LanguageLabel.Text = Localization.Get("language", SessionPage.Language);
        };
        LanguageBox.SelectedIndex = SessionPage.Language == AppLanguage.Japanese ? 1 : 0;
        LanguageLabel.Text = Localization.Get("language", SessionPage.Language);
    }

    /// <summary>開発用: 画面を PNG に書き出す。</summary>
    public async Task SaveScreenshotAsync(string path)
    {
        await Task.Delay(500);
        var scale = RenderScaling;
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale)), new Avalonia.Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    /// <summary>
    /// 閉じるときは、いったん止めてルームと公開（cloudflared）の後片付けを待ってから本当に閉じる
    /// （待たずにアプリが終わると、cloudflared が動いたまま残ることがある）。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _readyToClose) return;
        e.Cancel = true;
        if (_shuttingDown) return;
        _shuttingDown = true;
        IsEnabled = false;
        _ = ShutdownAndCloseAsync();
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            await SessionPage.ShutdownAsync().AsTask().WaitAsync(ShutdownTimeout);
        }
        catch (Exception e)
        {
            // 時間切れやエラーでも閉じる（原因はログに残す）
            ErrorLog.Write("Shutdown", e);
        }
        finally
        {
            // 後片付けが終わらなかったときも、cloudflared だけは確実に止める
            CloudflareTunnel.KillAll();
            _readyToClose = true;
            Close();
        }
    }
}
