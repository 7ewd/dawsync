using AbletonMulti.Core;
using AbletonMulti.Core.Sync;

// 使い方:
//   AbletonMulti.Cli <project.als> [--json out.json] [--hash]        プロジェクトのチェック
//   AbletonMulti.Cli session --host [--name A] [--bridge-port 47400] [--port 47401] [--samples dir]
//   AbletonMulti.Cli session --host --publish ...                  インターネットに公開（招待コードが出る）
//   AbletonMulti.Cli session --join 192.168.1.10:47401 --key 合言葉 [--name B] [--bridge-port 47400]
//   AbletonMulti.Cli session --join xxx.trycloudflare.com#合言葉 ...
//   AbletonMulti.Cli server [--port 47401]                          中継サーバーだけを動かす
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("usage: AbletonMulti.Cli <project.als> [--json out.json] [--hash]");
    Console.WriteLine("       AbletonMulti.Cli session (--host | --join host:port) [--name N] [--bridge-port P] [--port P]");
    Console.WriteLine("       AbletonMulti.Cli server [--port P]");
    return 1;
}

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
int IntOption(string name, int fallback) => int.TryParse(Option(name), out var v) ? v : fallback;

switch (args[0])
{
    case "session":
        return await RunSessionAsync();
    case "server":
        await using (var server = new RoomServer())
        {
            server.Start(IntOption("--port", RoomServer.DefaultPort));
            Console.WriteLine($"中継サーバーを起動しました: port {server.Port}（Ctrl+C で終了）");
            await WaitForCtrlC();
        }
        return 0;
    default:
        return await CheckAsync();
}

async Task<int> RunSessionAsync()
{
    // --samples: 受け取ったサンプルの置き場所（テストで 1 台に複数立ち上げるとき用）
    await using var session = Option("--samples") is { } samples
        ? new SessionController(new FileStore(samples), Path.GetDirectoryName(Path.GetFullPath(samples)))
        : new SessionController();
    session.Activity += e => Console.WriteLine($"[{e.Time:HH:mm:ss}] {e.Kind,-7} {e.Who} {e.Text}");
    session.Changed += () => Console.WriteLine($"          (Live: {(session.Bridge.IsConnected ? "接続" : "未接続")}, 参加者: {string.Join(", ", session.Peers.Select(p => p.Name))})");
    session.StartBridge(IntOption("--bridge-port", LiveBridge.DefaultPort));
    if (args.Contains("--overwrite"))
    {
        // テスト用: 「このセットをルームの内容で上書き」を自動で押す
        var overwritten = false;
        session.Changed += () =>
        {
            if (!session.CanOverwrite || overwritten) return;
            overwritten = true;
            _ = session.SyncWithLiveAsync(overwrite: true);
        };
    }

    var name = Option("--name") ?? Environment.UserName;
    if (args.Contains("--host"))
    {
        await session.HostAsync(name, IntOption("--port", RoomServer.DefaultPort), Option("--key"), startBlank: args.Contains("--blank"));
        Console.WriteLine($"ホスト中: {string.Join(", ", RoomServer.LocalAddresses().Select(a => $"{a}:{session.HostPort}"))}  合言葉: {session.RoomKey}");
        if (args.Contains("--publish"))
        {
            await session.PublishAsync();
            Console.WriteLine($"インターネットに公開: 招待コード {session.InviteCode}");
        }
    }
    else if (Option("--join") is { } target)
    {
        // 招待コード（アドレス#合言葉）でも、アドレスと --key でもよい
        var parts = target.Split('#');
        await session.JoinAsync(parts[0], name, parts.Length > 1 ? parts[1] : Option("--key"), startBlank: args.Contains("--blank"));
    }
    else
    {
        Console.WriteLine("--host か --join を指定してください");
        return 1;
    }
    await WaitForCtrlC();
    return 0;
}

static Task WaitForCtrlC()
{
    var tcs = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
    return tcs.Task;
}

async Task<int> CheckAsync()
{
    var catalog = PluginScanner.Scan();
    var result = ProjectAnalyzer.Analyze(args[0], catalog);
    var p = result.Project;

    Console.WriteLine($"{p.Name}  (Live {p.LiveVersion}, {p.Tempo} BPM)");
    Console.WriteLine($"トラック {p.Tracks.Count} / プラグイン {p.Plugins.Count} / サンプル {p.Samples.Count} / M4L {p.MaxDevices.Count}");
    Console.WriteLine($"スキャンしたプラグイン: {catalog.Plugins.Count} 個");
    Console.WriteLine();

    foreach (var t in p.Tracks)
        Console.WriteLine($"{new string(' ', t.Depth * 2)}[{t.Kind}] {t.Name}  clips={t.ClipCount} notes={t.MidiNoteCount}  {string.Join(" > ", t.Devices)}");
    Console.WriteLine();

    foreach (var x in p.Plugins)
        Console.WriteLine($"{x.Format,-9} {x.Name} ({x.Vendor}) x{x.InstanceCount}  {x.InstallState}  {x.InstalledPath}");
    Console.WriteLine();

    foreach (var g in p.Samples.GroupBy(s => (s.Location, s.Exists)))
        Console.WriteLine($"サンプル {g.Key.Location} exists={g.Key.Exists}: {g.Count()} 個");
    foreach (var d in p.MaxDevices)
        Console.WriteLine($"M4L {d.FileName} {d.Location} exists={d.Exists}");
    Console.WriteLine();

    foreach (var i in result.Issues)
        Console.WriteLine($"[{i.Severity}] {i.Title} ({i.Items.Count})");

    if (Option("--json") is { } jsonPath)
    {
        var json = await ManifestWriter.BuildJsonAsync(result, hashFiles: args.Contains("--hash"));
        await File.WriteAllTextAsync(jsonPath, json);
        Console.WriteLine($"\nマニフェストを書き出しました: {jsonPath}");
    }
    return 0;
}
