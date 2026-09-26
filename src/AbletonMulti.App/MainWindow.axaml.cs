using Avalonia.Controls;

namespace AbletonMulti.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    protected override async void OnClosed(EventArgs e)
    {
        await SessionPage.ShutdownAsync();
        base.OnClosed(e);
    }
}
