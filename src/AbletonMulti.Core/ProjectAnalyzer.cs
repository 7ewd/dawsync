namespace AbletonMulti.Core;

/// <summary>読み込んだプロジェクトを「共同編集で困ること」の観点でチェックする。</summary>
public static class ProjectAnalyzer
{
    private const int MaxListedItems = 50;

    public static AnalysisResult Analyze(string alsPath, PluginCatalog? catalog = null)
    {
        var project = AlsReader.Read(alsPath);

        if (catalog is not null)
        {
            foreach (var plugin in project.Plugins)
            {
                var match = catalog.Find(plugin);
                plugin.InstallState = match is null ? InstallState.Missing : InstallState.Installed;
                plugin.InstalledPath = match?.Path;
            }
        }

        return new AnalysisResult { Project = project, Issues = CheckIssues(project).OrderByDescending(i => i.Severity).ToList() };
    }

    private static IEnumerable<Issue> CheckIssues(AlsProject p)
    {
        var au = p.Plugins.Where(x => x.Format == PluginFormat.AudioUnit).ToList();
        if (au.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Error,
                Title = $"Mac 専用の AU プラグインが {au.Count} 個使われています",
                Detail = "Audio Unit は Mac でしか動かないため、Windows の人がこのプロジェクトを開くと音が鳴りません。",
                Fix = "同じプラグインの VST3 版に差し替えてください。",
                Items = au.Select(PluginLine).ToList(),
            };

        var missingSamples = p.Samples.Where(s => !s.Exists).ToList();
        if (missingSamples.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Error,
                Title = $"見つからないサンプルが {missingSamples.Count} 個あります",
                Detail = "この PC 上にファイルが存在しません。Live で開くとオフラインのクリップになります。",
                Fix = "Live で開いて「ファイルマネージャ」から場所を指定し直すか、元のファイルを持っている人に共有してもらってください。",
                Items = Limit(missingSamples.Select(s => s.StoredPath)),
            };

        var outsideSamples = p.Samples.Where(s => s.Exists && s.NeedsCollecting).ToList();
        if (outsideSamples.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Error,
                Title = $"プロジェクトフォルダの外にあるサンプルが {outsideSamples.Count} 個あります",
                Detail = "このままプロジェクトフォルダだけを共有すると、相手の PC ではこれらのサンプルが見つかりません。",
                Fix = "Live で「ファイル」→「すべてを収集して保存」を実行すると、プロジェクトフォルダにコピーされます。",
                Items = Limit(outsideSamples.Select(s => s.ResolvedPath)),
            };

        var outsideMax = p.MaxDevices.Where(d => d.NeedsCollecting || !d.Exists).ToList();
        if (outsideMax.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Warning,
                Title = $"Max for Live デバイス {outsideMax.Count} 個がプロジェクト外にあります",
                Detail = "User Library などにある .amxd は相手の PC にありません。",
                Fix = "「すべてを収集して保存」でプロジェクトフォルダにコピーしてください。",
                Items = outsideMax.Select(d => $"{d.FileName}（{string.Join(", ", d.Tracks)}）").ToList(),
            };

        var notInstalled = p.Plugins.Where(x => x.InstallState == InstallState.Missing && x.Format != PluginFormat.AudioUnit).ToList();
        if (notInstalled.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Warning,
                Title = $"この PC に入っていないプラグインが {notInstalled.Count} 個あります",
                Detail = "プラグインの設定はプロジェクトに保存されていますが、本体がないと音が鳴りません。",
                Fix = "同じプラグインをインストールしてください（プラグインはライセンスの都合で共有できません）。",
                Items = notInstalled.Select(PluginLine).ToList(),
            };

        if (p.ExternalRoutingTracks.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Warning,
                Title = "外部 MIDI 機器・ハードウェアを使うトラックがあります",
                Detail = "外部機器の音は他の人の PC では鳴りません。",
                Fix = "オーディオとして録音（リサンプリング）しておくと、他の人も聴けます。",
                Items = p.ExternalRoutingTracks.ToList(),
            };

        var vst2 = p.Plugins.Where(x => x.Format == PluginFormat.Vst2).ToList();
        if (vst2.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Info,
                Title = $"VST2 プラグインが {vst2.Count} 個あります",
                Detail = "VST2 は開発が終了しているため、相手の環境には VST2 版が無いことがあります。",
                Fix = "できれば VST3 版に差し替えるのがおすすめです。",
                Items = vst2.Select(PluginLine).ToList(),
            };

        var packs = p.RequiredPacks.Where(x => x != "Core Library").ToList();
        if (packs.Count > 0)
            yield return new Issue
            {
                Severity = Severity.Info,
                Title = $"Ableton の Pack が {packs.Count} 個必要です",
                Detail = "Pack の音はアップロードしなくても、相手が同じ Pack をインストールしていれば鳴ります。",
                Fix = "共同編集者にも同じ Pack をインストールしてもらってください。",
                Items = packs,
            };

        if (p.LiveVersion.Length > 0)
            yield return new Issue
            {
                Severity = Severity.Info,
                Title = $"Ableton Live {p.LiveVersion} で保存されています",
                Detail = "新しいバージョンの Live で保存したファイルは、古いバージョンでは開けません。",
                Fix = $"共同編集者は全員 Live {p.LiveVersion} 以上を使ってください。",
            };
    }

    private static string PluginLine(PluginUsage x) =>
        $"{x.Name}{(x.Vendor.Length > 0 ? $"（{x.Vendor}）" : "")} — {string.Join(", ", x.Tracks)}";

    private static List<string> Limit(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= MaxListedItems
            ? list
            : [.. list.Take(MaxListedItems), $"ほか {list.Count - MaxListedItems} 件"];
    }
}
