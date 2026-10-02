using System.Globalization;

namespace Maltese.App;

/// <summary>Supported Maltese UI languages.</summary>
public enum AppLanguage
{
    English,
    Japanese,
}

/// <summary>Small, dependency-free UI translation table.</summary>
public static class Localization
{
    public static AppLanguage SystemDefault =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "ja", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Japanese : AppLanguage.English;

    private static readonly IReadOnlyDictionary<string, (string English, string Japanese)> Text =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["language"] = ("Language", "言語"),
            ["daw_step"] = ("① Connect Ableton Live / Bitwig / REAPER", "① Ableton Live / Bitwig / REAPER とつなぐ"),
            ["how_to_open"] = ("How to connect ▾", "つなぎ方を見る ▾"),
            ["how_to_close"] = ("Hide instructions ▴", "つなぎ方を閉じる ▴"),
            ["live_1"] = ("1. Click the button above to install the Live script", "1. 上のボタンで Live にスクリプトを入れる"),
            ["live_2"] = ("2. Start (or restart) Live", "2. Live を起動（起動中なら再起動）"),
            ["live_3"] = ("3. In Live Preferences → Link, Tempo & MIDI → Control Surface, select Maltese", "3. Live の「環境設定」→「Tempo & MIDI」→「コントロールサーフェス」で「Maltese」を選ぶ"),
            ["bitwig_1"] = ("1. Click the button above to install the Bitwig extension", "1. 上のボタンで Bitwig に拡張を入れる"),
            ["bitwig_2"] = ("2. In Settings → Controllers, choose Maltese and add it", "2. Bitwig の「設定」→「コントローラー」で「Maltese」を追加"),
            ["reaper_1"] = ("1. Click the button above to install the REAPER script", "1. 上のボタンで REAPER にスクリプトを入れる"),
            ["reaper_2"] = ("2. Start (or restart) REAPER; it connects automatically", "2. REAPER を起動（起動中なら再起動）。あとは自動でつながります"),
            ["room_step"] = ("② Join a room", "② ルームに入る"),
            ["your_name"] = ("Your name", "あなたの名前"),
            ["name_placeholder"] = ("e.g. Alex", "例: たろう"),
            ["create_room"] = ("Create room", "ルームを作る"),
            ["or"] = ("or", "または"),
            ["invite_placeholder"] = ("Invite code (or address)", "招待コード（またはアドレス）"),
            ["key_placeholder"] = ("Passphrase", "合言葉"),
            ["join"] = ("Join", "参加する"),
            ["publish"] = ("Publish to the internet", "インターネットに公開"),
            ["published_invite"] = ("Invite code (send this to let others join)", "招待コード（これを送るだけで参加できます）"),
            ["copy"] = ("Copy", "コピー"),
            ["unpublish"] = ("Stop publishing", "公開をやめる"),
            ["passphrase"] = ("Passphrase", "合言葉"),
            ["copy_invite"] = ("Copy invite", "招待をコピー"),
            ["leave"] = ("Leave room", "ルームから出る"),
            ["participants"] = ("Participants", "参加者"),
            ["samples"] = ("Sample folder", "サンプルの保存先"),
            ["open"] = ("Open", "開く"),
            ["change"] = ("Change…", "変更…"),
            ["resync"] = ("Sync again", "もう一度合わせる"),
            ["overwrite"] = ("Overwrite this set with the room", "このセットをルームの内容で上書き"),
            ["activity"] = ("Everyone's changes", "みんなの変更"),
            ["clear"] = ("Clear", "消去"),
            ["waiting"] = ("Waiting for Ableton Live, Bitwig, or REAPER…", "Ableton Live・Bitwig・REAPER のどれかを待っています…"),
            ["live_install"] = ("Install the Live script", "Live にスクリプトを入れる"),
            ["live_update"] = ("Update the Live script (then restart Live)", "Live のスクリプトを更新する（そのあと Live を再起動）"),
            ["reaper_install"] = ("Install the REAPER script", "REAPER にスクリプトを入れる"),
            ["reaper_update"] = ("Update the REAPER script (then restart REAPER)", "REAPER のスクリプトを更新する（そのあと REAPER を再起動）"),
            ["bitwig_install"] = ("Install the Bitwig extension", "Bitwig に拡張を入れる"),
            ["bitwig_update"] = ("Update the Bitwig extension", "Bitwig の拡張を更新する"),
            ["disconnected"] = ("Ableton Live / Bitwig / REAPER is not connected", "Ableton Live / Bitwig / REAPER がつながっていません"),
            ["not_in_room"] = ("Not in a room", "ルームに入っていません"),
            ["reconnecting"] = ("Reconnecting to the room…", "ルームにつなぎ直しています…"),
            ["reconnecting_detail"] = ("Changes made while disconnected will be sent when connected", "通信が切れました。この間の変更は、つながったらルームに送ります"),
            ["waiting_host"] = ("Waiting for the host", "ホストを待っています"),
            ["editing"] = ("Editing together", "同時編集中"),
            ["connected"] = ("is connected", "とつながっています"),
            ["waiting_daw"] = (" is waiting", " は待っています"),
            ["notice"] = ("Notice", "お知らせ"),
            ["you"] = ("(you)", "（あなた）"),
            ["room_open"] = ("Room is open", "ルームを開いています"),
            ["joined"] = ("Joined: ", "参加中: "),
            ["host_start_title"] = ("How should this room start?", "ルームの始め方"),
            ["start_blank"] = ("Start with a blank set", "まっさらから始める"),
            ["start_blank_detail"] = ("Remove tracks, clips, and returns from the current set and start with one MIDI track. Save important work first, then open a new set ({0}).", "今開いているセットのトラック・クリップ・リターンなどを消して、トラック 1 本だけの状態から始めます。大事なセットを開いている場合は、先に保存してから新規セット（{0}）を開いてください。"),
            ["start_current"] = ("Continue with the current set", "今のセットの続きから始める"),
            ["start_current_detail"] = ("Use the current tracks, clips, notes, and samples as the room content. Audio sources and effects are not synchronized.", "今開いているセットの内容（トラック・クリップ・ノート・サンプル）をそのままルームに入れます。参加する人のセットはこの内容になります（音源・エフェクトは同期されません）。"),
            ["cancel"] = ("Cancel", "やめる"),
            ["blank_confirm_title"] = ("Start with a blank set", "セットをまっさらにします"),
            ["blank_confirm_detail"] = ("The current set in Ableton Live, Bitwig, or REAPER will be cleared and replaced with one MIDI track. Save important work first, then open a new set ({0}).", "Live・Bitwig・REAPER で今開いているセット（プロジェクト）のトラック・クリップ・リターンなどを消して、トラック 1 本だけの状態にしてから始めます。大事なセットを開いている場合は、先に保存してから新規セット（{0}）を開いてください。"),
            ["start_blank_action"] = ("Start blank", "まっさらにして始める"),
        };

    public static string Get(string key, AppLanguage language) =>
        Text.TryGetValue(key, out var value)
            ? language == AppLanguage.Japanese ? value.Japanese : value.English
            : key;

    public static string Count(string singular, string japanese, int count, AppLanguage language) =>
        language == AppLanguage.Japanese ? $"{japanese}（{count} 人）" : $"{count} {singular}{(count == 1 ? "" : "s")}";
}
