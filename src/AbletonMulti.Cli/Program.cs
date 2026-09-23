using AbletonMulti.Core;

// 使い方: AbletonMulti.Cli <project.als> [--json 出力先.json] [--hash]
if (args.Length == 0)
{
    Console.WriteLine("usage: AbletonMulti.Cli <project.als> [--json out.json] [--hash]");
    return 1;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
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

var jsonIndex = Array.IndexOf(args, "--json");
if (jsonIndex >= 0 && jsonIndex + 1 < args.Length)
{
    var json = await ManifestWriter.BuildJsonAsync(result, hashFiles: args.Contains("--hash"));
    await File.WriteAllTextAsync(args[jsonIndex + 1], json);
    Console.WriteLine($"\nマニフェストを書き出しました: {args[jsonIndex + 1]}");
}
return 0;
