using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AbletonMulti.Core;

/// <summary>
/// .als（gzip 圧縮された XML）を読み、トラック・プラグイン・サンプル等の依存関係を取り出す。
/// </summary>
public static partial class AlsReader
{
    // FileRef の RelativePathType（Ableton 非公開。実ファイルから確認した値）
    private const int RelTypeProject = 3;
    private const int RelTypeCoreLibrary = 5;
    private const int RelTypeUserLibrary = 6;
    private const int RelTypeBuiltin = 7;

    private static readonly Dictionary<string, string> FriendlyDeviceNames = new()
    {
        ["OriginalSimpler"] = "Simpler",
        ["MultiSampler"] = "Sampler",
        ["InstrumentGroupDevice"] = "Instrument Rack",
        ["AudioEffectGroupDevice"] = "Audio Effect Rack",
        ["MidiEffectGroupDevice"] = "MIDI Effect Rack",
        ["DrumGroupDevice"] = "Drum Rack",
        ["Eq8"] = "EQ Eight",
        ["Compressor2"] = "Compressor",
        ["StereoGain"] = "Utility",
        ["GlueCompressor"] = "Glue Compressor",
        ["MultibandDynamics"] = "Multiband Dynamics",
        ["AutoFilter"] = "Auto Filter",
        ["ExternalInstrument"] = "External Instrument",
        ["ExternalAudioEffect"] = "External Audio Effect",
    };

    /// <summary>
    /// .als の XML を読む。壊れたファイル・.als でないファイルは InvalidDataException（日本語の説明つき）にする。
    /// ファイルが無い・読めないときの IOException / UnauthorizedAccessException はそのまま投げる。
    /// </summary>
    public static XDocument LoadXml(string alsPath)
    {
        using var file = File.OpenRead(alsPath);
        // 普通は gzip 圧縮されているが、圧縮していない XML のままのものも読めるようにする
        var head = new byte[2];
        var read = file.ReadAtLeast(head, 2, throwOnEndOfStream: false);
        if (read == 0) throw new InvalidDataException("空のファイルです。");
        file.Position = 0;
        try
        {
            if (read == 2 && head[0] == 0x1F && head[1] == 0x8B)
            {
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                return XDocument.Load(gzip);
            }
            return XDocument.Load(file);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException("圧縮された .als ファイルとして読めませんでした（壊れているか、.als ファイルではありません）。", e);
        }
        catch (System.Xml.XmlException e)
        {
            throw new InvalidDataException($"Ableton Live のセットファイルとして読めませんでした（中身が壊れているか、.als ファイルではありません。{e.LineNumber} 行目）。", e);
        }
    }

    public static AlsProject Read(string alsPath)
    {
        var doc = LoadXml(alsPath);
        var root = doc.Root ?? throw new InvalidDataException("空の .als ファイルです。");
        if (root.Name != "Ableton")
            throw new InvalidDataException("Ableton Live のセットファイルではありません。");
        var liveSet = root.Element("LiveSet") ?? throw new InvalidDataException("LiveSet が見つかりません。");

        var creator = (string?)root.Attribute("Creator") ?? "";
        var project = new AlsProject
        {
            FilePath = Path.GetFullPath(alsPath),
            Creator = creator,
            LiveVersion = VersionRegex().Match(creator) is { Success: true } m ? m.Value : "",
            Tempo = ReadTempo(liveSet),
        };

        var trackElements = (liveSet.Element("Tracks")?.Elements() ?? [])
            .Append(liveSet.Element("MainTrack") ?? liveSet.Element("MasterTrack"))
            .OfType<XElement>();

        var plugins = new Dictionary<string, PluginUsage>();
        var samples = new Dictionary<string, FileDependency>(StringComparer.OrdinalIgnoreCase);
        var maxDevices = new Dictionary<string, FileDependency>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in trackElements)
        {
            var track = ReadTrack(t);
            project.Tracks.Add(track);

            foreach (var device in t.Descendants().Where(e => e.Name == "PluginDevice" || e.Name == "AuPluginDevice"))
            {
                if (ReadPlugin(device) is not { } p) continue;
                var usage = plugins.TryGetValue(p.Key, out var existing) ? existing : plugins[p.Key] = p;
                usage.InstanceCount++;
                usage.Tracks.Add(track.Name);
                if (usage.Vendor.Length == 0) usage.Vendor = p.Vendor;
            }

            foreach (var fileRef in t.Descendants("SampleRef").Select(s => s.Element("FileRef")).OfType<XElement>())
                AddDependency(samples, fileRef, project.ProjectDirectory, track.Name);

            foreach (var fileRef in t.Descendants("MxPatchRef").Select(s => s.Element("FileRef")).OfType<XElement>())
                AddDependency(maxDevices, fileRef, project.ProjectDirectory, track.Name);

            var midiOut = V(t.Element("DeviceChain")?.Element("MidiOutputRouting"), "Target") ?? "";
            var hasExternalDevice = t.Descendants().Any(e => e.Name == "ExternalInstrument" || e.Name == "ExternalAudioEffect");
            if (midiOut.StartsWith("MidiOut/External", StringComparison.Ordinal) || hasExternalDevice)
                project.ExternalRoutingTracks.Add(track.Name);
        }

        ComputeDepths(project.Tracks);
        project.Plugins.AddRange(plugins.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase));
        project.Samples.AddRange(samples.Values.OrderBy(s => s.Location).ThenBy(s => s.FileName, StringComparer.OrdinalIgnoreCase));
        project.MaxDevices.AddRange(maxDevices.Values.OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase));
        return project;
    }

    private static double? ReadTempo(XElement liveSet)
    {
        var main = liveSet.Element("MainTrack") ?? liveSet.Element("MasterTrack");
        var value = V(main?.Element("DeviceChain")?.Element("Mixer")?.Element("Tempo"), "Manual");
        return double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var bpm) ? bpm : null;
    }

    private static TrackInfo ReadTrack(XElement t)
    {
        var kind = t.Name.LocalName switch
        {
            "AudioTrack" => TrackKind.Audio,
            "MidiTrack" => TrackKind.Midi,
            "GroupTrack" => TrackKind.Group,
            "ReturnTrack" => TrackKind.Return,
            _ => TrackKind.Main,
        };
        var nameEl = t.Element("Name");
        var userName = V(nameEl, "UserName") ?? "";
        var name = userName.Length > 0 ? userName : V(nameEl, "EffectiveName") ?? "";
        if (kind == TrackKind.Main && name.Length == 0) name = "Main";

        var track = new TrackInfo
        {
            Id = int.TryParse((string?)t.Attribute("Id"), out var id) ? id : -1,
            Name = name,
            Kind = kind,
            GroupId = int.TryParse(V(t, "TrackGroupId"), out var gid) ? gid : -1,
            ClipCount = t.Descendants().Count(e => e.Name == "MidiClip" || e.Name == "AudioClip"),
            MidiNoteCount = t.Descendants("MidiNoteEvent").Count(),
        };

        var devices = t.Element("DeviceChain")?.Element("DeviceChain")?.Element("Devices")
                      ?? t.Descendants("Devices").FirstOrDefault();
        foreach (var d in devices?.Elements() ?? [])
            track.Devices.Add(DeviceDisplayName(d));
        return track;
    }

    private static string DeviceDisplayName(XElement device)
    {
        var userName = V(device, "UserName");
        if (!string.IsNullOrEmpty(userName)) return userName;

        if (device.Name == "PluginDevice" || device.Name == "AuPluginDevice")
            return ReadPlugin(device)?.Name ?? "Plugin";

        if (device.Name.LocalName.StartsWith("MxDevice", StringComparison.Ordinal))
        {
            var path = V(device.Descendants("MxPatchRef").Elements("FileRef").FirstOrDefault(), "Path");
            return path is { Length: > 0 } ? Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]) : "Max for Live";
        }

        var tag = device.Name.LocalName;
        return FriendlyDeviceNames.TryGetValue(tag, out var friendly) ? friendly : tag;
    }

    private static PluginUsage? ReadPlugin(XElement device)
    {
        var desc = device.Element("PluginDesc");
        if (desc is null) return null;
        var vendor = VendorFromBrowserPath(device);

        if (desc.Element("Vst3PluginInfo") is { } vst3)
        {
            var fields = vst3.Element("Uid")?.Elements()
                .Select(f => int.TryParse((string?)f.Attribute("Value"), out var n) ? (uint)n : 0u)
                .ToArray() ?? [];
            return new PluginUsage
            {
                Format = PluginFormat.Vst3,
                Name = V(vst3, "Name") ?? "VST3",
                Uid = fields.Length == 4 ? string.Concat(fields.Select(f => f.ToString("X8"))) : "",
                Vendor = vendor,
                IsInstrument = V(vst3, "DeviceType") == "1",
            };
        }

        if (desc.Element("VstPluginInfo") is { } vst2)
        {
            var name = V(vst2, "PlugName") ?? "";
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(V(vst2, "Path") ?? "VST");
            return new PluginUsage
            {
                Format = PluginFormat.Vst2,
                Name = name,
                Uid = V(vst2, "UniqueId") ?? "",
                Vendor = vendor,
                IsInstrument = V(vst2, "Category") == "2",
            };
        }

        if (desc.Element("AuPluginInfo") is { } au)
        {
            return new PluginUsage
            {
                Format = PluginFormat.AudioUnit,
                Name = V(au, "Name") ?? "Audio Unit",
                Uid = $"{V(au, "ComponentManufacturer")}:{V(au, "ComponentSubType")}",
                Vendor = V(au, "Manufacturer") is { Length: > 0 } m ? m : vendor,
                IsInstrument = V(au, "ComponentType") == "1635085685", // 'aumu'
            };
        }

        return null;
    }

    /// <summary>"query:Plugins#VST3:Xfer%20Records:Serum%202" → "Xfer Records"</summary>
    private static string VendorFromBrowserPath(XElement device)
    {
        var path = V(device.Element("SourceContext")?.Descendants("BrowserContentPath").FirstOrDefault(), null) ?? "";
        var hash = path.IndexOf('#');
        if (!path.StartsWith("query:Plugins", StringComparison.Ordinal) || hash < 0) return "";
        var parts = path[(hash + 1)..].Split(':');
        return parts.Length >= 3 ? Uri.UnescapeDataString(parts[1]) : "";
    }

    private static void AddDependency(Dictionary<string, FileDependency> into, XElement fileRef, string projectDir, string trackName)
    {
        var stored = V(fileRef, "Path") ?? "";
        var relative = V(fileRef, "RelativePath") ?? "";
        if (stored.Length == 0 && relative.Length == 0) return;

        var relType = int.TryParse(V(fileRef, "RelativePathType"), out var rt) ? rt : 0;
        var pack = V(fileRef, "LivePackName") ?? "";

        // この PC で実在する場所を探す。プロジェクト相対 → 保存時の絶対パス → 相対パスの順。
        var candidates = new List<string>();
        if (relType == RelTypeProject && relative.Length > 0) candidates.Add(Path.Combine(projectDir, relative));
        if (stored.Length > 0) candidates.Add(stored);
        if (relative.Length > 0 && relType != RelTypeProject) candidates.Add(Path.Combine(projectDir, relative));
        var existing = candidates.FirstOrDefault(File.Exists);
        // 別 OS のパス（Mac で "E:/..." 等）は GetFullPath すると壊れるので、見つからなければそのまま
        var resolved = existing is not null ? Path.GetFullPath(existing) : candidates[0];

        var location =
            pack.Length > 0 || relType is RelTypeCoreLibrary or RelTypeBuiltin ? FileLocation.AbletonLibrary
            : Path.IsPathFullyQualified(resolved) && IsUnder(resolved, projectDir) ? FileLocation.Project
            : relType == RelTypeUserLibrary ? FileLocation.UserLibrary
            : FileLocation.External;

        if (!into.TryGetValue(resolved, out var dep))
        {
            dep = new FileDependency
            {
                StoredPath = stored,
                RelativePath = relative,
                RelativePathType = relType,
                LivePackName = pack,
                OriginalFileSize = long.TryParse(V(fileRef, "OriginalFileSize"), out var size) ? size : 0,
                ResolvedPath = resolved,
                Exists = existing is not null,
                Location = location,
            };
            into[resolved] = dep;
        }
        dep.UseCount++;
        dep.Tracks.Add(trackName);
    }

    private static bool IsUnder(string path, string dir)
    {
        try
        {
            var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // .als に書かれたパスがこの OS では使えない形のとき
            return false;
        }
    }

    private static void ComputeDepths(List<TrackInfo> tracks)
    {
        // Id が重複・欠けている（-1）ファイルもあるので、最初の 1 つだけを使う
        var byId = new Dictionary<int, TrackInfo>();
        foreach (var t in tracks.Where(t => t.Kind != TrackKind.Main && t.Id >= 0))
            byId.TryAdd(t.Id, t);
        foreach (var t in tracks)
        {
            var depth = 0;
            for (var g = t.GroupId; g >= 0 && byId.TryGetValue(g, out var parent) && depth < 32; g = parent.GroupId)
                depth++;
            t.Depth = depth;
        }
    }

    /// <summary>子要素 child の Value 属性（child が null なら e 自身の Value）。</summary>
    private static string? V(XElement? e, string? child) =>
        (string?)(child is null ? e : e?.Element(child))?.Attribute("Value");

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionRegex();
}
