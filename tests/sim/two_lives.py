"""
モックの Live 4 台（A・B・C・D）で同時編集を再現するテスト。

事前に DawSync.Cli を 4 つ起動しておく（run_sim.sh がやる）:
  A: session --host  --bridge-port 47410 --port 47411 --key TEST42 --samples logs/samples_a
  B: session --join 127.0.0.1:47411 --key TEST42 --bridge-port 47420 --samples logs/samples_b
  C: 同上（途中から、同じセットで参加）
  D: 同上 --overwrite（途中から、空のセットで参加して上書き）
環境変数 SYNC_DEVICES=1 で、デバイス（音源・エフェクト）も同期するモードのテストをする。
本物の Remote Script（src/.../RemoteScript）をモックの Live の上で動かし、
A で変更したものが B に届くか（逆も）、途中から参加した C が同じ状態になるかを確かめる。
"""
import os
import random
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "mock_live"))
sys.path.insert(0, os.path.join(HERE, "..", "..", "src", "DawSync.Core", "RemoteScript"))

import Live  # noqa: E402  (モック)
from DawSync import model as model_module  # noqa: E402
from DawSync import multi  # noqa: E402

model_module.SYNC_DEVICES = os.environ.get("SYNC_DEVICES") == "1"
SYNC = model_module.SYNC_DEVICES

LOGS = os.path.join(HERE, "logs")
VERBOSE = os.environ.get("VERBOSE") == "1"
results = []


class CInstance:
    def __init__(self, name, song):
        self.name = name
        self._song = song

    def song(self):
        return self._song

    def log(self, message):
        if VERBOSE or "error" in message.lower() or "failed" in message.lower():
            print("  [%s] %s" % (self.name, message))


class Side:
    """1 台分の Live（モック）と、その上で動く Remote Script。"""

    def __init__(self, name, song, port, plugins):
        self.name = name
        self.song = song
        self.port = port
        self.app = Live._App()
        self.app.browser = Live.Browser(plugins)
        self.app.browser.song = song
        self.script = None
        self.warnings = []

    def start(self):
        Live.Application.current = self.app
        self.script = multi.DawSync(CInstance(self.name, self.song))
        self.script._port = self.port
        original = self.script._warn
        self.script._warn = lambda m: (self.warnings.append(m), original(m))
        self.script._model.warn = self.script._warn

    def tick(self):
        if self.script is not None:
            Live.Application.current = self.app
            self.script.update_display()


def make_song():
    song = Live.Song(scenes=4)
    song.tempo = 128.0
    bass = Live.Track("Bass", slots=4)
    bass.clip_slots[0].create_clip(4.0)
    for i in range(4):
        bass.clip_slots[0].clip._notes.append(Live.MidiNote(36, float(i), 0.5, 100))
    bass.arrangement_clips = [Live.Clip(8.0, 16.0, [(40, 0.0, 1.0, 90)])]
    bass.devices = [Live.make_plugin("Serum 2"), Live.make_native("EQ Eight")]
    lead = Live.Track("Lead", slots=4)
    drums = Live.Track("Drums", midi=False, slots=4)
    drums.devices = [Live.make_native("Compressor")]
    song.tracks = [bass, lead, drums]
    song.return_tracks = [Live.Track("Reverb", midi=False, slots=0)]
    song.master_track.devices = [Live.make_native("Utility")]
    return song


def clone_structure(src):
    """途中から参加する人用: 同じセットを別に開いた状態（ID は別々、クリップは無し）を作る。"""
    song = Live.Song(scenes=len(src.scenes))
    song.tempo = 99.0
    tracks = []
    for t in src.tracks:
        nt = Live.Track(t.name, midi=t.has_midi_input, slots=len(src.scenes), sends=len(src.return_tracks))
        nt.devices = [Live.make_plugin(d.class_display_name) if d.class_name == "PluginDevice" else Live.make_native(d.class_display_name)
                      for d in t.devices if d.class_display_name != "OnlyA Synth"]
        tracks.append(nt)
    song.tracks = tracks
    song.return_tracks = [Live.Track(r.name, midi=False, slots=0, sends=len(src.return_tracks)) for r in src.return_tracks]
    song.master_track.devices = [Live.make_native(d.class_display_name) for d in src.master_track.devices]
    return song


A = Side("A", make_song(), 47410, ["Serum 2", "OnlyA Synth"])
B = Side("B", make_song(), 47420, ["Serum 2"])
C = Side("C", None, 47430, ["Serum 2"])
D = Side("D", None, 47440, ["Serum 2"])
E = Side("E", None, 47460, ["Serum 2"])
sides = [A, B, C, D, E]


def tick():
    for side in sides:
        side.tick()


# 遅延のある中継を通すとき（run_sim.sh の LATENCY_MS）は、届くまで長めに待つ
TIMEOUT_SCALE = 1.0 + float(os.environ.get("LATENCY_MS") or 0) / 100.0


def run_until(cond, timeout=4.0):
    end = time.time() + timeout * TIMEOUT_SCALE
    while time.time() < end:
        tick()
        if cond():
            for _ in range(10):  # 少し余分に回して、エコーや二重送信が起きないかも確かめる
                tick()
                time.sleep(0.01)
            return True
        time.sleep(0.02)
    return False


def check(name, cond, timeout=4.0):
    try:
        ok = run_until(lambda: _safe(cond), timeout)
    except Exception as e:  # noqa: BLE001
        ok = False
        print("     例外: %r" % e)
    results.append((name, ok))
    print(("PASS " if ok else "FAIL ") + name)
    return ok


def _safe(cond):
    try:
        return cond()
    except (IndexError, AttributeError, KeyError):
        return False


def notes(clip):
    return sorted((n.pitch, n.start_time, n.duration) for n in clip._notes)


def names(song):
    return [t.name for t in song.tracks]


def devices(track):
    return [d.class_display_name for d in track.devices]


def track(song, name):
    return next(t for t in song.tracks if t.name == name)


def comparable(side, skip_devices=()):
    """全キーの値（ファイルはパスが PC ごとに違うのでファイル名だけ）"""
    model = side.script._model
    out = {}
    for key in model.bindings:
        if any(key.startswith("d/%s" % d) for d in skip_devices):
            continue
        value = model.read(key)
        if isinstance(value, dict) and value.get("file"):
            value = dict(value, file=os.path.basename(value["file"]))
        out[key] = value
    return out


def only_a_ids():
    model = A.script._model
    return [i for i, o in model._objects.items() if getattr(o, "class_display_name", "") == "OnlyA Synth"]


def diff_keys(x, y):
    return sorted(k for k in set(x) | set(y) if x.get(k) != y.get(k))


sample_dir = os.path.join(LOGS, "source_samples")
os.makedirs(sample_dir, exist_ok=True)
KICK = os.path.join(sample_dir, "kick.wav")
SNARE = os.path.join(sample_dir, "snare.wav")
for path, size in ((KICK, 300000), (SNARE, 1500000)):
    with open(path, "wb") as f:
        f.write(os.urandom(size))

# ============================================================== 接続
A.start()
B.start()
check("A と B がアプリに接続する", lambda: A.script._connected and B.script._connected, timeout=10)
run_until(lambda: False, timeout=2.0)  # ルームへの初回同期を待つ

# ============================================================== 同じ PC で別の DAW も開いている
import json as _json
import socket as _socket
other = _socket.create_connection(("127.0.0.1", 47410), timeout=3)
other.sendall((_json.dumps({"t": "hello", "proto": 2, "script": "bitwig-test", "live": "Bitwig Studio 6", "daw": "bitwig"}) + chr(10)).encode())
reply = b""
end_time = time.time() + 3
while b"busy" not in reply and time.time() < end_time:
    tick()
    try:
        other.settimeout(0.05)
        reply += other.recv(4096)
    except _socket.timeout:
        pass
other.close()
check("先に Live がつながっていると、後から来た別の DAW は待たされる（取り合いにならない）",
      lambda: b'"busy"' in reply and A.script._connected)

# ============================================================== 基本
A.song.tempo = 140.0
check("テンポ", lambda: B.song.tempo == 140.0)

B.song.tracks[1].mixer_device.volume.value = 0.5
A.song.tracks[1].mixer_device.panning.value = -0.4
A.song.tracks[1].mixer_device.sends[0].value = 0.3
A.song.master_track.mixer_device.volume.value = 0.7
A.song.tracks[0].mute = True
B.song.tracks[1].solo = True
A.song.tempo = 141.0
check("ミキサー（音量・パン・センド・マスター・ミュート・ソロ）は同期しない（各自のもの）",
      lambda: B.song.tempo == 141.0 and A.song.tracks[1].mixer_device.volume.value != 0.5
      and B.song.tracks[1].mixer_device.panning.value != -0.4 and B.song.tracks[1].mixer_device.sends[0].value != 0.3
      and B.song.master_track.mixer_device.volume.value != 0.7
      and B.song.tracks[0].mute is False and A.song.tracks[1].solo is False)

# ============================================================== アレンジメントのロケーター／テンポ・拍子の変化
TIMING_KEYS = ("tempo", "sig", "tempo_map", "sig_map")


def record_sent(side):
    """その Live が送った変更のキーを side.sent に残す。"""
    original = side.script._send_ops
    side.sent = []

    def wrapped(ops):
        side.sent.extend(o["k"] for o in ops or [])
        return original(ops)
    side.script._send_ops = wrapped


def quiet(side, keys, seconds=0.6):
    """しばらく回して、keys のどれも送らなかったか。"""
    side.sent = []
    run_until(lambda: False, timeout=seconds)
    return not any(k in keys for k in side.sent)


record_sent(A)
record_sent(B)

A.song.current_song_time = 8.0
A.song.set_or_delete_cue()
A.song.cue_points[0].name = "Verse"
A.song.current_song_time = 0.0
check("ロケーターの位置と名称", lambda: [(c.time, c.name) for c in B.song.cue_points] == [(8.0, "Verse")])
_cue_a = A.song.cue_points[0]
B.song.cue_points[0].name = "Verse B"
check("ロケーター名の変更", lambda: [(c.time, c.name) for c in A.song.cue_points] == [(8.0, "Verse B")])
check("名前の変更では、相手のロケーターを作り直さない", lambda: A.song.cue_points[0] is _cue_a)
A.song.cue_points[0].time = 12.0  # ロケーターをドラッグした（本物の Live では API からは動かせない）
check("ロケーター位置の変更", lambda: [(c.time, c.name) for c in B.song.cue_points] == [(12.0, "Verse B")])
A.song.current_song_time = 12.0
A.song.set_or_delete_cue()
A.song.current_song_time = 0.0
check("ロケーターの削除", lambda: len(B.song.cue_points) == 0)
for beat, name in ((4.0, "Intro"), (16.0, "Chorus")):
    A.song.current_song_time = beat
    A.song.set_or_delete_cue()
    next(c for c in A.song.cue_points if c.time == beat).name = name
A.song.current_song_time = 0.0
check("ロケーターを続けて追加", lambda: [(c.time, c.name) for c in B.song.cue_points] == [(4.0, "Intro"), (16.0, "Chorus")])
next(c for c in A.song.cue_points if c.time == 16.0).name = "Chorus 2"
check("後から増えたロケーターの名前の変更も届く",
      lambda: [(c.time, c.name) for c in B.song.cue_points] == [(4.0, "Intro"), (16.0, "Chorus 2")])

# 再生中に届いたロケーターは、再生位置を動かさないよう止まってから反映する
B.song.is_playing = True
B.song.current_song_time = 33.0
run_until(lambda: False, timeout=0.3)
A.song.current_song_time = 24.0
A.song.set_or_delete_cue()
A.song.cue_points[-1].name = "Bridge"
A.song.current_song_time = 0.0
run_until(lambda: False, timeout=1.5)
check("再生中は再生位置を動かさない（ロケーターは止まるまで待つ）",
      lambda: B.song.current_song_time == 33.0 and len(B.song.cue_points) == 2, timeout=0.5)
B.sent = []
B.song.is_playing = False
check("止まったらロケーターを反映する（再生位置は元のまま）",
      lambda: [(c.time, c.name) for c in B.song.cue_points] == [(4.0, "Intro"), (16.0, "Chorus 2"), (24.0, "Bridge")]
      and B.song.current_song_time == 33.0)
check("反映したロケーターを送り返さない", lambda: "locators" not in B.sent and quiet(B, ("locators",)), timeout=1.0)
B.song.current_song_time = 0.0

# --- テンポ・拍子の変化。Live で書いたオートメーションは、保存した .als から読む
import gzip as _gzip

SIG_3_4, SIG_4_4, SIG_7_8 = 2 + 2 * 99, 3 + 2 * 99, 6 + 3 * 99


def save_als(side, tempo_events, sig_events=((-63072000, SIG_4_4),), manual=None):
    """Live がセットを保存した（.als を書いた）ことにする。"""
    path = os.path.join(LOGS, "%s.als" % side.name)
    tempo_xml = "".join('<FloatEvent Id="%d" Time="%s" Value="%s" />' % (n, t, v) for n, (t, v) in enumerate(tempo_events))
    sig_xml = "".join('<EnumEvent Id="%d" Time="%s" Value="%s" />' % (n, t, v) for n, (t, v) in enumerate(sig_events))
    manual = tempo_events[0][1] if manual is None else manual
    xml = ('<?xml version="1.0" encoding="UTF-8"?><Ableton MajorVersion="5" Creator="Ableton Live 12.4.6"><LiveSet>'
           '<MainTrack><AutomationEnvelopes><Envelopes>'
           '<AutomationEnvelope Id="0"><EnvelopeTarget><PointeeId Value="10" /></EnvelopeTarget>'
           '<Automation><Events>%s</Events></Automation></AutomationEnvelope>'
           '<AutomationEnvelope Id="1"><EnvelopeTarget><PointeeId Value="8" /></EnvelopeTarget>'
           '<Automation><Events>%s</Events></Automation></AutomationEnvelope>'
           '</Envelopes></AutomationEnvelopes><DeviceChain><Mixer>'
           '<Tempo><Manual Value="%s" /><AutomationTarget Id="8" /></Tempo>'
           '<TimeSignature><Manual Value="201" /><AutomationTarget Id="10" /></TimeSignature>'
           '</Mixer></DeviceChain></MainTrack></LiveSet></Ableton>') % (sig_xml, tempo_xml, manual)
    with _gzip.open(path, "wb") as f:
        f.write(xml.encode("utf-8"))
    save_als.count = getattr(save_als, "count", 0) + 1
    stamp = time.time() + save_als.count
    os.utime(path, (stamp, stamp))
    side.song.file_path = path


def map_of(side, key="tempo_map"):
    return side.script._model.read(key)


A.song.tempo = 100.0
check("テンポ（オートメーションの無いセット）", lambda: B.song.tempo == 100.0 and map_of(B) == [[0.0, 100.0]])

# Live で「8 拍目で 100 → 120 に跳び、16 拍目までに 140 へ上げる（ランプ）」オートメーションと、
# 「3/4 で始まり 16 拍目から 7/8」の拍子を書いて保存した。Live は点と点の間を直線で結ぶので、段差は同じ時刻の 2 点
A_TEMPO_EVENTS = [(-63072000, 100), (8, 100), (8, 120), (16, 140)]
A_SIG_EVENTS = [(-63072000, SIG_3_4), (16, SIG_7_8)]
save_als(A, A_TEMPO_EVENTS, A_SIG_EVENTS)
EXPECTED_TEMPO = [[0.0, 100.0], [8.0, 120.0, 1], [16.0, 140.0]]
check("保存した .als のテンポ・オートメーション（段差とランプ）が届く", lambda: map_of(B) == EXPECTED_TEMPO)
check("保存した .als の拍子の変化が届く", lambda: map_of(B, "sig_map") == [[0.0, 3, 4], [16.0, 7, 8]])
check("届いた変化は、止まっていても今の位置の値にする", lambda: B.song.tempo == 100.0
      and (B.song.signature_numerator, B.song.signature_denominator) == (3, 4))
check("拍 0 の値（古いバージョン向け）も届く", lambda: B.script._model.read("tempo") == 100.0
      and B.script._model.read("sig") == [3, 4])
B.song.is_playing = True
B.song.current_song_time = 12.0
check("再生中はランプの途中の BPM に追従する", lambda: abs(B.song.tempo - 130.0) < 1e-6, timeout=1.0)
B.song.current_song_time = 20.0
check("再生中は拍子の変化にも追従する", lambda: B.song.tempo == 140.0
      and (B.song.signature_numerator, B.song.signature_denominator) == (7, 8), timeout=1.0)
check("追従させた値を、自分の変更として送らない", lambda: quiet(B, TIMING_KEYS), timeout=1.0)
B.song.current_song_time = 0.0
check("ループやシークで戻ると、戻った位置の値になる", lambda: B.song.tempo == 100.0
      and (B.song.signature_numerator, B.song.signature_denominator) == (3, 4), timeout=1.0)
B.song.is_playing = False
B.song.current_song_time = 10.0
check("止まっているときも、再生位置を動かすとその位置の値にする", lambda: abs(B.song.tempo - 125.0) < 1e-6, timeout=1.0)
check("位置を動かして変わったテンポは送らない", lambda: quiet(B, TIMING_KEYS) and map_of(A) == EXPECTED_TEMPO, timeout=1.0)

# B が保存した（B の .als にはオートメーションが無く、手で入れた値は追従中の値）。ルームのテンポの変化を壊さない
save_als(B, [(-63072000, 125.0)])
check("オートメーションの無いセットを保存しても、ルームのテンポの変化を消さない",
      lambda: quiet(B, TIMING_KEYS) and map_of(A) == EXPECTED_TEMPO and map_of(B) == EXPECTED_TEMPO, timeout=1.5)
# A がもう一度保存した（オートメーションはそのまま、手で入れた値だけ違う）。前と同じなので送らない
A.sent = []
save_als(A, A_TEMPO_EVENTS, A_SIG_EVENTS, manual=133)
check("オートメーションを変えずに保存しても送らない", lambda: quiet(A, TIMING_KEYS), timeout=1.0)

# 止まっているときに手でテンポを変えたら、再生位置の区間の BPM を変えたことにする
B.song.current_song_time = 20.0
run_until(lambda: B.song.tempo == 140.0, timeout=1.0)
B.song.tempo = 150.0
EDITED_TEMPO = [[0.0, 100.0], [8.0, 120.0, 1], [16.0, 150.0]]
check("止まっているときに手で変えたテンポは、その区間の BPM として届く", lambda: map_of(A) == EDITED_TEMPO)
B.song.current_song_time = 0.0

# Live 自身のテンポのオートメーションが動かしている間の変化は送らない
A.song.master_track.mixer_device.song_tempo.automation_state = 1
A.song.is_playing = True
A.song.tempo = 111.0
check("Live のオートメーションが動かしているテンポは送らない",
      lambda: quiet(A, TIMING_KEYS) and map_of(B) == EDITED_TEMPO, timeout=1.0)
A.song.is_playing = False
A.song.master_track.mixer_device.song_tempo.automation_state = 0
A.song.current_song_time = 0.0
run_until(lambda: False, timeout=0.3)

# A が Live でオートメーションを消して保存した → テンポ・拍子の変化が無くなったことが届く
save_als(A, [(-63072000, 120.0)], [(-63072000, SIG_3_4)])
check("Live でオートメーションを消して保存すると、変化が無くなったことが届く",
      lambda: map_of(B) == [[0.0, 120.0]] and map_of(B, "sig_map") == [[0.0, 3, 4]] and B.song.tempo == 120.0)
A.song.signature_numerator = 4
check("拍子（変化の無いセット）", lambda: map_of(B, "sig_map") == [[0.0, 4, 4]]
      and (B.song.signature_numerator, B.song.signature_denominator) == (4, 4))
A.song.tempo = 141.0
check("テンポを手で変えると、拍 0 の値とテンポの変化の両方が届く",
      lambda: B.song.tempo == 141.0 and map_of(B) == [[0.0, 141.0]] and B.script._model.read("tempo") == 141.0)
for side in (A, B):
    side.song.file_path = None

B.song.tracks[2].name = "Drums Bus"
check("トラック名", lambda: A.song.tracks[2].name == "Drums Bus")

# ============================================================== ノートとクリップ
A.song.tracks[0].clip_slots[0].clip.user_add_note(43, 2.5)
check("ノート", lambda: notes(B.song.tracks[0].clip_slots[0].clip) == notes(A.song.tracks[0].clip_slots[0].clip))

bass_clip_b = B.song.tracks[0].clip_slots[0].clip
bass_clip_b._notes[0].mpe = "B が描いたピッチベンド"
A.song.tracks[0].clip_slots[0].clip.user_add_note(50, 3.0)
check("相手がノートを追加しても、自分の MPE は消えない",
      lambda: any(n.pitch == 50 for n in bass_clip_b._notes)
      and any(n.mpe == "B が描いたピッチベンド" for n in bass_clip_b._notes))

clip_a = A.song.tracks[0].clip_slots[0].clip
clip_a._notes[1].probability = 0.5
clip_a._notes[1].velocity_deviation = 20.0
clip_a._notify("notes")
check("ノートの確率・ベロシティ範囲",
      lambda: any(n.probability == 0.5 and n.velocity_deviation == 20.0 for n in bass_clip_b._notes)
      and any(n.mpe == "B が描いたピッチベンド" for n in bass_clip_b._notes))

B.song.tracks[0].arrangement_clips[0].user_add_note(47, 4.0)
check("アレンジメントのノート",
      lambda: notes(A.song.tracks[0].arrangement_clips[0]) == notes(B.song.tracks[0].arrangement_clips[0]))

# アレンジメントでクリップを動かす（重なっても切れ端が残らないこと）
# B はそのクリップを開いて編集中（作り直されても開いたままになること）。B だけが持っている MPE もある
B.song.tracks[0].arrangement_clips[0]._notes[0].mpe = "B のアレンジのピッチベンド"
B.song.view.detail_clip = B.song.tracks[0].arrangement_clips[0]
B.song.view.selected_track = B.song.tracks[0]
arr_a = A.song.tracks[0].arrangement_clips[0]
arr_a.start_time, arr_a.end_time = 13.0, 21.0   # 左へ動かす（右側に元のクリップの切れ端ができやすい）


def arrangement(side, index=0):
    return [(c.start_time, c.end_time, len(c._notes)) for c in side.song.tracks[index].arrangement_clips]


check("アレンジメントのクリップを動かしても切れ端が残らない",
      lambda: arrangement(B) == arrangement(A) and len(arrangement(B)) == 1 and arrangement(B)[0][0] == 13.0)
check("相手が動かしても、クリップの中の MPE（同期していない情報）は消えない",
      lambda: any(n.mpe == "B のアレンジのピッチベンド" for n in B.song.tracks[0].arrangement_clips[0]._notes))

arr_a = A.song.tracks[0].arrangement_clips[0]
arr_a.end_time = 25.0
check("アレンジメントのクリップの右端を伸ばす", lambda: arrangement(B) == arrangement(A) == [(13.0, 25.0, arrangement(A)[0][2])])


def edges(side, index=0):
    return [(c.start_time, c.end_time, c.start_marker, c.looping, c.loop_start, c.loop_end, sorted(notes(c)))
            for c in side.song.tracks[index].arrangement_clips]


# 左端を動かす（位置と中身の開始位置が一緒にずれ、右端はそのまま）
arr_a = A.song.tracks[0].arrangement_clips[0]
arr_a.start_time, arr_a.start_marker = 14.0, arr_a.start_marker + 1.0
check("アレンジメントのクリップの左端を動かす", lambda: edges(B) == edges(A) and edges(B)[0][:2] == (14.0, 25.0))
arr_b = B.song.tracks[0].arrangement_clips[0]
arr_b.start_time, arr_b.start_marker = 13.0, arr_b.start_marker - 1.0
check("左端を戻す（逆向き）", lambda: edges(A) == edges(B) and edges(A)[0][:2] == (13.0, 25.0))
arr_a = A.song.tracks[0].arrangement_clips[0]
arr_a.loop_start, arr_a.loop_end = 1.0, 3.0
check("アレンジメントのクリップのループ範囲", lambda: edges(B) == edges(A) and edges(B)[0][4:6] == (1.0, 3.0))
arr_b = B.song.tracks[0].arrangement_clips[0]
arr_b.looping = False
check("アレンジメントのクリップのループを切る", lambda: edges(A) == edges(B) and edges(A)[0][3] is False)
arr_a = A.song.tracks[0].arrangement_clips[0]
arr_a.looping = True
check("アレンジメントのクリップのループを入れる", lambda: edges(A) == edges(B) and edges(B)[0][3] is True)
arr_a = A.song.tracks[0].arrangement_clips[0]
A.song.tracks[0].user_split(arr_a, arr_a.start_time + 4.0)
check("アレンジメントのクリップを分割する", lambda: edges(B) == edges(A) and len(edges(B)) == 2 and edges(B)[1][0] == 17.0)
B.song.tracks[0].delete_clip(B.song.tracks[0].arrangement_clips[1])
check("分割した後ろを消す", lambda: edges(A) == edges(B) and len(edges(A)) == 1)
# 同じクリップを 2 人が同時に編集する（届く前にお互い編集する）
clip_a, clip_b = A.song.tracks[0].arrangement_clips[0], B.song.tracks[0].arrangement_clips[0]
clip_a.user_add_note(70, 1.0)
clip_b.user_add_note(72, 2.0)
check("同時に別々のノートを足しても、両方残る",
      lambda: notes(A.song.tracks[0].arrangement_clips[0]) == notes(B.song.tracks[0].arrangement_clips[0])
      and {(70, 1.0, 0.5), (72, 2.0, 0.5)} <= set(notes(A.song.tracks[0].arrangement_clips[0])))

clip_a, clip_b = A.song.tracks[0].arrangement_clips[0], B.song.tracks[0].arrangement_clips[0]
clip_b.user_add_note(74, 3.0)                 # B がノートを編集している最中に
clip_a.name = "Riff"                          # A がクリップの名前を変える
check("ノートの編集と名前の変更を同時にしても、どちらも消えない",
      lambda: A.song.tracks[0].arrangement_clips[0].name == B.song.tracks[0].arrangement_clips[0].name == "Riff"
      and (74, 3.0, 0.5) in notes(A.song.tracks[0].arrangement_clips[0])
      and notes(A.song.tracks[0].arrangement_clips[0]) == notes(B.song.tracks[0].arrangement_clips[0]))

# B がノートを選んで編集中に、A がトラックを追加する（Live はそちらを選んで、ノートの選択を外してしまう）
clip_b = B.song.tracks[0].arrangement_clips[0]
B.song.view.detail_clip, B.song.view.selected_track = clip_b, B.song.tracks[0]
clip_b.select_notes_by_id([n.note_id for n in clip_b._notes[:2]])
chosen = set(clip_b._selected)
A.song.create_midi_track(len(A.song.tracks))
A.song.tracks[-1].name = "New From A"
check("相手がトラックを追加しても、開いていたクリップと選んでいたノートはそのまま",
      lambda: "New From A" in names(B.song) and B.song.view.detail_clip is clip_b
      and B.song.view.selected_track is B.song.tracks[0] and clip_b._selected == chosen)
A.song.delete_track(len(A.song.tracks) - 1)
run_until(lambda: "New From A" not in names(B.song))

check("相手が動かしたり伸ばしたりしても、開いていたクリップは開いたまま",
      lambda: B.song.view.detail_clip is B.song.tracks[0].arrangement_clips[0] and B.song.view.selected_track is B.song.tracks[0])

slot_b = B.song.tracks[1].clip_slots[2]
slot_b.create_clip(8.0)
slot_b.clip.user_add_note(60, 0.0)
slot_b.clip.name = "Chords"
check("クリップを作る（名前・長さ・ノートごと）",
      lambda: A.song.tracks[1].clip_slots[2].clip.name == "Chords"
      and notes(A.song.tracks[1].clip_slots[2].clip) == notes(slot_b.clip)
      and A.song.tracks[1].clip_slots[2].clip.loop_end == 8.0)

A.song.tracks[1].clip_slots[2].clip.loop_end = 6.0
check("クリップのループ範囲", lambda: B.song.tracks[1].clip_slots[2].clip.loop_end == 6.0)

A.song.tracks[1].clip_slots[2].delete_clip()
check("クリップを消す", lambda: not B.song.tracks[1].clip_slots[2].has_clip)

# ============================================================== トラック・シーン
A.song.create_midi_track(1)
A.song.tracks[1].name = "Pad"
check("トラックを追加（同じ位置に）", lambda: names(B.song) == names(A.song) and B.song.tracks[1].name == "Pad")

B.song.tracks[1].name = "Pad 2"
check("追加したトラックの名前", lambda: A.song.tracks[1].name == "Pad 2")
B.song.tracks[1].name = "Pad"
run_until(lambda: A.song.tracks[1].name == "Pad")

A.song.create_scene(1)
A.song.scenes[1].name = "Verse"
check("シーンを追加", lambda: len(B.song.scenes) == len(A.song.scenes) and B.song.scenes[1].name == "Verse")

B.song.tracks[1].clip_slots[1].create_clip(4.0)
B.song.tracks[1].clip_slots[1].clip.user_add_note(72, 0.0)
check("追加したシーンのクリップ", lambda: A.song.tracks[1].clip_slots[1].has_clip
      and notes(A.song.tracks[1].clip_slots[1].clip) == [(72, 0.0, 0.5)])

A.song.create_midi_track(-1)
A.song.tracks[-1].name = "Temp"
run_until(lambda: "Temp" in names(B.song))
B.song.delete_track(len(B.song.tracks) - 1)
check("トラックを削除", lambda: "Temp" not in names(A.song) and names(A.song) == names(B.song))

A.song.create_midi_track(0)
A.song.tracks[0].name = "From A"
B.song.create_audio_track(0)
B.song.tracks[0].name = "From B"
check("同時にトラックを追加しても両方残り、同じ順番になる",
      lambda: "From A" in names(A.song) and "From B" in names(A.song) and names(A.song) == names(B.song))

# ============================================================== グループ
pad_a, lead_a_t = track(A.song, "Pad"), track(A.song, "Lead")
group_a = A.song.user_group([pad_a, lead_a_t], "Synths")
check("相手がグループを作ると、作り方の案内が出る（グループは作られない）",
      lambda: any("Ctrl+G" in w for w in B.warnings) and not any(t.is_foldable for t in B.song.tracks))
before_names = names(B.song)
group_b = B.song.user_group([track(B.song, "Pad"), track(B.song, "Lead")], "Group")
check("同じトラックを手動でグループにすると、相手のグループとつながる（名前も同期される）",
      lambda: group_b.name == "Synths" and names(A.song) == names(B.song))

group_a.color = 0x123456
check("つながったグループの色", lambda: group_b.color == 0x123456)

A.song.user_ungroup(group_a)
check("相手がグループを解除しても、中のトラックは消えない（案内が出る）",
      lambda: "Pad" in names(B.song) and "Lead" in names(B.song) and any("Ctrl+Shift+G" in w for w in B.warnings), timeout=3)
B.song.user_ungroup(group_b)
check("こちらでも解除すると元どおり", lambda: names(A.song) == names(B.song) and not any(t.is_foldable for t in B.song.tracks))

# ============================================================== デバイス
bass_a, bass_b = track(A.song, "Bass"), track(B.song, "Bass")
lead_a, lead_b = track(A.song, "Lead"), track(B.song, "Lead")
if SYNC:
    bass_a.user_add(Live.make_native("Reverb"), 1)
    check("エフェクトを追加（同じ位置に）", lambda: devices(bass_b) == devices(bass_a) == ["Serum 2", "Reverb", "EQ Eight"])

    bass_a.devices[1].parameters[1].value = 0.9
    check("エフェクトのつまみ", lambda: bass_b.devices[1].parameters[1].value == 0.9)

    lead_b.user_add(Live.make_plugin("Serum 2"))
    check("VST を追加（相手にも入っているもの）", lambda: devices(lead_a) == ["Serum 2"])

    lead_a.devices[0].parameters[3].value = 0.25
    check("VST のつまみ", lambda: lead_b.devices[0].parameters[3].value == 0.25)

    lead_b.devices[0].selected_preset_index = 2
    check("VST のプリセット", lambda: lead_a.devices[0].selected_preset_index == 2)

    bass_a.user_add(Live.make_plugin("OnlyA Synth"))
    run_until(lambda: False, timeout=1.0)
    check("相手に無い VST は追加されず、警告が出る",
          lambda: "OnlyA Synth" not in devices(bass_b) and any("OnlyA Synth" in w for w in B.warnings))

    A.song.move_device(bass_a.devices[2], bass_a, 0)   # EQ Eight を先頭へ
    check("デバイスの並べ替え", lambda: devices(bass_b)[:3] == ["EQ Eight", "Serum 2", "Reverb"])

    bass_b.delete_device(devices(bass_b).index("Reverb"))
    check("デバイスを削除", lambda: "Reverb" not in devices(bass_a))

    A.song.master_track.devices[0].parameters[1].value = 0.1
    check("マスターのデバイス", lambda: B.song.master_track.devices[0].parameters[1].value == 0.1)

else:
    before_b = devices(bass_b)
    bass_a.user_add(Live.make_native("Reverb"), 1)
    lead_b.user_add(Live.make_plugin("Serum 2"))
    bass_a.devices[0].parameters[1].value = 0.9
    run_until(lambda: False, timeout=1.0)
    check("音源・エフェクトは同期されない（各自自由にかけられる）",
          lambda: devices(bass_b) == before_b and devices(lead_a) == [] and bass_b.devices[0].parameters[1].value != 0.9)

# ============================================================== サンプル
drums_a, drums_b = track(A.song, "Drums Bus"), track(B.song, "Drums Bus")
drums_a.clip_slots[0].create_audio_clip(KICK)
check("オーディオクリップ（サンプルが転送される）",
      lambda: drums_b.clip_slots[0].has_clip
      and os.path.basename(drums_b.clip_slots[0].clip.file_path) == "kick.wav"
      and os.path.abspath(drums_b.clip_slots[0].clip.file_path).startswith(os.path.join(LOGS, "samples_b"))
      and open(drums_b.clip_slots[0].clip.file_path, "rb").read() == open(KICK, "rb").read(), timeout=8)

drums_a.clip_slots[0].clip.gain = 0.8
drums_a.clip_slots[0].clip.warp_mode = 4
check("オーディオクリップのゲイン・ワープ", lambda: drums_b.clip_slots[0].clip.gain == 0.8 and drums_b.clip_slots[0].clip.warp_mode == 4)

drums_a.clip_slots[0].clip.pitch_coarse = -3
drums_a.clip_slots[0].clip.pitch_fine = 25.0
check("オーディオクリップのピッチ", lambda: drums_b.clip_slots[0].clip.pitch_coarse == -3 and drums_b.clip_slots[0].clip.pitch_fine == 25.0)


def warp_markers(clip):
    return [(m.beat_time, m.sample_time) for m in clip.warp_markers]


drums_a.clip_slots[0].clip.add_warp_marker(Live.Clip.WarpMarker(sample_time=0.8, beat_time=2.0))
check("ワープマーカーを足す", lambda: warp_markers(drums_b.clip_slots[0].clip) == warp_markers(drums_a.clip_slots[0].clip)
      and len(warp_markers(drums_b.clip_slots[0].clip)) == 3)
drums_b.clip_slots[0].clip.remove_warp_marker(2.0)
drums_b.clip_slots[0].clip.add_warp_marker({"beat_time": 1.5, "sample_time": 0.9})
check("ワープマーカーを動かす", lambda: warp_markers(drums_a.clip_slots[0].clip) == [(0.0, 0.0), (1.5, 0.9), (4.0, 2.0)])

drums_b.create_audio_clip(SNARE, 32.0)
check("アレンジメントのオーディオクリップ（大きいファイル）",
      lambda: any(c.file_path and c.file_path.endswith("snare.wav") and c.start_time == 32.0 for c in drums_a.arrangement_clips), timeout=10)

if SYNC:
    simpler = lead_a.user_add(Live.make_native("Simpler"))
    simpler.replace_sample(SNARE)
    check("Simpler のサンプル",
          lambda: devices(lead_b)[-1] == "Simpler" and lead_b.devices[-1].sample is not None
          and os.path.basename(lead_b.devices[-1].sample.file_path) == "snare.wav", timeout=8)

# バウンス: MIDI トラックが（新しい）オーディオトラックに置き換わり、ファイルは少し遅れて書き終わる
BOUNCE = os.path.join(sample_dir, "Pad Bounce.wav")
bounce_data = os.urandom(800000)
with open(BOUNCE, "wb") as f:
    f.write(bounce_data[:1000])      # まだ書き込み中
pad_index = names(A.song).index("Pad")
bounced = Live.Track("Pad", midi=False, slots=len(A.song.scenes), sends=len(A.song.return_tracks))
bounced.arrangement_clips = [Live.Clip(4.0, 0.0, file_path=BOUNCE)]  # モックのオーディオクリップは 4 拍
A.song.tracks = A.song.tracks[:pad_index] + [bounced] + A.song.tracks[pad_index + 1:]
run_until(lambda: False, timeout=1.0)
with open(BOUNCE, "wb") as f:
    f.write(bounce_data)             # 書き終わり


def bounced_ok(side):
    t = side.song.tracks[names(side.song).index("Pad")]
    return (not t.has_midi_input and len(t.arrangement_clips) == 1
            and open(t.arrangement_clips[0].file_path, "rb").read() == bounce_data)


check("バウンス（MIDI → オーディオ、書き込み中のファイル）が完成した音で届く", lambda: bounced_ok(B), timeout=12)

# ============================================================== ランダムな同時編集
rng = random.Random(1234)


def random_edit(side):
    song = side.song
    t = track(song, rng.choice(["Bass", "Lead", "Pad"]))
    kind = rng.randrange(7)
    if kind == 0:
        song.tempo = float(rng.choice([100, 110, 120, 130, 140]))
    elif kind == 1:
        t.mixer_device.volume.value = round(rng.random(), 3)
    elif kind == 2:
        t.mute = not t.mute
    elif kind == 3:
        track(song, "Bass").clip_slots[0].clip.user_add_note(rng.randrange(36, 72), rng.randrange(16) / 4.0)
    elif kind == 4 and track(song, "Bass").devices:
        d = rng.choice(track(song, "Bass").devices)
        rng.choice(d.parameters).value = round(rng.random(), 3)
    elif kind == 5:
        song.return_tracks[0].mixer_device.volume.value = round(rng.random(), 3)
    else:
        t.mixer_device.panning.value = round(rng.uniform(-1, 1), 3)


end = time.time() + 2.5
edits = 0
while time.time() < end:
    random_edit(A if rng.random() < 0.5 else B)
    edits += 1
    tick()
    time.sleep(rng.choice([0, 0.005, 0.02]))
check("ランダムに %d 回同時編集しても A と B が完全に一致する" % edits,
      lambda: comparable(A, only_a_ids()) == comparable(B), timeout=6)
d = diff_keys(comparable(A, only_a_ids()), comparable(B))
if d:
    print("     ずれているキー: %s" % d[:8])

# ============================================================== 途中から参加（別に開いた同じセット）
C.song = clone_structure(A.song)
C.app.browser.song = C.song
c_bass_devices = [id(x) for x in track(C.song, "Bass").devices]
C.start()
check("途中から参加した C が同じ状態になる（クリップ・サンプルも）",
      lambda: C.script._connected and C.song.tempo == A.song.tempo and names(C.song) == names(A.song)
      and track(C.song, "Drums Bus").clip_slots[0].has_clip and bounced_ok(C)
      and (not SYNC or devices(track(C.song, "Lead")) == devices(lead_a))
      and notes(track(C.song, "Bass").clip_slots[0].clip) == notes(bass_a.clip_slots[0].clip), timeout=15)
if not SYNC:
    check("C が自分でかけていた音源・エフェクトはそのまま残る",
          lambda: [id(x) for x in track(C.song, "Bass").devices] == c_bass_devices)

track(C.song, "Lead").color = 0x654321
check("C の変更が A と B に届く（ID の付け替えができている）",
      lambda: lead_a.color == 0x654321 and lead_b.color == 0x654321)

check("C も完全に一致する", lambda: comparable(C) == comparable(B), timeout=6)
d = diff_keys(comparable(C), comparable(B))
if d:
    print("     ずれているキー: %s" % d[:8])
    for k in d[:3]:
        print("       %s  C=%r  B=%r  A=%r" % (k, comparable(C).get(k), comparable(B).get(k), comparable(A).get(k)))
    for side in (A, C):
        m = side.script._model
        print("     %s Bass devices: %r" % (side.name, [(m.id_of(x), x.class_display_name) for x in track(side.song, "Bass").devices]))

# ============================================================== 空のセットから参加して上書き
D.song = Live.Song(scenes=2)
D.song.tracks = [Live.Track("1-MIDI", slots=2)]
D.app.browser.song = D.song
D.start()
check("空のセットから参加して上書きすると、トラック・クリップ・バウンスが全部届く",
      lambda: D.script._connected and names(D.song) == names(A.song) and len(D.song.scenes) == len(A.song.scenes)
      and bounced_ok(D) and notes(track(D.song, "Bass").clip_slots[0].clip) == notes(bass_a.clip_slots[0].clip),
      timeout=20)
check("D も完全に一致する", lambda: comparable(D) == comparable(B), timeout=6)
d = diff_keys(comparable(D), comparable(B))
if d:
    print("     ずれているキー: %s" % d[:8])

# ============================================================== 別のテンプレートの人が「まっさら」から参加
E.song = Live.Song(scenes=12)
E.song.tempo = 90.0
E.song.tracks = [Live.Track("Audio 1", midi=False, slots=12, sends=2), Live.Track("Audio 2", midi=False, slots=12, sends=2),
                 Live.Track("MIDI 1", slots=12, sends=2), Live.Track("MIDI 2", slots=12, sends=2)]
E.song.tracks[2].devices = [Live.make_native("EQ Eight")]
E.song.return_tracks = [Live.Track("A-Reverb", midi=False, slots=0, sends=2), Live.Track("B-Delay", midi=False, slots=0, sends=2)]
E.song.master_track.devices = [Live.make_native("Utility")]
E.app.browser.song = E.song
run_until(lambda: False, timeout=1.0)
tempo_before = A.song.tempo
E.start()
check("別のテンプレートの人も、まっさらにしてから参加すると同じ状態になる",
      lambda: E.script._connected and names(E.song) == names(A.song)
      and [r.name for r in E.song.return_tracks] == [r.name for r in A.song.return_tracks]
      and len(E.song.scenes) == len(A.song.scenes) and (SYNC or E.song.master_track.devices == [])
      and bounced_ok(E) and comparable(E) == comparable(B), timeout=20)
d = diff_keys(comparable(E), comparable(B))
if d:
    print("     ずれているキー: %s" % d[:8])
    print("     %s" % [(k, comparable(s).get(k)) for s in (A, B, E) for k in d[:3]])

check("まっさらにして参加した人のテンポ（120）が、みんなに広がらない", lambda: A.song.tempo == tempo_before == E.song.tempo, 3)

# ============================================================== エコーが続かないこと
sent = []
original_send = A.script._send
A.script._send = lambda msg: (sent.append(msg), original_send(msg))
run_until(lambda: False, timeout=1.0)
A.script._send = original_send
ok = not sent
results.append(("何も触らなければ何も送られない（無限ループしない）", ok))
print(("PASS " if ok else "FAIL ") + "何も触らなければ何も送られない（無限ループしない）" + ("" if ok else " %r" % sent[:2]))

# ============================================================== 後片付け
for side in sides:
    side.script.disconnect()
leftover = sum(o.listener_count() for o in [A.song] + list(A.song.tracks) + list(A.song.scenes) + list(bass_a.devices))
results.append(("disconnect でリスナーが外れる", leftover == 0))
print(("PASS " if leftover == 0 else "FAIL ") + "disconnect でリスナーが外れる")

failed = [n for n, ok in results if not ok]
print()
print("%d / %d passed" % (len(results) - len(failed), len(results)))
sys.exit(1 if failed else 0)
