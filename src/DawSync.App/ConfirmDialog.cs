using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DawSync.App;

/// <summary>「本当にいいですか？」の確認画面。</summary>
public static class ConfirmDialog
{
    public static Task<bool> AskAsync(Window owner, string title, string message, string ok, string cancel)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var okButton = new Button { Content = ok, Padding = new Thickness(18, 8) };
        var cancelButton = new Button { Content = cancel, Padding = new Thickness(18, 8) };
        okButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, FontSize = 13.5, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton },
                },
            },
        };
        return dialog.ShowDialog<bool>(owner);
    }

    /// <summary>いくつかの中から選ぶ画面。選んだ選択肢の番号を返す（閉じた・やめたときは -1）。</summary>
    public static async Task<int> ChooseAsync(Window owner, string title, string message,
        IReadOnlyList<(string Label, string Detail)> choices, string cancel)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (message.Length > 0)
            panel.Children.Add(new TextBlock { Text = message, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10),
                Content = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = choices[i].Label, FontSize = 14, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = choices[i].Detail, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 },
                    },
                },
            };
            button.Click += (_, _) => dialog.Close(index);
            panel.Children.Add(button);
        }
        var cancelButton = new Button { Content = cancel, Padding = new Thickness(18, 8), HorizontalAlignment = HorizontalAlignment.Right };
        cancelButton.Click += (_, _) => dialog.Close(-1);
        panel.Children.Add(cancelButton);
        dialog.Content = panel;
        var result = await dialog.ShowDialog<int?>(owner);
        return result ?? -1;
    }
}
