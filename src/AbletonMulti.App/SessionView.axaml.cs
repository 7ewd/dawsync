using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AbletonMulti.Core.Sync;

namespace AbletonMulti.App;

public sealed class PeerVm(Peer peer, bool isMe)
{
    public string Name => peer.Name;
    public string Initial => peer.Name.Length > 0 ? peer.Name[..1].ToUpperInvariant() : "?";
    public string Tag => isMe ? "（あなた）" : "";
    public IBrush Color => SessionView.PeerBrush(peer.Id);
}

public sealed record AddressVm(string Address, string Label);

public sealed class ActivityVm : INotifyPropertyChanged
{
    public required string Who { get; init; }
    public required string? Key { get; init; }
    public required ActivityKind Kind { get; init; }
    public required IBrush Color { get; init; }

    public string Text { get => _text; set => Set(ref _text, value); }
    public DateTime Time { get => _time; set { Set(ref _time, value); OnChanged(nameof(TimeText)); } }
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsWarning => Kind == ActivityKind.Warning;

    private string _text = "";
    private DateTime _time;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name!);
    }
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class SessionView : UserControl
{
    private static readonly Color[] PeerColors = new[]
        { "#FF764D", "#3E83F8", "#30A46C", "#8E4EC6", "#E5484D", "#E08A00", "#12A594", "#D6409F" }
        .Select(Color.Parse).ToArray();

    public static IBrush PeerBrush(int id) => Palette.Solid(PeerColors[Math.Abs(id) % PeerColors.Length]);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SessionController _session;
    private readonly ObservableCollection<ActivityVm> _activity = [];
    private string? _bridgeError;
    private bool _howToShownOnce;

    public SessionView()
    {
        _session = new SessionController(new FileStore(_settings.SamplesFolder));
        InitializeComponent();
        ActivityList.ItemsSource = _activity;
        NameBox.Text = _settings.Name ?? Environment.UserName;
        AddressBox.Text = _settings.LastAddress;
        KeyBox.Text = _settings.LastKey;
        SamplesPath.Text = _session.Files.CacheDirectory;

        _session.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _session.Activity += e => Dispatcher.UIThread.Post(() => AddActivity(e));

        try
        {
            _session.StartBridge();
        }
        catch (SocketException)
        {
            _bridgeError = "AbletonMulti がもう 1 つ起動しているようです。どちらかを閉じてください。";
        }

        InstallButton.Click += (_, _) => InstallScript();
        BitwigInstallButton.Click += (_, _) => InstallBitwigExtension();
        HowToToggle.Click += (_, _) => ShowHowTo(!HowToPanel.IsVisible);
        HostButton.Click += async (_, _) =>
        {
            var start = await ChooseHostStartAsync();
            if (start < 0) return;
            await RunRoomActionAsync(() => _session.HostAsync(MyName(), startBlank: start == 0));
        };
        JoinButton.Click += async (_, _) => await JoinAsync();
        AddressBox.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await JoinAsync(); };
        LeaveButton.Click += async (_, _) => await _session.LeaveAsync();
        ResyncButton.Click += async (_, _) => await _session.SyncWithLiveAsync();
        OverwriteButton.Click += async (_, _) => await _session.SyncWithLiveAsync(overwrite: true);
        KeyBox.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await JoinAsync(); };
        OpenSamplesButton.Click += (_, _) => OpenFolder(_session.Files.CacheDirectory);
        ChangeSamplesButton.Click += async (_, _) => await ChangeSamplesFolderAsync();
        CopyInviteButton.Click += async (_, _) => await CopyInviteAsync();
        PublishButton.Click += async (_, _) => await PublishAsync();
        UnpublishButton.Click += async (_, _) => await _session.UnpublishAsync();
        CopyPublicButton.Click += async (_, _) => await CopyAsync(_session.InviteCode ?? "", CopyPublicButton, "コピー");
        AddressBox.TextChanged += (_, _) =>
        {
            // 招待コードを貼ったら、その場で合言葉の欄に分ける
            if (AddressBox.Text?.Split('#') is [var a, var k, ..] && k.Trim().Length > 0)
            {
                KeyBox.Text = k.Trim();
                AddressBox.Text = a.Trim();
            }
        };
        ClearButton.Click += (_, _) => { _activity.Clear(); Refresh(); };

        Refresh();
    }

    public ValueTask ShutdownAsync() => _session.DisposeAsync();

    // 起動引数（--host / --join アドレス）から使う
    public Task HostAsync() => RunRoomActionAsync(() => _session.HostAsync(MyName()));

    /// <summary>
    /// ルームはいつも「まっさらなセット」（MIDI トラック 1 本）から始める（人によってテンプレートが違っても揃うように）。
    /// 今開いているセットの中身が消えるので、確認する。
    /// </summary>
    /// <summary>
    /// ルームをどこから始めるか選ぶ。0: まっさらから、1: 今開いているセットの続きから、-1: やめる。
    /// 参加する人はどちらの場合もまっさらにしてから、ルームの内容を受け取る。
    /// </summary>
    private async Task<int> ChooseHostStartAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return 0;
        var shortcut = OperatingSystem.IsMacOS() ? "Cmd+N" : "Ctrl+N";
        return await ConfirmDialog.ChooseAsync(window, "ルームの始め方",
            "",
            [
                ("まっさらから始める",
                 "今開いているセットのトラック・クリップ・リターンなどを消して、トラック 1 本だけの状態から始めます。" +
                 $"大事なセットを開いている場合は、先に保存してから新規セット（{shortcut}）を開いてください。"),
                ("今のセットの続きから始める",
                 "今開いているセットの内容（トラック・クリップ・ノート・サンプル）をそのままルームに入れます。" +
                 "参加する人のセットはこの内容になります（音源・エフェクトは同期されないので、各自でかけてください）。"),
            ],
            "やめる");
    }

    private async Task<bool> ConfirmBlankAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return true;
        var shortcut = OperatingSystem.IsMacOS() ? "Cmd+N" : "Ctrl+N";
        return await ConfirmDialog.AskAsync(window,
            "セットをまっさらにします",
            "Live（または Bitwig）で今開いているセットのトラック・クリップ・リターンなどを消して、" +
            "トラック 1 本だけの状態にしてから始めます。\n\n" +
            $"大事なセットを開いている場合は、先に保存してから新規セット（{shortcut}）を開いてください。",
            "まっさらにして始める", "やめる");
    }
    public Task JoinAsync(string address, string? key)
    {
        AddressBox.Text = address;
        if (key is not null) KeyBox.Text = key;
        return JoinAsync();
    }

    public Task PublishForDevAsync() => PublishAsync();

    private string MyName()
    {
        var name = NameBox.Text?.Trim() is { Length: > 0 } n ? n : Environment.UserName;
        _settings.Name = name;
        _settings.Save();
        return name;
    }

    private async Task JoinAsync()
    {
        var address = AddressBox.Text?.Trim() ?? "";
        if (address.Length == 0)
        {
            ShowRoomError("招待コードかアドレスを入力してください");
            return;
        }
        // 招待コード（アドレス#合言葉）が貼られたら分ける
        if (address.Split('#') is [var a, var k, ..])
        {
            address = a.Trim();
            KeyBox.Text = k.Trim();
            AddressBox.Text = address;
        }
        var key = KeyBox.Text?.Trim() ?? "";
        if (key.Length == 0)
        {
            ShowRoomError("合言葉を入力してください（ホストの画面に出ています）");
            return;
        }
        _settings.LastAddress = address;
        _settings.LastKey = key;
        if (!await ConfirmBlankAsync()) return;
        await RunRoomActionAsync(() => _session.JoinAsync(address, MyName(), key, startBlank: true));
    }

    private async Task RunRoomActionAsync(Func<Task> action)
    {
        RoomError.IsVisible = false;
        HostButton.IsEnabled = JoinButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ShowRoomError("このPCではすでにルームが開かれています。");
        }
        catch (Exception e) when (e is SocketException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            ShowRoomError(e is InvalidOperationException
                ? e.Message
                : "ホストに接続できませんでした。アドレスが正しいか、ホストがルームを開いているか、同じネットワークにいるかを確認してください。");
        }
        finally
        {
            HostButton.IsEnabled = JoinButton.IsEnabled = true;
            Refresh();
        }
    }

    private async Task CopyInviteAsync()
    {
        var text = _session.InviteCode is { } code
            ? $"AbletonMulti の「招待コードかアドレス」にこれを貼って参加してください:\n{code}"
            : $"AbletonMulti のルームに参加してください\nアドレス:\n{string.Join("\n", ((IEnumerable<AddressVm>?)AddressList.ItemsSource ?? []).Select(a => $"  {a.Address}（{a.Label}）"))}\n合言葉: {_session.RoomKey}";
        await CopyAsync(text, CopyInviteButton, "招待をコピー");
    }

    private async Task CopyAsync(string text, Button button, string label)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        button.Content = "コピーしました";
        await Task.Delay(1500);
        button.Content = label;
    }

    private async Task PublishAsync()
    {
        PublishButton.IsEnabled = false;
        PublishStatus.IsVisible = true;
        PublishStatus.Text = CloudflareTunnel.IsInstalled ? "公開しています…（10 秒ほどかかります）" : "準備しています…";
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
                PublishStatus.Text = p.Total is { } t
                    ? $"初回だけ準備が必要です（Cloudflare のツールをダウンロード中 {p.Done * 100 / Math.Max(1, t)}%）"
                    : $"初回だけ準備が必要です（ダウンロード中 {p.Done / 1024 / 1024} MB）");
            await _session.PublishAsync(progress);
            PublishStatus.IsVisible = false;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or TimeoutException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            PublishStatus.Text = $"公開できませんでした: {e.Message}";
        }
        finally
        {
            PublishButton.IsEnabled = true;
            Refresh();
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
    }

    private async Task ChangeSamplesFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "受け取ったサンプルの保存先を選ぶ",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
        try
        {
            _session.Files.ChangeDirectory(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", $"その場所は使えません: {e.Message}"));
            return;
        }
        _settings.SamplesFolder = path;
        _settings.Save();
        SamplesPath.Text = path;
        AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.System, "", $"これから受け取るサンプルは「{path}」に保存します"));
    }

    private void ShowHowTo(bool show)
    {
        HowToPanel.IsVisible = show;
        HowToToggle.Content = show ? "つなぎ方を閉じる ▴" : "つなぎ方を見る ▾";
    }

    private void ShowRoomError(string message)
    {
        RoomError.Text = message;
        RoomError.IsVisible = true;
    }

    private void InstallScript()
    {
        try
        {
            var path = RemoteScriptInstaller.Install();
            ShowHowTo(true);
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.System, "", $"Live にスクリプトを入れました: {path}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", $"スクリプトを入れられませんでした: {e.Message}"));
        }
        Refresh();
    }

    private void InstallBitwigExtension()
    {
        try
        {
            var path = BitwigExtensionInstaller.Install();
            ShowHowTo(true);
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.System, "", $"Bitwig に拡張を入れました: {path}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", $"Bitwig に拡張を入れられませんでした: {e.Message}"));
        }
        Refresh();
    }

    private async void OnCopyAddress(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string address } button && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(address);
            button.Content = "コピーしました";
            await Task.Delay(1500);
            button.Content = "コピー";
        }
    }

    // ---- 表示の更新 ----

    private void Refresh()
    {
        // ① Live
        var live = _session.Bridge;
        var scriptState = RemoteScriptInstaller.GetState();
        if (_bridgeError is not null)
            SetStatus(LiveDot, LiveStatus, Palette.Red, _bridgeError);
        else if (live.IsConnected)
            SetStatus(LiveDot, LiveStatus, Palette.Green, $"{live.DawName} とつながっています");
        else
            SetStatus(LiveDot, LiveStatus, Palette.Amber, "Ableton Live か Bitwig を待っています…");

        // つながっていれば手順は不要（古いスクリプトのときだけ更新を促す）
        HowToToggle.IsVisible = !live.IsConnected;
        if (live.IsConnected) ShowHowTo(false);
        // Live だけ／Bitwig だけの PC では、使わない方のボタンは出さない（どちらも見つからなければ Live 用を出す）
        var bitwigPresent = BitwigExtensionInstaller.IsBitwigPresent();
        var livePresent = RemoteScriptInstaller.IsLivePresent() || !bitwigPresent;
        (InstallButton.Content, InstallButton.IsVisible) = scriptState switch
        {
            _ when !livePresent => ("", false),
            ScriptInstallState.NotInstalled => ("Live にスクリプトを入れる", true),
            ScriptInstallState.Outdated => ("スクリプトを更新する（そのあと Live を再起動）", true),
            _ => ("", false),
        };
        var bitwigState = BitwigExtensionInstaller.GetState();
        (BitwigInstallButton.Content, BitwigInstallButton.IsVisible) = bitwigState switch
        {
            _ when !bitwigPresent => ("", false),
            ScriptInstallState.NotInstalled => ("Bitwig に拡張を入れる", true),
            ScriptInstallState.Outdated => ("Bitwig の拡張を更新する", true),
            _ => ("", false),
        };
        if ((scriptState == ScriptInstallState.NotInstalled || bitwigPresent && bitwigState == ScriptInstallState.NotInstalled)
            && !live.IsConnected && !_howToShownOnce)
        {
            _howToShownOnce = true;
            ShowHowTo(true);
        }

        // ② ルーム
        JoinPanel.IsVisible = !_session.InRoom;
        InRoomPanel.IsVisible = _session.InRoom;
        HostInfo.IsVisible = _session.IsHost;
        if (_session.InRoom)
        {
            RoomTitle.Text = _session.IsHost ? "ルームを開いています" : $"参加中: {_session.RoomAddress}";
            var port = _session.HostPort ?? RoomServer.DefaultPort;
            KeyText.Text = _session.RoomKey;
            var published = _session.PublicAddress is not null;
            PublishedPanel.IsVisible = published;
            PublishButton.IsVisible = !published;
            PublicCodeText.Text = _session.InviteCode;
            AddressList.ItemsSource = RoomServer.LocalAddresses()
                .Select(a => new AddressVm(
                    port == RoomServer.DefaultPort ? a : $"{a}:{port}",
                    a.StartsWith("100.", StringComparison.Ordinal) ? "Tailscale 用" : "同じ Wi-Fi 用"))
                .ToList();
        }

        // 参加者
        PeersCard.IsVisible = _session.InRoom;
        PeersTitle.Text = $"参加者（{_session.Peers.Count} 人）";
        PeerList.ItemsSource = _session.Peers.Select(p => new PeerVm(p, p.Id == _session.MyId)).ToList();

        // 状態バナー
        var (color, title, detail, resync) =
            !_session.InRoom ? (Palette.Gray, "ルームに入っていません", "", false)
            : _session.PausedReason is { } reason ? (Palette.Red, "同期を止めています", reason, true)
            : !live.IsConnected ? (Palette.Amber, "Ableton Live / Bitwig がつながっていません", "", false)
            : _session.WaitingForHost ? (Palette.Amber, "ホストを待っています", "", false)
            : (Palette.Green, "同時編集中", "", false);
        Banner.Background = Palette.Tint(color, 0x1F);
        Banner.BorderBrush = Palette.Tint(color, 0x66);
        BannerDot.Fill = Palette.Solid(color);
        BannerTitle.Text = title;
        BannerDetail.Text = detail;
        BannerDetail.IsVisible = detail.Length > 0;
        ResyncButton.IsVisible = resync;
        OverwriteButton.IsVisible = resync && _session.CanOverwrite;
    }

    private static void SetStatus(Avalonia.Controls.Shapes.Ellipse dot, TextBlock text, Color color, string message)
    {
        dot.Fill = Palette.Solid(color);
        text.Text = message;
    }

    private void AddActivity(ActivityEntry e)
    {
        var who = e.Kind switch
        {
            ActivityKind.System or ActivityKind.Warning => "お知らせ",
            _ => e.Who,
        };

        // フェーダーをドラッグしている間などは同じ行を更新する（ログが流れすぎないように）
        if (e.Key is not null && _activity.FirstOrDefault() is { } top
            && top.Key == e.Key && top.Who == who && (e.Time - top.Time).TotalSeconds < 3)
        {
            top.Text = e.Text;
            top.Time = e.Time;
            return;
        }

        var peerId = _session.Peers.FirstOrDefault(p => p.Name == e.Who)?.Id;
        _activity.Insert(0, new ActivityVm
        {
            Who = who,
            Key = e.Key,
            Kind = e.Kind,
            Color = e.Kind switch
            {
                ActivityKind.Warning => Palette.Solid(Palette.Red),
                ActivityKind.System => Palette.Solid(Palette.Gray),
                _ => peerId is { } id ? PeerBrush(id) : Palette.Solid(Palette.Gray),
            },
            Text = e.Text,
            Time = e.Time,
        });
        while (_activity.Count > 300) _activity.RemoveAt(_activity.Count - 1);
    }
}

/// <summary>名前と最後に参加したアドレスを覚えておく。</summary>
public sealed class AppSettings
{
    public string? Name { get; set; }
    public string? LastAddress { get; set; }
    public string? LastKey { get; set; }
    /// <summary>受け取ったサンプルの保存先（null なら ドキュメント/AbletonMulti/Samples）</summary>
    public string? SamplesFolder { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AbletonMulti", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
