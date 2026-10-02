using Avalonia.Media;

namespace DawSync.App;

/// <summary>画面の色。ライト/ダーク両方で読める中間色にしている。</summary>
public static class Palette
{
    public static readonly Color Red = Color.Parse("#E5484D");
    public static readonly Color Amber = Color.Parse("#E08A00");
    public static readonly Color Green = Color.Parse("#30A46C");
    public static readonly Color Blue = Color.Parse("#3E83F8");
    public static readonly Color Purple = Color.Parse("#8E4EC6");
    public static readonly Color Gray = Color.Parse("#8B8D98");

    public static IBrush Solid(Color c) => new SolidColorBrush(c);
    public static IBrush Tint(Color c, byte alpha = 0x2A) => new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));

}
