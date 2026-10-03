"""Ableton Live の Live Object Model を、Remote Script のテストに必要な分だけまねたもの。"""
import os


class Listenable:
    """add_x_listener / remove_x_listener / x_has_listener と、値の変更通知を提供する。"""

    _observed = ()

    def __init__(self):
        object.__setattr__(self, "_listeners", {})

    def __getattr__(self, name):
        if name.startswith("add_") and name.endswith("_listener"):
            prop = name[4:-9]
            return lambda cb: self._listeners.setdefault(prop, []).append(cb)
        if name.startswith("remove_") and name.endswith("_listener"):
            prop = name[7:-9]
            return lambda cb: self._listeners[prop].remove(cb)
        if name.endswith("_has_listener"):
            prop = name[:-13]
            return lambda cb: cb in self._listeners.get(prop, [])
        raise AttributeError(name)

    def __setattr__(self, name, value):
        changed = getattr(self, name, None) != value
        object.__setattr__(self, name, value)
        if name in self._observed and changed:
            self._notify(name)

    def _notify(self, prop):
        for cb in list(self._listeners.get(prop, [])):
            cb()

    def listener_count(self):
        return sum(len(v) for v in self._listeners.values())


class DeviceParameter(Listenable):
    _observed = ("value",)

    def __init__(self, name="Param", value=0.0, min=0.0, max=1.0):
        Listenable.__init__(self)
        self.name = name
        self.min = min
        self.max = max
        self.value = value
        self.automation_state = 0  # AutomationState: 0 無い / 1 再生中 / 2 手で上書き


class MixerDevice:
    def __init__(self, sends=0):
        self.volume = DeviceParameter("Volume", 0.85)
        self.panning = DeviceParameter("Pan", 0.0, -1.0, 1.0)
        self.sends = [DeviceParameter("Send", 0.0) for _ in range(sends)]


# ------------------------------------------------------------------ devices

NATIVE_DEVICES = {
    # 表示名: (class_name, パラメータ名)
    "EQ Eight": ("Eq8", ["Device On", "1 Frequency A", "1 Gain A"]),
    "Reverb": ("Reverb", ["Device On", "Decay Time", "Dry/Wet"]),
    "Compressor": ("Compressor2", ["Device On", "Threshold", "Ratio"]),
    "Utility": ("StereoGain", ["Device On", "Gain", "Width"]),
    "Simpler": ("OriginalSimpler", ["Device On", "Volume", "Transpose"]),
    "Audio Effect Rack": ("AudioEffectGroupDevice", ["Device On", "Macro 1", "Macro 2"]),
}


class Device(Listenable):
    _observed = ("selected_preset_index", "sample")

    def __init__(self, class_name, display_name, param_names, can_have_chains=False):
        Listenable.__init__(self)
        self.class_name = class_name
        self.class_display_name = display_name
        self.name = display_name
        self.parameters = [DeviceParameter(n, 1.0 if n == "Device On" else 0.5) for n in param_names]
        self.can_have_chains = can_have_chains
        self.chains = [Chain("Chain 1")] if can_have_chains else []
        if class_name in ("PluginDevice", "AuPluginDevice"):
            self.presets = ["Init", "Preset A", "Preset B"]
            self.selected_preset_index = 0
        if class_name == "OriginalSimpler":
            self.sample = None

    def replace_sample(self, path):
        if not os.path.exists(path):
            raise RuntimeError("no such file: " + path)
        self.sample = Sample(path)


class Sample:
    def __init__(self, path):
        self.file_path = path


def make_native(display_name):
    class_name, params = NATIVE_DEVICES[display_name]
    return Device(class_name, display_name, params, can_have_chains=class_name.endswith("GroupDevice"))


def make_plugin(name):
    return Device("PluginDevice", name, ["Device On"] + ["%s Param %d" % (name, i) for i in range(1, 6)])


class DeviceContainer(Listenable):
    _observed = ("devices", "name", "mute")

    def __init__(self):
        Listenable.__init__(self)
        self.devices = []

    def insert_device(self, name, index=-1):
        if name not in NATIVE_DEVICES:
            raise RuntimeError("unknown device " + name)
        devices = list(self.devices)
        devices.insert(len(devices) if index < 0 else index, make_native(name))
        self.devices = devices

    def delete_device(self, index):
        devices = list(self.devices)
        del devices[index]
        self.devices = devices

    # テスト用: ユーザーがデバイスを追加した
    def user_add(self, device, index=None):
        devices = list(self.devices)
        devices.insert(len(devices) if index is None else index, device)
        self.devices = devices
        return device


class Chain(DeviceContainer):
    def __init__(self, name):
        DeviceContainer.__init__(self)
        self.name = name
        self.mute = False
        self.mixer_device = MixerDevice(0)


# -------------------------------------------------------------------- clips

class MidiNote:
    _next_id = 1

    def __init__(self, pitch, start_time, duration, velocity, mute=False,
                 probability=1.0, velocity_deviation=0.0, release_velocity=64.0):
        self.pitch = pitch
        self.start_time = start_time
        self.duration = duration
        self.velocity = velocity
        self.mute = mute
        self.probability = probability
        self.velocity_deviation = velocity_deviation
        self.release_velocity = release_velocity
        self.mpe = None  # MPE（ノートごとの表現）の代わり。API からは読めない
        self.note_id = MidiNote._next_id
        MidiNote._next_id += 1

    def copy(self):
        c = MidiNote.__new__(MidiNote)
        c.__dict__.update(self.__dict__)
        return c


class Clip(Listenable):
    _observed = ("name", "color", "looping", "loop_start", "loop_end", "start_marker", "end_marker",
                 "gain", "pitch_coarse", "pitch_fine", "warping", "warp_mode", "start_time", "end_time")

    def __init__(self, length=4.0, start_time=0.0, notes=(), file_path=None, arrangement=True):
        Listenable.__init__(self)
        self.is_arrangement_clip = arrangement
        self.end_time = start_time + length
        self.is_audio_clip = file_path is not None
        self.is_midi_clip = not self.is_audio_clip
        self.file_path = file_path
        self.start_time = start_time
        self.name = ""
        self.color = 0
        self.looping = True
        self.loop_start = 0.0
        self.loop_end = length
        self.start_marker = 0.0
        self.end_marker = length
        if self.is_audio_clip:
            self.gain = 0.4
            self.pitch_coarse = 0
            self.pitch_fine = 0.0
            self.warping = True
            self.warp_mode = 0
            self.warp_markers = [WarpMarker(sample_time=0.0, beat_time=0.0), WarpMarker(sample_time=length / 2.0, beat_time=length)]
        self._notes = [MidiNote(*n) for n in notes]
        self._selected = set()   # 選択中のノートの id

    @property
    def length(self):
        # 本物と同じく、ループ中はループの長さ、そうでなければマーカー間の長さ
        return (self.loop_end - self.loop_start) if self.looping else (self.end_marker - self.start_marker)

    def __setattr__(self, name, value):
        if name == "loop_start" and hasattr(self, "loop_end") and value >= self.loop_end:
            raise RuntimeError("loop_start must be < loop_end")
        if name == "loop_end" and hasattr(self, "loop_start") and value <= self.loop_start:
            raise RuntimeError("loop_end must be > loop_start")
        Listenable.__setattr__(self, name, value)

    def _range(self, from_pitch, pitch_span, from_time, time_span):
        return [n for n in self._notes
                if from_pitch <= n.pitch < from_pitch + pitch_span and from_time <= n.start_time < from_time + time_span]

    def get_notes_extended(self, from_pitch, pitch_span, from_time, time_span):
        # 本物と同じく、返すのはコピー（apply_note_modifications するまで反映されない）
        return [n.copy() for n in self._range(from_pitch, pitch_span, from_time, time_span)]

    def remove_notes_extended(self, from_pitch, pitch_span, from_time, time_span):
        before = len(self._notes)
        doomed = self._range(from_pitch, pitch_span, from_time, time_span)
        self._notes = [n for n in self._notes if n not in doomed]
        if len(self._notes) != before:
            self._notify("notes")

    def get_selected_notes_extended(self, from_pitch, pitch_span, from_time, time_span):
        return [n.copy() for n in self._range(from_pitch, pitch_span, from_time, time_span) if n.note_id in self._selected]

    def select_notes_by_id(self, ids):
        self._selected = set(ids)

    def deselect_all_notes(self):
        self._selected = set()

    def remove_notes_by_id(self, ids):
        self._notes = [n for n in self._notes if n.note_id not in ids]
        self._notify("notes")

    def apply_note_modifications(self, notes):
        by_id = dict((n.note_id, n) for n in self._notes)
        for m in notes:
            if m.note_id not in by_id:
                raise RuntimeError("note not in clip")
            target = by_id[m.note_id]
            for attr in ("pitch", "start_time", "duration", "velocity", "mute",
                         "probability", "velocity_deviation", "release_velocity"):
                setattr(target, attr, getattr(m, attr))
        self._notify("notes")

    def add_new_notes(self, specs):
        for s in specs:
            self._drop_same(s.pitch, s.start_time)
            self._notes.append(MidiNote(s.pitch, s.start_time, s.duration, s.velocity, s.mute,
                                        s.probability, s.velocity_deviation, s.release_velocity))
        self._notify("notes")

    def _drop_same(self, pitch, start):
        # 本物と同じく、同じ音程・同じ位置のノートは 2 つ置けない（新しい方で置き換わる）
        self._notes = [n for n in self._notes if not (n.pitch == pitch and n.start_time == start)]

    def add_warp_marker(self, marker):
        if isinstance(marker, dict):
            marker = WarpMarker(marker.get("sample_time", 0.0), marker.get("beat_time", 0.0))
        if any(m.beat_time == marker.beat_time for m in self.warp_markers):
            raise RuntimeError("marker already exists")
        self.warp_markers = sorted(self.warp_markers + [marker], key=lambda m: m.beat_time)
        self._notify("warp_markers")

    def remove_warp_marker(self, beat_time):
        self.warp_markers = [m for m in self.warp_markers if m.beat_time != beat_time]
        self._notify("warp_markers")

    # テスト用: ユーザーがノートを 1 つ置いた
    def user_add_note(self, pitch, start, duration=0.5, velocity=100):
        self._drop_same(pitch, start)
        self._notes.append(MidiNote(pitch, start, duration, velocity))
        self._notify("notes")


class WarpMarker:
    def __init__(self, sample_time=0.0, beat_time=0.0):
        self.sample_time = sample_time
        self.beat_time = beat_time


class _Spec:
    def __init__(self, pitch, start_time, duration, velocity=100, mute=False,
                 probability=1.0, velocity_deviation=0.0, release_velocity=64.0):
        self.pitch = pitch
        self.start_time = start_time
        self.duration = duration
        self.velocity = velocity
        self.mute = mute
        self.probability = probability
        self.velocity_deviation = velocity_deviation
        self.release_velocity = release_velocity


Clip.MidiNoteSpecification = _Spec
Clip.WarpMarker = WarpMarker  # Live.Clip.MidiNoteSpecification として使えるように


class ClipSlot(Listenable):
    _observed = ("has_clip",)

    def __init__(self):
        Listenable.__init__(self)
        self.clip = None
        self.has_clip = False

    def create_clip(self, length):
        if self.has_clip:
            raise RuntimeError("slot already has a clip")
        self.clip = Clip(length, arrangement=False)
        self.has_clip = True

    def create_audio_clip(self, path):
        if not os.path.exists(path):
            raise RuntimeError("no such file: " + path)
        self.clip = Clip(4.0, file_path=path, arrangement=False)
        self.has_clip = True

    def delete_clip(self):
        self.clip = None
        self.has_clip = False


# ------------------------------------------------------------------- tracks

class Track(DeviceContainer):
    _observed = ("name", "color", "mute", "solo", "arrangement_clips", "devices")

    def __init__(self, name, midi=True, slots=4, sends=1, group=False):
        DeviceContainer.__init__(self)
        self.is_grouped = False
        self.group_track = None
        self.name = name
        self.color = 0xFF8800
        self.mute = False
        self.solo = False
        self.has_midi_input = midi and not group
        self.is_foldable = group
        self.mixer_device = MixerDevice(sends)
        self.clip_slots = [ClipSlot() for _ in range(slots)]
        self.arrangement_clips = []

    def create_midi_clip(self, start_time, length):
        return self._place(Clip(length, start_time))

    def create_audio_clip(self, path, start_time):
        if not os.path.exists(path):
            raise RuntimeError("no such file: " + path)
        return self._place(Clip(4.0, start_time, file_path=path))

    def _place(self, clip):
        """本物の Live と同じく、重なった既存のクリップは切り詰め／分割される（切れ端は新しいクリップになる）。"""
        s, e = clip.start_time, clip.end_time
        result = []
        for c in self.arrangement_clips:
            if c.end_time <= s or c.start_time >= e:
                result.append(c)
                continue
            if c.start_time < s:
                result.append(_piece(c, c.start_time, s))
            if c.end_time > e:
                result.append(_piece(c, e, c.end_time))
        self.arrangement_clips = sorted(result + [clip], key=lambda c: c.start_time)
        return clip

    def delete_clip(self, clip):
        self.arrangement_clips = [c for c in self.arrangement_clips if c is not clip]

    # テスト用: ユーザーがクリップを分割した（Ctrl+E。前と後ろの 2 つになり、中身の位置はそのまま）
    def user_split(self, clip, at):
        rest = [c for c in self.arrangement_clips if c is not clip]
        self.arrangement_clips = sorted(rest + [_piece(clip, clip.start_time, at), _piece(clip, at, clip.end_time)],
                                        key=lambda c: c.start_time)

    def duplicate_clip_to_arrangement(self, clip, destination_time):
        """本物と同じく、クリップを丸ごと（ノートの MPE なども）複製する。"""
        copy = Clip(clip.end_time - clip.start_time, destination_time, file_path=clip.file_path)
        copy._notes = [n.copy() for n in clip._notes]
        for attr in ("name", "color", "looping", "loop_start", "loop_end", "start_marker", "end_marker"):
            object.__setattr__(copy, attr, getattr(clip, attr))
        if clip.is_audio_clip:
            for attr in ("gain", "pitch_coarse", "pitch_fine", "warping", "warp_mode"):
                object.__setattr__(copy, attr, getattr(clip, attr))
            copy.warp_markers = list(clip.warp_markers)
        return self._place(copy)


Track.Track = Track  # Live.Track.Track として使えるように


def _piece(clip, start, end):
    """本物と同じく、切れ端は中身の位置（開始位置・ループ）をそのまま引き継ぐ。"""
    piece = Clip(end - start, start, file_path=clip.file_path)
    piece._notes = [n.copy() for n in clip._notes]
    piece.name = clip.name
    offset = start - clip.start_time
    for attr in ("looping", "loop_start", "loop_end"):
        object.__setattr__(piece, attr, getattr(clip, attr))
    object.__setattr__(piece, "start_marker", clip.start_marker + offset)
    object.__setattr__(piece, "end_marker", clip.start_marker + offset + (end - start))
    return piece


class Scene(Listenable):
    _observed = ("name", "color")

    def __init__(self, name=""):
        Listenable.__init__(self)
        self.name = name
        self.color = 0


class CuePoint(Listenable):
    """Arrangement locator exposed by Live's Song.cue_points list."""

    _observed = ("name", "time")

    def __init__(self, time=0.0, name=""):
        Listenable.__init__(self)
        self.time = float(time)
        self.name = str(name)


class SongView:
    def __init__(self):
        self.selected_track = None
        self.detail_clip = None


class Song(Listenable):
    _observed = ("tempo", "signature_numerator", "signature_denominator", "tracks", "return_tracks", "scenes", "cue_points")

    def __init__(self, scenes=4):
        Listenable.__init__(self)
        self.tempo = 120.0
        self.signature_numerator = 4
        self.signature_denominator = 4
        self._song_time = 0.0
        self._pending_time = None
        self._in_script = False
        # Real Live exposes this transport state and the Remote Script uses it
        # to apply received tempo/signature map rows only during playback.
        self.is_playing = False
        self.cue_points = []
        self.tracks = []
        self.return_tracks = []
        self.scenes = [Scene() for _ in range(scenes)]
        self.master_track = Track("Master", midi=False, slots=0, sends=0)
        self.master_track.mixer_device.song_tempo = DeviceParameter("Song Tempo", 120.0, 20.0, 999.0)
        self.file_path = None  # 保存した .als の場所（保存していなければ None）
        self.view = SongView()

    # 本物の Live では、スクリプトが current_song_time を変えても、読める（ロケーターを置ける）のは次の tick から。
    # テストのコード（人の操作のつもり）から変えたときはすぐ変わる
    @property
    def current_song_time(self):
        return self._song_time

    @current_song_time.setter
    def current_song_time(self, value):
        if self._in_script:
            self._pending_time = float(value)
        else:
            self._song_time = float(value)

    def _begin_tick(self):
        if self._pending_time is not None:
            self._song_time, self._pending_time = self._pending_time, None
        self._in_script = True

    def _end_tick(self):
        self._in_script = False

    # 本物の Live は、ロケーターを作るときアレンジメントのグリッドに吸い付ける（吸い付いた先にあれば消す）
    cue_grid = 0.25

    def set_or_delete_cue(self):
        """Mirror Live's toggle at the current Arrangement playback position."""
        at = float(self.current_song_time)
        cues = list(self.cue_points)
        hit = next((c for c in cues if abs(c.time - at) < 1e-4), None)
        if hit is None and self.cue_grid:
            at = round(at / self.cue_grid) * self.cue_grid
            hit = next((c for c in cues if abs(c.time - at) < 1e-4), None)
        if hit is None:
            cues.append(CuePoint(at, str(len(cues) + 1)))  # 本物の Live と同じく、作った順の番号の名前
        else:
            cues.remove(hit)
        self.cue_points = cues  # 本物の Live と同じく、位置の順には並べない

    def _slots(self):
        return len(self.scenes)

    def create_midi_track(self, index=-1):
        return self._insert_track(Track("%d-MIDI" % (len(self.tracks) + 1), True, self._slots(), len(self.return_tracks)), index)

    def create_audio_track(self, index=-1):
        return self._insert_track(Track("%d-Audio" % (len(self.tracks) + 1), False, self._slots(), len(self.return_tracks)), index)

    def _insert_track(self, track, index):
        tracks = list(self.tracks)
        tracks.insert(len(tracks) if index < 0 else index, track)
        self.tracks = tracks
        # 本物の Live と同じく、作ったトラックが選ばれ、開いていたクリップのノートの選択は外れる
        if self.view.detail_clip is not None:
            self.view.detail_clip.deselect_all_notes()
        self.view.selected_track = track
        self.view.detail_clip = None
        return track

    def delete_track(self, index):
        tracks = list(self.tracks)
        doomed = tracks[index]
        # 本物と同じく、グループを消すと中のトラックも消える
        tracks = [t for t in tracks if t is not doomed and t.group_track is not doomed]
        self.tracks = tracks

    # テスト用: ユーザーが Ctrl+G でグループにした / Ctrl+Shift+G で解除した
    def user_group(self, members, name="Group"):
        index = min(self.tracks.index(t) for t in members)
        group = Track(name, midi=False, slots=len(self.scenes), sends=len(self.return_tracks), group=True)
        for t in members:
            t.is_grouped = True
            t.group_track = group
        tracks = list(self.tracks)
        tracks.insert(index, group)
        self.tracks = tracks
        return group

    def user_ungroup(self, group):
        for t in self.tracks:
            if t.group_track is group:
                t.is_grouped = False
                t.group_track = None
        self.tracks = [t for t in self.tracks if t is not group]

    def create_return_track(self):
        self.return_tracks = self.return_tracks + [Track("Return", midi=False, slots=0, sends=len(self.return_tracks) + 1)]
        for t in self.tracks + self.return_tracks[:-1]:
            t.mixer_device.sends.append(DeviceParameter("Send", 0.0))

    def delete_return_track(self, index):
        returns = list(self.return_tracks)
        del returns[index]
        for t in self.tracks + returns:
            del t.mixer_device.sends[index]
        self.return_tracks = returns

    def create_scene(self, index=-1):
        scenes = list(self.scenes)
        pos = len(scenes) if index < 0 else index
        scenes.insert(pos, Scene())
        for t in self.tracks:
            t.clip_slots.insert(pos, ClipSlot())
        self.scenes = scenes

    def delete_scene(self, index):
        scenes = list(self.scenes)
        del scenes[index]
        for t in self.tracks:
            del t.clip_slots[index]
        self.scenes = scenes

    def move_device(self, device, target, position):
        for container in self._containers():
            if device in container.devices:
                container.devices = [d for d in container.devices if d is not device]
        devices = list(target.devices)
        devices.insert(min(position, len(devices)), device)
        target.devices = devices
        return position

    def _containers(self):
        result = []
        for t in self.tracks + self.return_tracks + [self.master_track]:
            result.append(t)
            for d in t.devices:
                result.extend(d.chains)
        return result


# ------------------------------------------------------------------ browser

class BrowserItem:
    def __init__(self, name, children=(), loadable=False, factory=None):
        self.name = name
        self.children = list(children)
        self.is_loadable = loadable
        self.is_folder = bool(children)
        self.factory = factory


class Browser:
    def __init__(self, installed_plugins):
        self.plugins = BrowserItem("Plug-Ins", [
            BrowserItem("VST3", [BrowserItem(n, loadable=True, factory=lambda n=n: make_plugin(n)) for n in installed_plugins]),
        ])
        self.audio_effects = BrowserItem("Audio Effects", [
            BrowserItem(n, loadable=True, factory=lambda n=n: make_native(n)) for n in NATIVE_DEVICES
        ])
        self.instruments = BrowserItem("Instruments", [])
        self.midi_effects = BrowserItem("MIDI Effects", [])
        self.song = None

    def load_item(self, item):
        track = self.song.view.selected_track
        track.user_add(item.factory())


class _App:
    browser = None

    def get_major_version(self):
        return 12

    def get_minor_version(self):
        return 4

    def get_bugfix_version(self):
        return 6


class Application:
    current = _App()

    @staticmethod
    def get_application():
        return Application.current
