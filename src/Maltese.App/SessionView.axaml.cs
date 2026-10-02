using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Maltese.Core.Sync;

namespace Maltese.App;

public sealed class PeerVm(Peer peer, bool isMe, AppLanguage language)
{
    public string Name => peer.Name;
    public string Initial => peer.Name.Length > 0 ? peer.Name[..1].ToUpperInvariant() : "?";
    public string Tag => isMe ? Localization.Get("you", language) : "";
    public IBrush Color => SessionView.PeerBrush(peer.Id);
}

public sealed record AddressVm(string Address, string Label, string CopyText);

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
    public Bitmap? Image { get; init; }
    public bool HasImage => Image is not null;

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

    private static Bitmap? _secretImage;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SessionController _session;
    private readonly ObservableCollection<ActivityVm> _activity = [];
    private readonly DispatcherTimer _environmentTimer;
    private AppLanguage _language;
    private string? _bridgeError;
    private bool _howToShownOnce;

    // ルームの操作（作る・参加・退出・公開をやめる）をしている間は true（Enter の連打などで 2 つ同時に動かないように）
    private bool _busy;
    // 公開の準備中（ダウンロードに時間がかかるので、その間もルームから出られるように _busy とは別にする）
    private bool _publishing;
    // 退出するたびに増やす（退出前に始めた公開が、後から終わっても使わないように）
    private int _publishGeneration;

    // DAW ごとのスクリプトの状態とこの PC のアドレス（ファイルやネットワークを調べるので、Refresh のたびには調べない）
    private InstallStates _install = InstallStates.Unknown;
    private IReadOnlyList<string> _localAddresses = [];
    private IReadOnlyList<AddressVm> _shownAddresses = [];
    private bool _checkingEnvironment;
    private bool _splittingInvite;

    public SessionView()
    {
        var (files, samplesWarning) = OpenFileStore(_settings.SamplesFolder);
        _session = new SessionController(files);
        InitializeComponent();
        _language = _settings.Language;
        ApplyLanguage();
        ActivityList.ItemsSource = _activity;
        NameBox.Text = _settings.Name ?? Environment.UserName;
        AddressBox.Text = _settings.LastAddress;
        KeyBox.Text = _settings.LastKey;
        SamplesPath.Text = _session.Files.CacheDirectory;
        if (samplesWarning is not null) AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", samplesWarning));

        _session.Changed += () => Dispatcher.UIThread.Post(Refresh);
        _session.Activity += e => Dispatcher.UIThread.Post(() => AddActivity(e));

        try
        {
            _session.StartBridge();
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _bridgeError = "Maltese がもう 1 つ起動しているようです。どちらかを閉じてください。";
        }
        catch (SocketException e)
        {
            // Windows がポートを予約している・権限がない など（もう 1 つ起動しているわけではない）
            _bridgeError = $"DAW とつなぐためのポート（{LiveBridge.DefaultPort}）を開けませんでした: {SocketReason(e)}";
            ErrorLog.Write("StartBridge", e);
        }
        catch (Exception e)
        {
            _bridgeError = $"DAW とつなぐ準備ができませんでした: {Describe(e)}";
            ErrorLog.Write("StartBridge", e);
        }

        InstallButton.Click += (_, _) => RunAction("スクリプトを入れられませんでした", InstallScript);
        BitwigInstallButton.Click += (_, _) => RunAction("Bitwig に拡張を入れられませんでした", InstallBitwigExtension);
        ReaperInstallButton.Click += (_, _) => RunAction("REAPER にスクリプトを入れられませんでした", InstallReaperScript);
        HowToToggle.Click += (_, _) => ShowHowTo(!HowToPanel.IsVisible);
        HostButton.Click += (_, _) => RunAction("ルームを作れませんでした", () => HostAsync(startBlank: null));
        JoinButton.Click += (_, _) => RunAction("ルームに参加できませんでした", JoinAsync);
        AddressBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) RunAction("ルームに参加できませんでした", JoinAsync); };
        KeyBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) RunAction("ルームに参加できませんでした", JoinAsync); };
        LeaveButton.Click += (_, _) => RunAction("ルームから出られませんでした", LeaveAsync);
        ResyncButton.Click += (_, _) => RunAction("合わせ直せませんでした", () => _session.SyncWithLiveAsync(), ResyncButton);
        OverwriteButton.Click += (_, _) => RunAction("上書きできませんでした", () => _session.SyncWithLiveAsync(overwrite: true), OverwriteButton);
        OpenSamplesButton.Click += (_, _) => RunAction("フォルダを開けませんでした", () => OpenFolder(_session.Files.CacheDirectory));
        ChangeSamplesButton.Click += (_, _) => RunAction("保存先を変えられませんでした", ChangeSamplesFolderAsync, ChangeSamplesButton);
        CopyInviteButton.Click += (_, _) => RunAction("コピーできませんでした", CopyInviteAsync);
        PublishButton.Click += (_, _) => RunAction("公開できませんでした", PublishAsync);
        UnpublishButton.Click += (_, _) => RunAction("公開をやめられませんでした", () => RunExclusiveAsync(_session.UnpublishAsync));
        CopyPublicButton.Click += (_, _) => RunAction("コピーできませんでした", () => CopyAsync(_session.InviteCode ?? "", CopyPublicButton, "コピー"));
        AddressBox.TextChanged += (_, _) =>
        {
            // 招待コードを貼ったら、その場で合言葉の欄に分ける（「招待をコピー」の文章ごと貼られても、コードだけ取り出す）
            if (_splittingInvite || ParseInvite(AddressBox.Text) is not ({ } a, { } k)) return;
            _splittingInvite = true;
            try
            {
                KeyBox.Text = k;
                AddressBox.Text = a;
            }
            finally
            {
                _splittingInvite = false;
            }
        };
        ClearButton.Click += (_, _) => { _activity.Clear(); Refresh(); };
        NameBox.TextChanged += (_, _) => CheckSecretInput(NameBox.Text);
        AddressBox.TextChanged += (_, _) => CheckSecretInput(AddressBox.Text);
        KeyBox.TextChanged += (_, _) => CheckSecretInput(KeyBox.Text);

        UpdateEnvironmentNow();
        _environmentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _environmentTimer.Tick += (_, _) => _ = UpdateEnvironmentAsync();
        _environmentTimer.Start();

        Refresh();
    }

    public ValueTask ShutdownAsync()
    {
        _environmentTimer.Stop();
        return _session.DisposeAsync();
    }

    /// <summary>The language currently used by the window. MainWindow binds its selector to this value.</summary>
    public AppLanguage Language => _language;

    public void SetLanguage(AppLanguage language)
    {
        if (_language == language) return;
        _language = language;
        _settings.Language = language;
        _settings.Save();
        ApplyLanguage();
        Refresh();
    }

    private string T(string key) => Localization.Get(key, _language);

    /// <summary>Apply all labels that are not data-bound. Runtime status text is refreshed separately.</summary>
    private void ApplyLanguage()
    {
        DawStep.Text = T("daw_step");
        RoomStep.Text = T("room_step");
        HowToToggle.Content = HowToPanel.IsVisible ? T("how_to_close") : T("how_to_open");
        HowLive1.Text = T("live_1");
        HowLive2.Text = T("live_2");
        HowLive3.Text = T("live_3");
        HowBitwig1.Text = T("bitwig_1");
        HowBitwig2.Text = T("bitwig_2");
        HowReaper1.Text = T("reaper_1");
        HowReaper2.Text = T("reaper_2");
        NameLabel.Text = T("your_name");
        NameBox.PlaceholderText = T("name_placeholder");
        HostButton.Content = T("create_room");
        OrText.Text = T("or");
        AddressBox.PlaceholderText = T("invite_placeholder");
        KeyBox.PlaceholderText = T("key_placeholder");
        JoinButton.Content = T("join");
        PublishButton.Content = T("publish");
        PublishedInviteText.Text = T("published_invite");
        CopyPublicButton.Content = T("copy");
        UnpublishButton.Content = T("unpublish");
        KeyLabel.Text = T("passphrase");
        CopyInviteButton.Content = T("copy_invite");
        LeaveButton.Content = T("leave");
        SamplesStep.Text = T("samples");
        OpenSamplesButton.Content = T("open");
        ChangeSamplesButton.Content = T("change");
        ResyncButton.Content = T("resync");
        OverwriteButton.Content = T("overwrite");
        ActivityTitle.Text = T("activity");
        ClearButton.Content = T("clear");
    }

    // 起動引数（--host / --join アドレス）から使う
    public Task HostAsync() => HostAsync(startBlank: false);

    /// <summary>
    /// 保存しておいたサンプルの保存先を開く。外付けドライブを外した等で使えなければ、既定の場所に切り替える
    /// （設定は書き換えない。ドライブをつなぎ直せば、次の起動からまた使える）。
    /// </summary>
    private static (FileStore Files, string? Warning) OpenFileStore(string? saved)
    {
        var candidates = new List<string>();
        if (saved is { Length: > 0 }) candidates.Add(saved);
        // 旧名の保存先があれば、新しい空のフォルダを作る前に引き継ぐ。
        AddIfExisting(FileStore.LegacyCacheDirectory());
        AddIfExisting(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AbletonMulti", "Samples"));
        AddIfExisting(Path.Combine(Path.GetTempPath(), "AbletonMulti", "Samples"));
        candidates.Add(FileStore.DefaultCacheDirectory());
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Maltese", "Samples"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "Maltese", "Samples"));

        void AddIfExisting(string path)
        {
            if (Directory.Exists(path) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
                candidates.Add(path);
        }

        Exception? first = null;
        foreach (var dir in candidates)
        {
            try
            {
                var files = new FileStore(dir);
                return (files, first is null ? null
                    : $"サンプルの保存先「{candidates[0]}」が使えないので、今回は「{files.CacheDirectory}」に保存します（{Describe(first)}）");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException or System.Security.SecurityException)
            {
                ErrorLog.Write($"FileStore({dir})", e);
                first ??= e;
            }
        }
        // どこにも書けない（普通は起こらない）: 最後の候補で作って、エラーはそのまま出す
        return (new FileStore(candidates[^1]), null);
    }

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
        return await ConfirmDialog.ChooseAsync(window, T("host_start_title"),
            "",
            [
                (T("start_blank"), string.Format(T("start_blank_detail"), shortcut)),
                (T("start_current"), T("start_current_detail")),
            ],
            T("cancel"));
    }

    private async Task<bool> ConfirmBlankAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return true;
        var shortcut = OperatingSystem.IsMacOS() ? "Cmd+N" : "Ctrl+N";
        return await ConfirmDialog.AskAsync(window,
            T("blank_confirm_title"),
            string.Format(T("blank_confirm_detail"), shortcut),
            T("start_blank_action"), T("cancel"));
    }
    public Task JoinAsync(string address, string? key)
    {
        AddressBox.Text = address;
        if (key is not null) KeyBox.Text = key;
        return JoinAsync();
    }

    public Task PublishForDevAsync() => PublishAsync();

    /// <summary>思わぬエラーを、アプリを落とさずにお知らせに出す（App の UI スレッドのエラー処理からも使う）。</summary>
    public void ReportError(Exception e, string? message = null)
    {
        ErrorLog.Write(message ?? "UI", e);
        AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "",
            $"{message ?? "思わぬエラーが起きました"}: {Describe(e)}"));
    }

    private string MyName()
    {
        var name = NameBox.Text?.Trim() is { Length: > 0 } n ? n : Environment.UserName;
        _settings.Name = name;
        _settings.Save();
        return name;
    }

    /// <summary>ボタンなどの操作を実行する。思わぬエラーでもアプリを落とさず、お知らせに出す。</summary>
    /// <param name="disable">実行している間、押せなくしておくボタン</param>
    private async void RunAction(string failMessage, Func<Task> action, Control? disable = null)
    {
        if (disable is not null) disable.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            ReportError(e, failMessage);
        }
        finally
        {
            if (disable is not null) disable.IsEnabled = true;
            UpdateRoomButtons();
        }
    }

    private void RunAction(string failMessage, Action action) => RunAction(failMessage, () =>
    {
        action();
        return Task.CompletedTask;
    });

    private Task JoinAsync() => RunExclusiveAsync(async () =>
    {
        var address = AddressBox.Text?.Trim() ?? "";
        if (address.Length == 0)
        {
            ShowRoomError("招待コードかアドレスを入力してください");
            return;
        }
        // 招待コード（アドレス#合言葉）が貼られたら分ける
        if (ParseInvite(address) is ({ } a, { } k))
        {
            address = a;
            KeyBox.Text = k;
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
        await ConnectAsync(() => _session.JoinAsync(address, MyName(), key, startBlank: true), hosting: false);
    });

    /// <param name="startBlank">null ならどこから始めるかを聞く</param>
    private Task HostAsync(bool? startBlank) => RunExclusiveAsync(async () =>
    {
        if (startBlank is not { } blank)
        {
            var start = await ChooseHostStartAsync();
            if (start < 0) return;
            blank = start == 0;
        }
        await ConnectAsync(() => _session.HostAsync(MyName(), startBlank: blank), hosting: true);
    });

    private Task LeaveAsync() => RunExclusiveAsync(async () =>
    {
        // 公開の準備中に出たら、その公開は使わない
        _publishGeneration++;
        PublishStatus.IsVisible = false;
        await _session.LeaveAsync();
    });

    /// <summary>ルームの操作（作る・参加・退出・公開をやめる）は同時に 1 つだけ動かす。</summary>
    private async Task RunExclusiveAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateRoomButtons();
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
            UpdateRoomButtons();
            Refresh();
        }
    }

    /// <summary>ルームを作る・ルームに参加する。失敗したらわけをルームの欄に出す。</summary>
    private async Task ConnectAsync(Func<Task> action, bool hosting)
    {
        RoomError.IsVisible = false;
        try
        {
            await action();
        }
        catch (SocketException e) when (hosting && e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            ShowRoomError($"このPCではすでにルームが開かれています（またはほかのアプリがポート {RoomServer.DefaultPort} を使っています）。");
        }
        catch (SocketException e) when (hosting)
        {
            ErrorLog.Write("Host", e);
            ShowRoomError($"ルームを開けませんでした: {SocketReason(e)}");
        }
        catch (InvalidOperationException e)
        {
            // 合言葉が違う・ルームを先に作って等（サーバーやアプリからの説明をそのまま出す）
            ShowRoomError(Describe(e));
        }
        catch (Exception e) when (hosting)
        {
            ErrorLog.Write("Host", e);
            ShowRoomError($"ルームを開けませんでした: {Describe(e)}");
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException)
        {
            ShowRoomError("招待コード（アドレス）の形が正しくありません。ホストから届いた招待コードを、そのまま全部貼り付けてください。");
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException)
        {
            ShowRoomError("ホストに接続できませんでした。招待コードが正しいか（途中で切れたり、打ち間違えたりしていないか）、" +
                          "ホストがインターネットに公開したままになっているかを確認してください。" + TimeoutNote(e));
        }
        catch (Exception e)
        {
            // 接続の失敗はどんなものでもアプリを落とさずに知らせる
            ShowRoomError("ホストに接続できませんでした。アドレスが正しいか、ホストがルームを開いているか、同じネットワークにいるかを確認してください。" + TimeoutNote(e));
        }
    }

    private static string TimeoutNote(Exception e) => IsTimeout(e) ? "（時間内に応答がありませんでした）" : "";

    private async Task CopyInviteAsync()
    {
        // どの行を貼っても参加できるように、アドレスごとに「アドレス#合言葉」の形にする
        var text = _session.InviteCode is { } code
            ? $"Maltese の「招待コードかアドレス」にこれを貼って参加してください:\n{code}"
            : "Maltese の「招待コードかアドレス」に、どれか 1 行を貼って参加してください:\n" +
              string.Join("\n", _shownAddresses.Select(a => $"{a.Address}#{_session.RoomKey}（{a.Label}）"));
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
        if (_busy || _publishing || !_session.IsHost || _session.RoomKey is not { } room) return;
        _publishing = true;
        var generation = _publishGeneration;
        // 準備している間に退出した・別のルームを作り直した ときは、この公開の結果は使わない
        bool Current() => generation == _publishGeneration && _session.IsHost && _session.RoomKey == room;
        UpdateRoomButtons();
        PublishStatus.IsVisible = true;
        PublishStatus.Text = CloudflareTunnel.IsInstalled ? "公開しています…（10 秒ほどかかります）" : "準備しています…";
        try
        {
            var progress = new Progress<(long Done, long? Total)>(p =>
            {
                if (!Current()) return;
                PublishStatus.Text = p.Total is { } t
                    ? $"初回だけ準備が必要です（Cloudflare のツールをダウンロード中 {p.Done * 100 / Math.Max(1, t)}%）"
                    : $"初回だけ準備が必要です（ダウンロード中 {p.Done / 1024 / 1024} MB）";
            });
            await _session.PublishAsync(progress);
            if (!Current())
            {
                // もう無いルームのために開いてしまったトンネルは閉じる
                await _session.UnpublishAsync();
                return;
            }
            PublishStatus.IsVisible = false;
        }
        catch (Exception) when (!Current())
        {
            // 退出した後の失敗は知らせない
        }
        catch (Exception e)
        {
            ErrorLog.Write("Publish", e);
            PublishStatus.Text = $"公開できませんでした: {Describe(e)}";
        }
        finally
        {
            _publishing = false;
            UpdateRoomButtons();
            Refresh();
        }
    }

    private static Task OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
        return Task.CompletedTask;
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", $"その場所は使えません: {Describe(e)}"));
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
        HowToToggle.Content = show ? T("how_to_close") : T("how_to_open");
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
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", $"スクリプトを入れられませんでした: {Describe(e)}"));
        }
        UpdateEnvironmentNow();
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
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", $"Bitwig に拡張を入れられませんでした: {Describe(e)}"));
        }
        UpdateEnvironmentNow();
        Refresh();
    }

    private void InstallReaperScript()
    {
        try
        {
            var path = ReaperScriptInstaller.Install();
            ShowHowTo(true);
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.System, "", $"REAPER にスクリプトを入れました（REAPER を再起動すると動きます）: {path}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AddActivity(new ActivityEntry(DateTime.Now, ActivityKind.Warning, "", $"REAPER にスクリプトを入れられませんでした: {Describe(e)}"));
        }
        UpdateEnvironmentNow();
        Refresh();
    }

    private void OnCopyAddress(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string address } button)
            RunAction("コピーできませんでした", () => CopyAsync(address, button, "コピー"));
    }

    // ---- 招待コード ----

    /// <summary>
    /// 貼られた文章から招待コード（アドレス#合言葉）を取り出す。
    /// 「招待をコピー」の文章ごと貼られても、改行が消えていても、コードの部分だけを使う。
    /// </summary>
    public static (string? Address, string? Key) ParseInvite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('#')) return (null, null);
        if (InviteRegex().Match(text) is not { Success: true } m) return (null, null);
        var hash = m.Value.LastIndexOf('#');
        var address = m.Value[..hash].TrimEnd('/');
        var key = m.Value[(hash + 1)..];
        return address.Length > 0 && key.Length > 0 ? (address, key) : (null, null);
    }

    // "xxx.trycloudflare.com#ABC123"、"192.168.1.10:47401#ABC123"、"https://xxx.trycloudflare.com/#ABC123" など
    [GeneratedRegex(@"(?:(?:wss?|https?)://)?[A-Za-z0-9.\-]+(?::\d+)?/?#[A-Za-z0-9]{4,}", RegexOptions.IgnoreCase)]
    private static partial Regex InviteRegex();

    // ---- エラーの説明 ----

    /// <summary>例外を、画面に出す短い日本語の説明にする（英語のままのメッセージをなるべく出さない）。</summary>
    public static string Describe(Exception e)
    {
        switch (e)
        {
            case AggregateException { InnerExceptions.Count: 1 } a:
                return Describe(a.InnerExceptions[0]);
            case SocketException s:
                return SocketReason(s);
            case WebSocketException { InnerException: { } inner }:
                return Describe(inner);
            case HttpRequestException { StatusCode: { } status }:
                return $"サーバーからエラーが返ってきました（{(int)status}）";
            case HttpRequestException { InnerException: SocketException s }:
                return SocketReason(s);
            case HttpRequestException:
                return "インターネットにつながらないようです";
            case UnauthorizedAccessException:
                return "アクセスが許可されていません（権限がありません）";
        }
        if (IsJapanese(e.Message)) return e.Message;
        if (IsTimeout(e)) return "時間内に応答がありませんでした";
        return e switch
        {
            OperationCanceledException => "途中で中止しました",
            WebSocketException => "相手とつながりませんでした",
            FileNotFoundException or DirectoryNotFoundException => "ファイルまたはフォルダが見つかりません",
            _ => e.Message,
        };
    }

    private static bool IsTimeout(Exception e) =>
        e is TimeoutException
        || e is OperationCanceledException { InnerException: TimeoutException }
        || e is SocketException { SocketErrorCode: SocketError.TimedOut }
        || e.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || e.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
        || e.InnerException is { } inner && IsTimeout(inner);

    private static bool IsJapanese(string text) =>
        text.Any(c => c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿');

    /// <summary>ソケットのエラーを日本語で。</summary>
    public static string SocketReason(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse => "ほかのアプリがこのポートを使っています",
        SocketError.AccessDenied => "このポートは使えません（Windows に予約されているか、権限がありません）",
        SocketError.AddressNotAvailable => "このアドレスは使えません",
        SocketError.TimedOut => "時間内に応答がありませんでした",
        SocketError.ConnectionRefused => "相手が接続を受け付けていません",
        SocketError.ConnectionReset or SocketError.ConnectionAborted => "接続が切れました",
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "相手が見つかりませんでした",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown => "ネットワークにつながっていません",
        _ => $"通信のエラー（{e.SocketErrorCode}）",
    };

    // ---- インストール状態・アドレスの確認（5 秒ごと）----

    private sealed record InstallStates(
        bool LivePresent, bool BitwigPresent, bool ReaperPresent,
        ScriptInstallState Live, ScriptInstallState Bitwig, ScriptInstallState Reaper)
    {
        public static readonly InstallStates Unknown = new(true, false, false,
            ScriptInstallState.Installed, ScriptInstallState.Installed, ScriptInstallState.Installed);
    }

    private static T Try<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            // 調べられないものは、ボタンを出さない側に倒す（毎回お知らせを出すとうるさいので黙って）
            return fallback;
        }
    }

    private static (InstallStates Install, IReadOnlyList<string> Addresses) ReadEnvironment() => (
        new InstallStates(
            Try(RemoteScriptInstaller.IsLivePresent, true),
            Try(BitwigExtensionInstaller.IsBitwigPresent, false),
            Try(ReaperScriptInstaller.IsReaperPresent, false),
            Try(RemoteScriptInstaller.GetState, ScriptInstallState.Installed),
            Try(BitwigExtensionInstaller.GetState, ScriptInstallState.Installed),
            Try(ReaperScriptInstaller.GetState, ScriptInstallState.Installed)),
        Try(() => (IReadOnlyList<string>)RoomServer.LocalAddresses().ToList(), []));

    /// <summary>今すぐ調べ直す（起動時と、スクリプトを入れた直後）。</summary>
    private void UpdateEnvironmentNow()
    {
        (_install, _localAddresses) = ReadEnvironment();
    }

    /// <summary>5 秒ごとに裏で調べ直し、変わっていたら画面を更新する。</summary>
    private async Task UpdateEnvironmentAsync()
    {
        if (_checkingEnvironment) return;
        _checkingEnvironment = true;
        try
        {
            var (install, addresses) = await Task.Run(ReadEnvironment);
            if (install == _install && addresses.SequenceEqual(_localAddresses)) return;
            _install = install;
            _localAddresses = addresses;
            Refresh();
        }
        catch (Exception e)
        {
            ErrorLog.Write("UpdateEnvironment", e);
        }
        finally
        {
            _checkingEnvironment = false;
        }
    }

    private void UpdateRoomButtons()
    {
        HostButton.IsEnabled = JoinButton.IsEnabled = LeaveButton.IsEnabled = UnpublishButton.IsEnabled = !_busy;
        PublishButton.IsEnabled = !_busy && !_publishing;
    }

    // ---- 表示の更新 ----

    private void Refresh()
    {
        // ① Live
        var live = _session.Bridge;
        var install = _install;
        if (_bridgeError is not null)
            SetStatus(LiveDot, LiveStatus, Palette.Red, _bridgeError);
        else if (live.IsConnected)
            SetStatus(LiveDot, LiveStatus, Palette.Green, $"{live.DawName}{T("connected")}" +
                (live.WaitingDaw is { } waiting ? _language == AppLanguage.Japanese ? $"（{waiting}{T("waiting_daw")}）" : $" ({waiting}{T("waiting_daw")})" : ""));
        else
            SetStatus(LiveDot, LiveStatus, Palette.Amber, T("waiting"));

        // つながっていれば手順は不要（古いスクリプトのときだけ更新を促す）
        HowToToggle.IsVisible = !live.IsConnected;
        if (live.IsConnected) ShowHowTo(false);
        // Live だけ／Bitwig だけの PC では、使わない方のボタンは出さない（どちらも見つからなければ Live 用を出す）
        var bitwigPresent = install.BitwigPresent;
        var reaperPresent = install.ReaperPresent;
        var livePresent = install.LivePresent || (!bitwigPresent && !reaperPresent);
        var scriptState = install.Live;
        (InstallButton.Content, InstallButton.IsVisible) = scriptState switch
        {
            _ when !livePresent => ("", false),
            ScriptInstallState.NotInstalled => (T("live_install"), true),
            ScriptInstallState.Outdated => (T("live_update"), true),
            _ => ("", false),
        };
        var reaperState = install.Reaper;
        (ReaperInstallButton.Content, ReaperInstallButton.IsVisible) = reaperState switch
        {
            _ when !reaperPresent => ("", false),
            ScriptInstallState.NotInstalled => (T("reaper_install"), true),
            ScriptInstallState.Outdated => (T("reaper_update"), true),
            _ => ("", false),
        };
        var bitwigState = install.Bitwig;
        (BitwigInstallButton.Content, BitwigInstallButton.IsVisible) = bitwigState switch
        {
            _ when !bitwigPresent => ("", false),
            ScriptInstallState.NotInstalled => (T("bitwig_install"), true),
            ScriptInstallState.Outdated => (T("bitwig_update"), true),
            _ => ("", false),
        };
        if ((scriptState == ScriptInstallState.NotInstalled || bitwigPresent && bitwigState == ScriptInstallState.NotInstalled
             || reaperPresent && reaperState == ScriptInstallState.NotInstalled)
            && !live.IsConnected && !_howToShownOnce)
        {
            _howToShownOnce = true;
            ShowHowTo(true);
        }

        // ② ルーム
        JoinPanel.IsVisible = !_session.InRoom;
        InRoomPanel.IsVisible = _session.InRoom;
        if (_secretImagePending && _session.InRoom && _session.BroadcastSecretImage())
            _secretImagePending = false;
        HostInfo.IsVisible = _session.IsHost;
        // ホストしていなければ、公開の途中経過や失敗の表示は消しておく（次にルームを作ったときに残らないように）
        if (!_session.IsHost) PublishStatus.IsVisible = false;
        if (_session.InRoom)
        {
            RoomTitle.Text = _session.IsHost ? T("room_open") : $"{T("joined")}{_session.RoomAddress}";
            var port = _session.HostPort ?? RoomServer.DefaultPort;
            KeyText.Text = _session.RoomKey;
            var published = _session.PublicAddress is not null;
            PublishedPanel.IsVisible = published;
            PublishButton.IsVisible = !published;
            PublicCodeText.Text = _session.InviteCode;
            // 変わったときだけ作り直す（コピーボタンの「コピーしました」表示などが消えないように）
            var addresses = _localAddresses
                .Select(a => new AddressVm(
                    port == RoomServer.DefaultPort ? a : $"{a}:{port}",
                    a.StartsWith("100.", StringComparison.Ordinal)
                        ? _language == AppLanguage.Japanese ? "Tailscale 用" : "For Tailscale"
                        : _language == AppLanguage.Japanese ? "同じ Wi-Fi 用" : "Same Wi-Fi",
                    T("copy")))
                .ToList();
            if (!addresses.SequenceEqual(_shownAddresses))
            {
                _shownAddresses = addresses;
                AddressList.ItemsSource = addresses;
            }
        }

        // 参加者
        PeersCard.IsVisible = _session.InRoom;
        PeersTitle.Text = Localization.Count("participant", "参加者", _session.Peers.Count, _language);
        PeerList.ItemsSource = _session.Peers.Select(p => new PeerVm(p, p.Id == _session.MyId, _language)).ToList();

        // 状態バナー
        var (color, title, detail, resync) =
            !_session.InRoom ? (Palette.Gray, T("not_in_room"), "", false)
            : _session.Reconnecting ? (Palette.Amber, T("reconnecting"),
                T("reconnecting_detail"), false)
            : _session.PausedReason is { } reason ? (Palette.Red, "同期を止めています", reason, true)
            : !live.IsConnected ? (Palette.Amber, T("disconnected"), "", false)
            : _session.WaitingForHost ? (Palette.Amber, T("waiting_host"), "", false)
            : (Palette.Green, T("editing"), "", false);
        // テーマは黒地と白い細線で統一し、状態は左のドットで示す。
        Banner.Background = Palette.Solid(Color.Parse("#000000"));
        Banner.BorderBrush = Palette.Solid(Color.Parse("#FFFFFF"));
        BannerDot.Fill = Palette.Solid(color);
        BannerTitle.Text = title;
        BannerDetail.Text = detail;
        BannerDetail.IsVisible = detail.Length > 0;
        ResyncButton.IsVisible = resync;
        OverwriteButton.IsVisible = resync && _session.CanOverwrite;
        UpdateRoomButtons();
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
            ActivityKind.System or ActivityKind.Warning => T("notice"),
            _ => e.Who,
        };

        // 同じクリップを続けて編集している間などは同じ行を更新する（ログが流れすぎないように）。
        // 2 人が同時に編集していると行が交互に並ぶので、直近の数行から探して一番上に持ってくる
        if (e.Key is not null && _activity.Take(6).FirstOrDefault(a =>
                a.Key == e.Key && a.Who == who && a.Kind == e.Kind && (e.Time - a.Time).TotalSeconds < 10) is { } same)
        {
            same.Text = e.Text;
            same.Time = e.Time;
            var index = _activity.IndexOf(same);
            if (index > 0) _activity.Move(index, 0);
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
            Image = e.ImageAsset == "114514" ? SecretImage() : null,
        });
        while (_activity.Count > 300) _activity.RemoveAt(_activity.Count - 1);
    }

    private bool _secretInputLatched;
    private bool _secretImagePending;

    private void CheckSecretInput(string? value)
    {
        var hit = value is "１１４５１４" or "114514";
        if (!hit)
        {
            _secretInputLatched = false;
            return;
        }
        if (_secretInputLatched) return;
        _secretInputLatched = true;
        if (!_session.BroadcastSecretImage()) _secretImagePending = true;
    }

    private static Bitmap? SecretImage()
    {
        if (_secretImage is not null) return _secretImage;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Maltese.App/Assets/114514.png"));
            return _secretImage = new Bitmap(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorLog.Write("SecretImage", e);
            return null;
        }
    }
}

/// <summary>名前と最後に参加したアドレスを覚えておく。</summary>
public sealed class AppSettings
{
    /// <summary>Language selected in the UI. New installs follow the OS language.</summary>
    public AppLanguage Language { get; set; } = Localization.SystemDefault;
    public string? Name { get; set; }
    public string? LastAddress { get; set; }
    public string? LastKey { get; set; }
    /// <summary>受け取ったサンプルの保存先（null なら ドキュメント/Maltese/Samples）</summary>
    public string? SamplesFolder { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maltese", "settings.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AbletonMulti", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
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
