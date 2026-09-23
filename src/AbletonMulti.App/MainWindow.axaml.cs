using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AbletonMulti.Core;

namespace AbletonMulti.App;

public partial class MainWindow : Window
{
    private readonly Task<PluginCatalog> _catalog;
    private string? _currentPath;
    private AnalysisResult? _result;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _reloadDebounce;

    public MainWindow()
    {
        InitializeComponent();
        MaxList.ItemTemplate = SampleList.ItemTemplate;

        _catalog = Task.Run(() => PluginScanner.Scan());
        _catalog.ContinueWith(t => Dispatcher.UIThread.Post(() =>
            ScanStatus.Text = t.IsCompletedSuccessfully
                ? $"この PC のプラグイン: {t.Result.Plugins.Count} 個"
                : "プラグインの検索に失敗しました"));

        OpenButton.Click += async (_, _) => await PickAndOpenAsync();
        ReopenButton.Click += async (_, _) => await PickAndOpenAsync();
        ExportButton.Click += async (_, _) => await ExportManifestAsync();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDragHighlight(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ---- ファイルを開く ----

    private async Task PickAndOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Ableton Live のセットを選ぶ",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Ableton Live Set") { Patterns = ["*.als"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            await LoadAsync(path);
    }

    public async Task LoadAsync(string path)
    {
        ScanStatus.Text = "読み込み中…";
        try
        {
            var catalog = await _catalog;
            var result = await Task.Run(() => ProjectAnalyzer.Analyze(path, catalog));
            _currentPath = path;
            _result = result;
            Show(result);
            Watch(path);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            ErrorText.Text = $"読み込めませんでした: {e.Message}";
            ErrorText.IsVisible = true;
            if (_result is null) ShowEmpty();
            else ShowToast($"読み込めませんでした: {e.Message}");
        }
        finally
        {
            if (_catalog.IsCompletedSuccessfully)
                ScanStatus.Text = $"この PC のプラグイン: {_catalog.Result.Plugins.Count} 個";
        }
    }

    // ---- 画面の反映 ----

    private void ShowEmpty()
    {
        EmptyView.IsVisible = true;
        ResultView.IsVisible = false;
    }

    private void Show(AnalysisResult r)
    {
        var p = r.Project;
        EmptyView.IsVisible = false;
        ErrorText.IsVisible = false;
        ResultView.IsVisible = true;

        ProjectName.Text = p.Name;
        ProjectMeta.Text = string.Join("  ·  ", new[]
        {
            p.LiveVersion.Length > 0 ? $"Ableton Live {p.LiveVersion}" : p.Creator,
            p.Tempo is { } bpm ? $"{bpm:0.##} BPM" : "",
            p.ProjectDirectory,
        }.Where(s => s.Length > 0));

        Stats.Children.Clear();
        AddStat("トラック", p.Tracks.Count(t => t.Kind != TrackKind.Main));
        AddStat("プラグイン", p.Plugins.Count);
        AddStat("サンプル", p.Samples.Count);
        AddStat("Max for Live", p.MaxDevices.Count);
        AddStat("MIDI ノート", p.Tracks.Sum(t => t.MidiNoteCount));

        var overall = r.Overall == Severity.Info ? Severity.Ok : r.Overall;
        var color = Palette.For(overall);
        Verdict.Background = Palette.Tint(color, 0x1F);
        Verdict.BorderBrush = Palette.Tint(color, 0x66);
        VerdictIcon.Background = Palette.Solid(color);
        VerdictGlyph.Text = overall == Severity.Ok ? "✓" : "!";
        (VerdictTitle.Text, VerdictDetail.Text) = overall switch
        {
            Severity.Error => ("このままでは共有できません",
                $"直す必要があるものが {r.ErrorCount} 件{(r.WarningCount > 0 ? $"、注意点が {r.WarningCount} 件" : "")}あります。下のチェック結果を確認してください。"),
            Severity.Warning => ("共有できますが、注意点があります",
                $"注意点が {r.WarningCount} 件あります。相手の環境によっては正しく鳴らない可能性があります。"),
            _ => ("共有の準備ができています", "このプロジェクトフォルダをそのまま共同編集者に渡せます。"),
        };

        IssueList.ItemsSource = r.Issues.Select(i => new IssueVm(i)).ToList();
        PluginList.ItemsSource = p.Plugins.Select(x => new PluginVm(x)).ToList();
        SampleList.ItemsSource = p.Samples.Select(x => new FileVm(x)).ToList();
        MaxList.ItemsSource = p.MaxDevices.Select(x => new FileVm(x)).ToList();
        TrackList.ItemsSource = p.Tracks.Select(x => new TrackVm(x)).ToList();

        var problems = r.ErrorCount + r.WarningCount;
        IssuesTab.Header = problems > 0 ? $"チェック結果（{problems}）" : "チェック結果";
        PluginsTab.Header = $"プラグイン（{p.Plugins.Count}）";
        SamplesTab.Header = $"サンプル（{p.Samples.Count}）";
        MaxTab.Header = $"Max for Live（{p.MaxDevices.Count}）";
        MaxTab.IsVisible = p.MaxDevices.Count > 0;
        TracksTab.Header = $"トラック（{p.Tracks.Count(t => t.Kind != TrackKind.Main)}）";
        Title = $"{p.Name} — AbletonMulti";
    }

    private void AddStat(string label, int value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = value.ToString("N0"), FontSize = 22, FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12.5, Classes = { "subtle" } });
        Stats.Children.Add(panel);
    }

    private async void ShowToast(string message)
    {
        Toast.Text = message;
        Toast.IsVisible = true;
        await Task.Delay(3500);
        if (Toast.Text == message) Toast.IsVisible = false;
    }

    // ---- Live で保存されたら自動で再チェック ----

    private void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        // Live は一時ファイルに書いてから置き換えるので、Renamed/Created も拾う
        _watcher.Changed += (_, _) => ScheduleReload();
        _watcher.Created += (_, _) => ScheduleReload();
        _watcher.Renamed += (_, _) => ScheduleReload();
    }

    private void ScheduleReload() => Dispatcher.UIThread.Post(() =>
    {
        _reloadDebounce?.Stop();
        _reloadDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, async (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            if (_currentPath is null) return;
            await LoadAsync(_currentPath);
            ShowToast("Live で保存されたので再チェックしました");
        });
        _reloadDebounce.Start();
    });

    // ---- マニフェスト書き出し ----

    private async Task ExportManifestAsync()
    {
        if (_result is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "マニフェストを保存",
            SuggestedFileName = $"{_result.Project.Name}.manifest.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;

        ExportButton.IsEnabled = false;
        try
        {
            var progress = new Progress<(int Done, int Total)>(p => ExportButton.Content = $"ハッシュ計算中… {p.Done}/{p.Total}");
            var json = await Task.Run(() => ManifestWriter.BuildJsonAsync(_result, hashFiles: true, progress));
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
            ShowToast($"書き出しました: {file.Name}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowToast($"書き出せませんでした: {e.Message}");
        }
        finally
        {
            ExportButton.Content = "マニフェストを書き出す";
            ExportButton.IsEnabled = true;
        }
    }

    // ---- ドラッグ＆ドロップ ----

    private static string? DroppedAls(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && p.EndsWith(".als", StringComparison.OrdinalIgnoreCase));

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var ok = DroppedAls(e) is not null;
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        SetDragHighlight(ok);
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = DroppedAls(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetDragHighlight(false);
        if (DroppedAls(e) is { } path)
            await LoadAsync(path);
        else
            ShowToast(".als ファイルをドロップしてください");
    }

    private void SetDragHighlight(bool on)
    {
        DropOverlay.IsVisible = on && ResultView.IsVisible;
        DropRect.Classes.Set("drag", on);
    }

    public async Task SaveScreenshotAsync(string path, int tabIndex)
    {
        Tabs.SelectedIndex = tabIndex;
        // Expander を開いた状態も確認できるよう、最初の項目だけ展開する
        await Task.Delay(300);
        foreach (var expander in IssueList.GetVisualDescendants().OfType<Expander>().Take(1))
            expander.IsExpanded = true;
        await Task.Delay(500);
        var scale = RenderScaling;
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(
            new Avalonia.PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale)), new Avalonia.Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
