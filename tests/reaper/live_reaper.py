"""
モックの Live 1 台と、本物の REAPER（DawSync のスクリプトを入れて起動しておく）で同時編集を確かめる。

  python tests/reaper/live_reaper.py            自動のテスト（REAPER の状態はスクリプトのデバッグ出力で見る）
  python tests/reaper/live_reaper.py --watch 300 テストの後、300 秒間つないだままにして Live 側の状態を
                                                 logs/live_state.json に書き続ける（REAPER を手で触って確かめる用）

REAPER で今開いているプロジェクトは「まっさら」にされるので、捨ててよいプロジェクトで行うこと。
REAPER 側はテスト用のポート（47491）を使うので、本物の Live やアプリには影響しない。
"""
import json
import os
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tests", "sim", "mock_live"))
sys.path.insert(0, os.path.join(ROOT, "src", "DawSync.Core", "RemoteScript"))

import Live  # noqa: E402  (モック)
from DawSync import multi  # noqa: E402

LOGS = os.path.join(HERE, "logs")
IPC = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "DawSync", "reaper-ipc")
TEST_PORT = 47491
BW_DIR = os.path.join(IPC, str(TEST_PORT))
STATE = os.path.join(BW_DIR, "state.json")
PORT_FILE = os.path.join(IPC, "port")
CLI = os.path.join(ROOT, "src", "DawSync.Cli", "bin", "Debug", "net10.0", "DawSync.Cli" + (".exe" if os.name == "nt" else ""))
results = []


class CInstance:
    def __init__(self, song):
        self._song = song

    def song(self):
        return self._song

    def log(self, message):
        if "error" in message.lower() or "failed" in message.lower():
            print("  [Live] " + message)


def make_song():
    song = Live.Song(scenes=4)
    song.tempo = 128.0
    bass = Live.Track("Bass", slots=4)
    bass.arrangement_clips = [Live.Clip(8.0, 8.0, [(40, 0.0, 1.0, 90), (43, 2.0, 1.0, 100)])]
    bass.arrangement_clips[0].name = "Bassline"
    keys = Live.Track("Keys", slots=4)
    keys.arrangement_clips = [Live.Clip(4.0, 0.0, [(60, 0.0, 2.0, 100), (64, 0.0, 2.0, 100), (67, 0.0, 2.0, 100)])]
    drums = Live.Track("Drums", midi=False, slots=4)
    song.tracks = [bass, keys, drums]
    return song


song = make_song()
app = Live._App()
app.browser = Live.Browser([])
app.browser.song = song
Live.Application.current = app
script = multi.DawSync(CInstance(song))
script._port = 47410


def tick():
    Live.Application.current = app
    script.update_display()


def run_until(cond, timeout):
    end = time.time() + timeout
    while time.time() < end:
        tick()
        try:
            if cond():
                return True
        except Exception:
            pass
        time.sleep(0.03)
    return False


def check(name, cond, timeout=6.0):
    # 遅延のある中継を通すときは、届くまで長めに待つ
    ok = run_until(cond, timeout * (1.0 + float(os.environ.get("LATENCY_MS") or 0) / 100.0))
    results.append((name, ok))
    print(("PASS " if ok else "FAIL ") + name, flush=True)
    return ok


# ---------------------------------------------------------------- REAPER の状態

def bw():
    with open(STATE, encoding="utf-8") as f:
        return json.load(f)


def bw_tracks(state=None):
    s = state or bw()
    ids = [k.split("/")[1] for k in s if k.startswith("t/") and k.count("/") == 1 and s[k]]
    ids.sort(key=lambda i: s["t/" + i]["o"])
    return [(i, s.get("t/%s/name" % i), s["t/" + i]["k"], s["t/" + i].get("g")) for i in ids]


def bw_names():
    return [t[1] for t in bw_tracks()]


def bw_id(name):
    return next(t[0] for t in bw_tracks() if t[1] == name)


def bw_clips(name):
    s = bw()
    tid = bw_id(name)
    clips = []
    for k, v in s.items():
        parts = k.split("/")
        if parts[0] == "c" and parts[1] == "a" and parts[2] == tid and v:
            clips.append((float(parts[3]), v.get("dur"), [tuple(r[:3]) for r in v.get("n", [])], v["p"].get("name")))
    return sorted(clips)


def live_clips(track):
    return sorted((c.start_time, c.end_time - c.start_time,
                   sorted((n.pitch, n.start_time, n.duration) for n in c._notes), c.name)
                  for c in track.arrangement_clips)


def same_clips(track):
    return [(s, d, n) for s, d, n, _ in bw_clips(track.name)] == [(s, d, n) for s, d, n, _ in live_clips(track)]


def close_notes(track):
    """REAPER と Live のノートが同じ（REAPER の丸め 1 目盛りくらいの差は同じとみなす）"""
    ours = [c[2] for c in bw_clips(track.name)]
    theirs = [c[2] for c in live_clips(track)]
    if len(ours) != len(theirs):
        return False
    for a, b in zip(ours, theirs):
        if len(a) != len(b):
            return False
        for x, y in zip(sorted(a), sorted(b)):
            if x[0] != y[0] or abs(x[1] - y[1]) > 0.002 or abs(x[2] - y[2]) > 0.002:
                return False
    return True


def names():
    return [t.name for t in song.tracks]


# ---------------------------------------------------------------- 準備

def main():
    os.makedirs(LOGS, exist_ok=True)
    os.makedirs(BW_DIR, exist_ok=True)
    open(os.path.join(IPC, "debug"), "w").close()
    if os.path.exists(STATE):
        os.remove(STATE)

    build = subprocess.run(["dotnet", "build", os.path.join(ROOT, "src", "DawSync.Cli"), "-v", "q", "-nologo"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
    if build.returncode != 0:
        print(build.stdout[-3000:])
        sys.exit(1)

    def env(name):
        # TRACE=1 で、アプリがやりとりした変更をそのまま logs/trace_<名前>.jsonl に書き出す（調査用）
        if os.environ.get("TRACE") != "1":
            return None
        path = os.path.join(LOGS, "trace_%s.jsonl" % name)
        if os.path.exists(path):
            os.remove(path)
        return dict(os.environ, DAWSYNC_TRACE=path)

    host = subprocess.Popen([CLI, "session", "--host", "--name", "Live", "--bridge-port", "47410", "--port", "47411",
                             "--key", "BWTEST", "--samples", os.path.join(LOGS, "samples_live")],
                            stdout=open(os.path.join(LOGS, "app_live.txt"), "w"), stderr=subprocess.STDOUT, env=env("live"))
    procs = [host]
    try:
        time.sleep(2.0)
        run_until(lambda: script._connected, 5)
        run_until(lambda: False, 1.5)
        # REAPER のスクリプトをテスト用のポートにつながせる（47400 は本物のアプリ用）
        with open(PORT_FILE, "w") as f:
            f.write("%d %d" % (TEST_PORT, int((time.time() + 3600) * 1000)))
        room = "127.0.0.1:47411"
        if os.environ.get("LATENCY_MS"):
            # Wi-Fi・インターネット越しのような遅延のある中継を通してつなぐ
            procs.append(subprocess.Popen([sys.executable, os.path.join(ROOT, "tests", "sim", "latency_proxy.py"), "47412", "47411",
                                           os.environ["LATENCY_MS"], os.environ.get("JITTER_MS", "0")]))
            room = "127.0.0.1:47412"
            time.sleep(0.5)
        bitwig = subprocess.Popen([CLI, "session", "--join", room, "--key", "BWTEST", "--name", "REAPER",
                                   "--bridge-port", str(TEST_PORT),
                                   "--samples", os.path.join(LOGS, "samples_reaper"), "--blank"],
                                  stdout=open(os.path.join(LOGS, "app_reaper.txt"), "w"), stderr=subprocess.STDOUT, env=env("reaper"))
        procs.append(bitwig)
        run_tests()
        if "--watch" in sys.argv:
            watch(float(sys.argv[sys.argv.index("--watch") + 1]))
    finally:
        for p in procs:
            p.kill()
        if os.path.exists(PORT_FILE):
            os.remove(PORT_FILE)
    failed = [n for n, ok in results if not ok]
    print("\n%d/%d passed" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


def run_tests():
    check("REAPER が参加して、トラックがそろう", lambda: bw_names() == ["Bass", "Keys", "Drums"], 30)
    check("トラックの種類", lambda: [t[2] for t in bw_tracks()] == ["midi", "midi", "audio"])
    check("テンポ", lambda: bw()["tempo"] == 128.0)
    check("アレンジャーのクリップとノート", lambda: same_clips(song.tracks[0]))
    check("クリップの名前", lambda: bw_clips("Bass")[0][3] == "Bassline")
    check("和音（同じ位置のノート）", lambda: same_clips(song.tracks[1]) and len(bw_clips("Keys")[0][2]) == 3)
    before = live_clips(song.tracks[0])
    check("REAPER から送り返されて Live が変わったりしない", lambda: live_clips(song.tracks[0]) == before, 3)

    song.tempo = 96.0
    check("テンポを変える", lambda: bw()["tempo"] == 96.0)
    song.signature_numerator = 3
    check("拍子を変える", lambda: bw()["sig"] == [3, 4])

    song.tracks[0].arrangement_clips[0].user_add_note(47, 4.0)
    check("ノートを足す", lambda: same_clips(song.tracks[0]))

    clip = song.tracks[0].arrangement_clips[0]
    clip._notes = [n for n in clip._notes if n.pitch != 43]
    clip._notify("notes")
    check("ノートを消す", lambda: same_clips(song.tracks[0]))

    # 弾いて入れたような、目盛りに乗っていないノート（REAPER は 1 拍 960 分割に丸めるので、少しずれる）
    clip = song.tracks[0].arrangement_clips[0]
    clip.user_add_note(50, 1.2345678, 0.3333333)
    clip.user_add_note(52, 2.7182818, 0.1415926)
    check("目盛りに乗っていないノート", lambda: close_notes(song.tracks[0]))
    for step in range(4):  # 上下の矢印で 1 つずつ動かす
        for n in clip._notes:
            if n.pitch in (50 + step, 52 + step) and n.start_time in (1.2345678, 2.7182818):
                n.pitch += 1
        clip._notify("notes")
        run_until(lambda: False, 0.15)
    check("目盛りに乗っていないノートを上下に動かしても、REAPER で複製にならない", lambda: close_notes(song.tracks[0]), 8)
    for n in clip._notes:
        if n.start_time == 1.2345678:
            n.start_time = 1.7345678
    clip._notify("notes")
    check("目盛りに乗っていないノートを左右に動かしても、REAPER で複製にならない", lambda: close_notes(song.tracks[0]), 8)
    clip._notes = [n for n in clip._notes if n.pitch not in (54, 56)]
    clip._notify("notes")
    check("目盛りに乗っていないノートを消せる", lambda: close_notes(song.tracks[0]) and same_clips(song.tracks[0]), 8)

    clip = song.tracks[0].arrangement_clips[0]
    clip.start_time, clip.end_time = 12.0, 20.0
    check("クリップを動かす", lambda: same_clips(song.tracks[0]) and [c[0] for c in bw_clips("Bass")] == [12.0])

    clip = song.tracks[0].arrangement_clips[0]
    clip.end_time = 24.0
    check("クリップの長さを変える", lambda: bw_clips("Bass")[0][1] == 12.0)

    new = song.tracks[1].create_midi_clip(16.0, 4.0)
    new.name = "Stab"
    new.user_add_note(72, 0.0)
    check("クリップを作る", lambda: same_clips(song.tracks[1]))

    song.tracks[1].delete_clip(song.tracks[1].arrangement_clips[-1])
    check("クリップを消す", lambda: same_clips(song.tracks[1]) and len(bw_clips("Keys")) == 1)

    # 分割（前と後ろの 2 つになり、後ろは中身の開始位置がずれる）
    bass = song.tracks[0]
    clip = bass.arrangement_clips[0]
    bass.user_split(clip, clip.start_time + 4.0)
    if not check("Live でクリップを分割する → REAPER",
                 lambda: same_clips(bass) and len(bw_clips("Bass")) == 2):
        print("     Live:   %s" % [(a, b, n) for a, b, n, _ in live_clips(bass)])
        print("     REAPER: %s" % [(a, b, n) for a, b, n, _ in bw_clips("Bass")])
        print("     REAPER p: %s" % [v.get("p") for k, v in sorted(bw().items()) if k.startswith("c/a/%s/" % bw_id("Bass")) and v])
    bass.delete_clip(bass.arrangement_clips[1])
    check("分割した後ろを消す → REAPER", lambda: same_clips(bass) and len(bw_clips("Bass")) == 1)

    # ループしているクリップ（4 拍のループを 16 拍に伸ばした）と、頭を詰めたクリップ（開始位置が 1 拍目）
    keys = song.tracks[1]
    loop = keys.create_midi_clip(32.0, 16.0)
    loop.user_add_note(60, 0.0)
    loop.user_add_note(64, 2.0)
    loop.loop_end = 4.0
    trimmed = keys.create_midi_clip(52.0, 4.0)
    trimmed.user_add_note(62, 1.5)
    trimmed.user_add_note(65, 3.0)
    trimmed.looping = False
    trimmed.start_marker = 1.0
    trimmed.end_marker = 5.0

    def bw_clip(track_name, start):
        return bw()["c/a/%s/%s" % (bw_id(track_name), start)]
    check("ループしているクリップ（REAPER でも繰り返す）",
          lambda: bw_clip("Keys", 32)["p"]["looping"] is True and bw_clip("Keys", 32)["p"]["le"] == 4
          and bw_clip("Keys", 32)["dur"] == 16 and same_clips(keys))
    check("頭を詰めたクリップ（中身の開始位置がずれない）",
          lambda: bw_clip("Keys", 52)["p"]["sm"] == 1 and same_clips(keys))
    before = [(c.start_time, c.end_time, c.looping, c.loop_start, c.loop_end, c.start_marker) for c in keys.arrangement_clips]
    check("REAPER から送り返されて、ループや開始位置が変わったりしない",
          lambda: [(c.start_time, c.end_time, c.looping, c.loop_start, c.loop_end, c.start_marker) for c in keys.arrangement_clips] == before, 3)
    keys.delete_clip(loop)
    keys.delete_clip(trimmed)
    run_until(lambda: same_clips(keys), 5)

    song.tracks[1].name = "Piano"
    check("トラック名", lambda: bw_names() == ["Bass", "Piano", "Drums"])
    song.tracks[1].mute = True
    check("ミュートは同期しない（各自のもの）", lambda: "t/%s/mute" % bw_id("Piano") not in bw(), 3)
    song.tracks[1].color = 0x3366CC
    check("トラックの色", lambda: bw().get("t/%s/color" % bw_id("Piano")) == 0x3366CC)

    song.create_midi_track(1)
    song.tracks[1].name = "Pad"
    check("トラックを追加（同じ位置に）", lambda: bw_names() == ["Bass", "Pad", "Piano", "Drums"])

    song.delete_track(1)
    check("トラックを消す", lambda: bw_names() == ["Bass", "Piano", "Drums"])

    song.tracks = [song.tracks[1], song.tracks[0], song.tracks[2]]
    check("トラックの並べ替え", lambda: bw_names() == ["Piano", "Bass", "Drums"])
    song.tracks = [song.tracks[1], song.tracks[0], song.tracks[2]]
    check("トラックの並べ替え（戻す）", lambda: bw_names() == ["Bass", "Piano", "Drums"] and same_clips(song.tracks[0]))

    group = song.user_group([song.tracks[0], song.tracks[1]], "Band")
    check("グループを作る（REAPER ではフォルダになる）",
          lambda: [(t[1], t[3] is not None) for t in bw_tracks()] == [("Band", False), ("Bass", True), ("Piano", True), ("Drums", False)], 8)

    song.user_ungroup(group)
    check("グループを解除する", lambda: [(t[1], t[3]) for t in bw_tracks()] == [("Bass", None), ("Piano", None), ("Drums", None)], 8)

    # ---- REAPER 側の編集が Live に届く（拡張のテスト用コマンドで、REAPER の中からクリップを触る）
    bass = song.tracks[0]
    start = bass.arrangement_clips[0].start_time
    bw_cmd("clip-dur Bass %g 16" % start)
    check("REAPER でクリップの長さを変える → Live", lambda: [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(start, start + 16)])
    bw_cmd("clip-move Bass %g 20" % start)
    check("REAPER でクリップを動かす → Live（切れ端なし）",
          lambda: [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(20.0, 36.0)] and same_clips(bass))
    bw_cmd("clip-note Bass 20 55 1 0.5")
    check("REAPER でノートを足す → Live", lambda: (55, 1.0, 0.5) in live_clips(bass)[0][2])

    # 同じクリップに、同時に別々のノートを足しても両方残る（相手の古い値で消されない）
    bass.arrangement_clips[0].user_add_note(62, 3.0)
    bw_cmd("clip-note Bass 20 64 3.5 0.5", wait=False)
    check("同時に別々のノートを足しても、両方残る（Live と REAPER）",
          lambda: same_clips(bass) and {(62, 3.0, 0.5), (64, 3.5, 0.5)} <= set(live_clips(bass)[0][2]), 8)
    bass.arrangement_clips[0].name = "Riff"
    bw_cmd("clip-note Bass 20 66 5 0.5", wait=False)
    if not check("名前の変更とノートの追加を同時にしても、どちらも消えない（Live と REAPER）",
                 lambda: same_clips(bass) and bw_clips("Bass")[0][3] == "Riff" and (66, 5.0, 0.5) in live_clips(bass)[0][2], 8):
        print("     Live:   %s" % [(a, b, len(n), nm) for a, b, n, nm in live_clips(bass)])
        print("     REAPER: %s" % [(a, b, len(n), nm) for a, b, n, nm in bw_clips("Bass")])

    # 同じクリップを同時に編集しても、最後は両方同じになる
    bass.arrangement_clips[0].user_add_note(60, 2.0)
    bw_cmd("clip-dur Bass 20 12", wait=False)
    check("同じクリップを同時に編集しても、両方同じ状態になる", lambda: same_clips(bass) and settled(), 10)

    # でたらめな編集を両方で同時に続けても、最後は同じ状態になる
    for seed in [int(x) for x in os.environ.get("FUZZ_SEEDS", "1,2,3").split(",")]:
        fuzz(40, seed)
        ok = check("でたらめな同時編集のあと、両方同じ状態になる（%d 回目）" % seed, lambda: converged() and settled(2.0), 20)
        if not ok:
            for t in song.tracks:
                if t.has_midi_input and not same_clips(t):
                    print("     %s Live:   %s" % (t.name, [(a, b, len(n)) for a, b, n, _ in live_clips(t)]))
                    print("     %s REAPER: %s" % (t.name, [(a, b, len(n)) for a, b, n, _ in bw_clips(t.name)]))
                    for (a, _, ln, _), (_, _, bn, _) in zip(live_clips(t), bw_clips(t.name)):
                        if ln != bn:
                            print("       @%s Live だけ: %s / REAPER だけ: %s" % (a, [x for x in ln if x not in bn or ln.count(x) > bn.count(x)],
                                                                          [x for x in bn if x not in ln]))
            print("     tempo Live %s REAPER %s" % (song.tempo, bw()["tempo"]))

    # 何もしていないときに、どちらも勝手に送らない（送り合いが続くと、相手の編集を上書きしてしまう）
    run_until(lambda: False, 2.0)
    counts = self_ops()
    run_until(lambda: False, 5.0)
    check("何もしていないときは、どちらからも何も送られない", lambda: self_ops() == counts, 1)

    # バウンスしたオーディオ（ファイルは相手の PC に送られ、そこのパスでクリップになる）
    tone = os.path.join(LOGS, "tone.wav")
    write_tone(tone)
    song.tracks[2].create_audio_clip(tone, 4.0)

    def audio_ok():
        s = bw()
        tid = bw_id("Drums")
        v = s.get("c/a/%s/4" % tid)
        return v is not None and v["k"] == "audio" and v["file"] and os.path.isfile(v["file"]) and v["file"] != tone
    check("オーディオクリップ（ファイルごと届く）", audio_ok, 15)

    def bw_audio():
        return bw()["c/a/%s/4" % bw_id("Drums")]["p"]

    live_audio = song.tracks[2].arrangement_clips[0]
    check("ワープマーカー（REAPER のストレッチマーカーになる）",
          lambda: bw_audio().get("wmk") == [[m.beat_time, m.sample_time] for m in live_audio.warp_markers])
    live_audio.add_warp_marker(Live.Clip.WarpMarker(sample_time=1.2, beat_time=2.0))
    check("ワープマーカーを足す", lambda: bw_audio().get("wmk") == [[0, 0], [2, 1.2], [4, 2]])
    live_audio.warping = False
    check("ワープをオフ", lambda: bw_audio()["warp"] is False)
    live_audio.warping = True
    check("ワープをオン（マーカーも戻る）", lambda: bw_audio()["warp"] is True and len(bw_audio().get("wmk") or []) == 3)
    live_audio.pitch_coarse = -5
    live_audio.pitch_fine = 30.0
    check("ピッチ（半音・セント）", lambda: bw_audio()["pc"] == -5 and bw_audio()["pf"] == 30)
    before = (live_audio.pitch_coarse, live_audio.pitch_fine, live_audio.warping,
              [(m.beat_time, m.sample_time) for m in live_audio.warp_markers])
    check("REAPER から送り返されて Live のオーディオが変わったりしない",
          lambda: (live_audio.pitch_coarse, live_audio.pitch_fine, live_audio.warping,
                   [(m.beat_time, m.sample_time) for m in live_audio.warp_markers]) == before, 3)


def fuzz(rounds=40, seed=1):
    """Live と REAPER の両方で、でたらめな編集を同時に続け、最後に両方が同じ状態になるか確かめる。"""
    import random
    rnd = random.Random(seed)
    midi_tracks = [t for t in song.tracks if t.has_midi_input]
    for _ in range(rounds):
        side = rnd.random()
        track = rnd.choice(midi_tracks)
        clips = track.arrangement_clips
        if side < 0.5:
            op = rnd.choice(["note", "unnote", "move", "resize", "new", "tempo"])
            try:
                if op == "note" and clips:
                    rnd.choice(clips).user_add_note(rnd.randint(40, 80), rnd.randint(0, 7) * 0.5)
                elif op == "unnote" and clips:
                    c = rnd.choice(clips)
                    if c._notes:
                        c._notes.pop(rnd.randrange(len(c._notes)))
                        c._notify("notes")
                elif op == "move" and clips:
                    # 本物の Live と同じく、ほかのクリップとは重ならない（重なる移動はしない）
                    c = rnd.choice(clips)
                    delta = rnd.choice([-4.0, 4.0, 8.0])
                    s2, e2 = c.start_time + delta, c.end_time + delta
                    if s2 >= 0 and not any(o is not c and o.start_time < e2 and o.end_time > s2 for o in clips):
                        c.start_time, c.end_time = s2, e2
                elif op == "resize" and clips:
                    c = rnd.choice(clips)
                    e2 = max(c.start_time + 1.0, c.end_time + rnd.choice([-2.0, 2.0]))
                    if not any(o is not c and o.start_time < e2 and o.end_time > c.start_time for o in clips):
                        c.end_time = e2
                elif op == "new":
                    start = rnd.randint(0, 12) * 4.0
                    if not any(c.start_time < start + 4 and c.end_time > start for c in clips):
                        track.create_midi_clip(start, 4.0).user_add_note(60, 0.0)
                elif op == "tempo":
                    song.tempo = float(rnd.choice([90, 100, 110, 120]))
            except RuntimeError:
                pass
        else:
            try:
                s = bw()
                tid = bw_id(track.name)
            except Exception:
                continue
            starts = [float(k.split("/")[3]) for k, v in s.items() if k.startswith("c/a/%s/" % tid) and v]
            if not starts:
                continue
            start = rnd.choice(starts)
            op = rnd.choice(["clip-note", "clip-dur", "clip-move"])
            if op == "clip-note":
                bw_cmd("clip-note %s %g %d %g 0.5" % (track.name, start, rnd.randint(40, 80), rnd.randint(0, 7) * 0.5), wait=False)
            elif op == "clip-dur":
                dur = rnd.choice([2.0, 4.0, 6.0, 8.0])
                if all(x <= start or x >= start + dur for x in starts if x != start):
                    bw_cmd("clip-dur %s %g %g" % (track.name, start, dur), wait=False)
            else:
                new = start + rnd.choice([-4.0, 4.0, 8.0])
                others = [x for x in starts if x != start]
                if new >= 0 and all(abs(x - new) >= 8 for x in others):
                    bw_cmd("clip-move %s %g %g" % (track.name, start, new), wait=False)
        run_until(lambda: False, rnd.choice([0.05, 0.2, 0.5]))


def converged():
    s = bw()
    if s["tempo"] != song.tempo or bw_names() != names():
        return False
    return all(same_clips(t) for t in song.tracks if t.has_midi_input)


LUA_FIND = """
local function find(name, qn)
  for i = 0, reaper.CountTracks(0) - 1 do
    local tr = reaper.GetTrack(0, i)
    local _, n = reaper.GetSetMediaTrackInfo_String(tr, "P_NAME", "", false)
    if n == name then
      for j = 0, reaper.CountTrackMediaItems(tr) - 1 do
        local it = reaper.GetTrackMediaItem(tr, j)
        if math.abs(reaper.TimeMap2_timeToQN(0, reaper.GetMediaItemInfo_Value(it, "D_POSITION")) - qn) < 1e-6 then return it end
      end
    end
  end
  error("no item at " .. qn)
end
reaper.Undo_BeginBlock2(0)
"""


def bw_cmd(*lines, wait=True):
    """REAPER の中でクリップ（アイテム）を触る（テスト用。REAPER のテストと同じ書き方のコマンドを Lua にする）。"""
    code = [LUA_FIND]
    for line in lines:
        a = line.split()
        name, start = a[1], float(a[2])
        if a[0] == "clip-dur":
            code.append('local it = find("%s", %r); local s = reaper.GetMediaItemInfo_Value(it, "D_POSITION")\n'
                        'reaper.SetMediaItemInfo_Value(it, "D_LENGTH", reaper.TimeMap2_QNToTime(0, %r + %r) - s)'
                        % (name, start, start, float(a[3])))
        elif a[0] == "clip-move":
            code.append('local it = find("%s", %r); reaper.SetMediaItemInfo_Value(it, "D_POSITION", reaper.TimeMap2_QNToTime(0, %r))'
                        % (name, start, float(a[3])))
        elif a[0] == "clip-note":
            code.append('local it = find("%s", %r); local take = reaper.GetActiveTake(it)\n'
                        'local base = reaper.MIDI_GetProjQNFromPPQPos(take, 0)\n'
                        'reaper.MIDI_InsertNote(take, false, false, reaper.MIDI_GetPPQPosFromProjQN(take, base + %r),'
                        ' reaper.MIDI_GetPPQPosFromProjQN(take, base + %r + %r), 0, %d, 100, false)'
                        % (name, start, float(a[4]), float(a[4]), float(a[5]), int(a[3])))
    code.append('reaper.Undo_EndBlock2(0, "test", -1)\nreaper.UpdateArrange()')
    path = os.path.join(BW_DIR, "cmd.lua")
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        f.write("\n".join(code) + "\n")
    os.replace(path + ".tmp", path)
    if wait:
        run_until(lambda: not os.path.exists(path), 5)


def self_ops():
    """アプリのログで、自分の DAW から送った変更の行数（Live 側, REAPER 側）。"""
    def count(name):
        with open(os.path.join(LOGS, name), encoding="utf-8", errors="replace") as f:
            return sum(1 for line in f if "Self" in line)
    return count("app_live.txt"), count("app_reaper.txt")


_last_ops = [None, 0.0]


def settled(seconds=1.5):
    """送られる変更が seconds 秒のあいだ増えていない。"""
    now = self_ops()
    if now != _last_ops[0]:
        _last_ops[0], _last_ops[1] = now, time.time()
        return False
    return time.time() - _last_ops[1] >= seconds


def write_tone(path, seconds=2.0, rate=44100):
    import math
    import struct
    import wave
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * 220 * i / rate)))
                               for i in range(int(seconds * rate))))


def watch(seconds):
    print("\n%d 秒間つないだままにします。REAPER を操作すると logs/live_state.json に Live 側の状態が出ます" % seconds, flush=True)
    end = time.time() + seconds
    last = None
    command_file = os.path.join(LOGS, "live_cmd.py")
    while time.time() < end:
        tick()
        # 手で確かめる用: logs/live_cmd.py を置くと、Live 側の操作として実行する（song, Live が使える）
        if os.path.exists(command_file):
            with open(command_file, encoding="utf-8") as f:
                code = f.read()
            os.remove(command_file)
            try:
                exec(code, {"song": song, "Live": Live, "script": script})
                print("live_cmd: ok", flush=True)
            except Exception as e:
                print("live_cmd: " + repr(e), flush=True)
        state = {
            "tempo": song.tempo,
            "sig": [song.signature_numerator, song.signature_denominator],
            "tracks": [{"name": t.name, "kind": "group" if t.is_foldable else ("midi" if t.has_midi_input else "audio"),
                        "group": t.group_track.name if t.group_track is not None else None, "mute": t.mute,
                        "clips": [[c.start_time, c.end_time, c.name, sorted([n.pitch, n.start_time, n.duration, n.velocity]
                                                                            for n in c._notes), c.file_path]
                                  + ([{"pc": c.pitch_coarse, "pf": c.pitch_fine, "warp": c.warping, "wm": c.warp_mode,
                                       "wmk": [[m.beat_time, m.sample_time] for m in c.warp_markers]}] if c.is_audio_clip else [])
                                  for c in t.arrangement_clips]}
                       for t in song.tracks],
            "warnings": [],
        }
        text = json.dumps(state, ensure_ascii=False, indent=1)
        if text != last:
            with open(os.path.join(LOGS, "live_state.json"), "w", encoding="utf-8") as f:
                f.write(text)
            last = text
        time.sleep(0.05)


if __name__ == "__main__":
    main()
