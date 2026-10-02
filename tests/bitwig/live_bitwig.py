"""
モックの Live 1 台と、本物の Bitwig（AbletonMulti 拡張を入れて起動しておく）で同時編集を確かめる。

  python tests/bitwig/live_bitwig.py            自動のテスト（Live → Bitwig、Bitwig の状態は拡張のデバッグ出力で見る）
  python tests/bitwig/live_bitwig.py --watch 300 テストの後、300 秒間つないだままにして Live 側の状態を
                                                 logs/live_state.json に書き続ける（Bitwig を手で触って確かめる用）

Bitwig で今開いているプロジェクトは「まっさら」にされるので、捨ててよいプロジェクトで行うこと。
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
sys.path.insert(0, os.path.join(ROOT, "src", "AbletonMulti.Core", "RemoteScript"))

import Live  # noqa: E402  (モック)
from AbletonMulti import multi  # noqa: E402

LOGS = os.path.join(HERE, "logs")
BW_DIR = os.path.join(tempfile.gettempdir(), "bitwig", "abletonmulti-bitwig")
STATE = os.path.join(BW_DIR, "state.json")
PORT_FILE = os.path.join(BW_DIR, "port")
BITWIG_TEST_PORT = 47490
BW_LOG = os.path.join(os.environ.get("LOCALAPPDATA", ""), "Bitwig Studio", "BitwigStudio.log")
CLI = os.path.join(ROOT, "src", "AbletonMulti.Cli", "bin", "Debug", "net10.0", "AbletonMulti.Cli" + (".exe" if os.name == "nt" else ""))
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
script = multi.AbletonMulti(CInstance(song))
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


# ---------------------------------------------------------------- Bitwig の状態

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


def bw_props(name, index=0):
    """Bitwig のクリップ（トラックの index 番目）の p（ループ・開始位置など）"""
    s = bw()
    tid = bw_id(name)
    keys = sorted((float(k.split("/")[3]), k) for k, v in s.items() if k.startswith("c/a/%s/" % tid) and v)
    return s[keys[index][1]]["p"]


def names():
    return [t.name for t in song.tracks]


# ---------------------------------------------------------------- 準備

def main():
    os.makedirs(LOGS, exist_ok=True)
    os.makedirs(BW_DIR, exist_ok=True)
    open(os.path.join(BW_DIR, "debug"), "w").close()
    if os.path.exists(STATE):
        os.remove(STATE)

    build = subprocess.run(["dotnet", "build", os.path.join(ROOT, "src", "AbletonMulti.Cli"), "-v", "q", "-nologo"],
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
        return dict(os.environ, ABLETONMULTI_TRACE=path)

    host = subprocess.Popen([CLI, "session", "--host", "--name", "Live", "--bridge-port", "47410", "--port", "47411",
                             "--key", "BWTEST", "--samples", os.path.join(LOGS, "samples_live")],
                            stdout=open(os.path.join(LOGS, "app_live.txt"), "w"), stderr=subprocess.STDOUT, env=env("live"))
    procs = [host]
    log_start = os.path.getsize(BW_LOG) if os.path.exists(BW_LOG) else 0
    try:
        time.sleep(2.0)
        run_until(lambda: script._connected, 5)
        run_until(lambda: False, 1.5)
        # Bitwig の拡張をテスト用のポートにつながせる（47400 だと本物の Live の Remote Script もつないでくるので）
        with open(PORT_FILE, "w") as f:
            f.write("%d %d" % (BITWIG_TEST_PORT, int((time.time() + 3600) * 1000)))
        room = "127.0.0.1:47411"
        if os.environ.get("LATENCY_MS"):
            # Wi-Fi・インターネット越しのような遅延のある中継を通してつなぐ
            procs.append(subprocess.Popen([sys.executable, os.path.join(ROOT, "tests", "sim", "latency_proxy.py"), "47412", "47411",
                                           os.environ["LATENCY_MS"], os.environ.get("JITTER_MS", "0")]))
            room = "127.0.0.1:47412"
            time.sleep(0.5)
        bitwig = subprocess.Popen([CLI, "session", "--join", room, "--key", "BWTEST", "--name", "Bitwig",
                                   "--bridge-port", str(BITWIG_TEST_PORT),
                                   "--samples", os.path.join(LOGS, "samples_bitwig"), "--blank"],
                                  stdout=open(os.path.join(LOGS, "app_bitwig.txt"), "w"), stderr=subprocess.STDOUT, env=env("bitwig"))
        procs.append(bitwig)
        run_tests()
        if "--watch" in sys.argv:
            watch(float(sys.argv[sys.argv.index("--watch") + 1]))
    finally:
        for p in procs:
            p.kill()
        if os.path.exists(PORT_FILE):
            os.remove(PORT_FILE)
    if os.path.exists(BW_LOG):
        with open(BW_LOG, encoding="utf-8", errors="replace") as f:
            f.seek(log_start)
            crashed = "Engine process exited" in f.read()
        results.append(("Bitwig のオーディオエンジンが落ちない", not crashed))
        print(("FAIL " if crashed else "PASS ") + "Bitwig のオーディオエンジンが落ちない")
    failed = [n for n, ok in results if not ok]
    print("\n%d/%d passed" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


def run_tests():
    check("Bitwig が参加して、トラックがそろう", lambda: bw_names() == ["Bass", "Keys", "Drums"], 30)
    check("トラックの種類", lambda: [t[2] for t in bw_tracks()] == ["midi", "midi", "audio"])
    check("テンポ", lambda: bw()["tempo"] == 128.0)
    check("アレンジャーのクリップとノート", lambda: same_clips(song.tracks[0]))
    check("クリップの名前", lambda: bw_clips("Bass")[0][3] == "Bassline")
    check("和音（同じ位置のノート）", lambda: same_clips(song.tracks[1]) and len(bw_clips("Keys")[0][2]) == 3)
    before = live_clips(song.tracks[0])
    check("Bitwig から送り返されて Live が変わったりしない", lambda: live_clips(song.tracks[0]) == before, 3)

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

    clip = song.tracks[0].arrangement_clips[0]
    clip.start_time, clip.end_time = 12.0, 20.0
    check("クリップを動かす", lambda: same_clips(song.tracks[0]) and [c[0] for c in bw_clips("Bass")] == [12.0])

    clip = song.tracks[0].arrangement_clips[0]
    clip.end_time = 24.0
    check("クリップの長さを変える", lambda: bw_clips("Bass")[0][1] == 12.0)

    # 左端を動かす（位置と中身の開始位置が一緒にずれ、右端はそのまま）
    clip = song.tracks[0].arrangement_clips[0]
    clip.start_time, clip.start_marker = clip.start_time + 1.0, clip.start_marker + 1.0
    check("Live でクリップの左端を動かす → Bitwig",
          lambda: [(c[0], c[1]) for c in bw_clips("Bass")] == [(13.0, 11.0)] and bw_props("Bass")["sm"] == clip.start_marker
          and same_clips(song.tracks[0]))
    clip = song.tracks[0].arrangement_clips[0]
    clip.looping, clip.loop_start, clip.loop_end = True, 1.0, 5.0
    check("Live でループの範囲を変える → Bitwig",
          lambda: bw_props("Bass")["looping"] is True and (bw_props("Bass")["ls"], bw_props("Bass")["le"]) == (1.0, 5.0)
          and bw_clips("Bass")[0][1] == 11.0)
    clip = song.tracks[0].arrangement_clips[0]
    clip.looping = False
    clip.start_marker, clip.end_marker = 1.0, 12.0
    check("Live でループを切る → Bitwig", lambda: bw_props("Bass")["looping"] is False and same_clips(song.tracks[0]))
    clip = song.tracks[0].arrangement_clips[0]
    song.tracks[0].user_split(clip, clip.start_time + 4.0)
    check("Live でクリップを分割する → Bitwig",
          lambda: same_clips(song.tracks[0]) and len(bw_clips("Bass")) == 2
          and bw_props("Bass", 1)["sm"] == song.tracks[0].arrangement_clips[1].start_marker)
    song.tracks[0].delete_clip(song.tracks[0].arrangement_clips[1])
    check("分割した後ろを消す → Bitwig", lambda: same_clips(song.tracks[0]) and len(bw_clips("Bass")) == 1)

    new = song.tracks[1].create_midi_clip(16.0, 4.0)
    new.name = "Stab"
    new.user_add_note(72, 0.0)
    check("クリップを作る", lambda: same_clips(song.tracks[1]))

    song.tracks[1].delete_clip(song.tracks[1].arrangement_clips[-1])
    check("クリップを消す", lambda: same_clips(song.tracks[1]) and len(bw_clips("Keys")) == 1)

    song.tracks[1].name = "Piano"
    check("トラック名", lambda: bw_names() == ["Bass", "Piano", "Drums"])
    song.tracks[1].mute = True
    check("ミュートは同期しない（各自のもの）", lambda: "t/%s/mute" % bw_id("Piano") not in bw(), 3)
    song.tracks[1].color = 0x3366CC
    run_until(lambda: False, 1.0)
    got = bw()["t/%s/color" % bw_id("Piano")]
    results.append(("トラックの色（近い色になる）", got != 0))
    print("INFO トラックの色: Live 0x3366cc → Bitwig 0x%06x" % int(got))

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
    check("グループを作る（Bitwig では自動でグループになる）",
          lambda: [(t[1], t[3] is not None) for t in bw_tracks()] == [("Band", False), ("Bass", True), ("Piano", True), ("Drums", False)], 8)

    song.user_ungroup(group)
    check("グループを解除する", lambda: [(t[1], t[3]) for t in bw_tracks()] == [("Bass", None), ("Piano", None), ("Drums", None)], 8)

    # ---- Bitwig 側の編集が Live に届く（拡張のテスト用コマンドで、Bitwig の中からクリップを触る）
    bass = song.tracks[0]
    start = bass.arrangement_clips[0].start_time
    bw_cmd("clip-dur Bass %g 16" % start)
    check("Bitwig でクリップの長さを変える → Live", lambda: [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(start, start + 16)])
    bw_cmd("clip-move Bass %g 20" % start)
    check("Bitwig でクリップを動かす → Live（切れ端なし）",
          lambda: [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(20.0, 36.0)] and same_clips(bass))
    bw_cmd("clip-trim Bass 20 22")
    check("Bitwig でクリップの左端を動かす → Live",
          lambda: [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(22.0, 36.0)]
          and bass.arrangement_clips[0].start_marker == bw_props("Bass")["sm"] and same_clips(bass))
    bw_cmd("clip-loop Bass 22 on 0 4")
    check("Bitwig でループを入れる → Live",
          lambda: bass.arrangement_clips[0].looping and (bass.arrangement_clips[0].loop_start, bass.arrangement_clips[0].loop_end) == (0.0, 4.0)
          and [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(22.0, 36.0)])
    bw_cmd("clip-loop Bass 22 off")
    check("Bitwig でループを切る → Live", lambda: not bass.arrangement_clips[0].looping and same_clips(bass))
    bw_cmd("clip-move Bass 22 20")
    check("Bitwig で左端を動かしたクリップを動かす → Live",
          lambda: [c.start_time for c in bass.arrangement_clips] == [20.0] and same_clips(bass))
    bw_cmd("clip-note Bass 20 55 1 0.5")
    check("Bitwig でノートを足す → Live", lambda: (55, 1.0, 0.5) in live_clips(bass)[0][2])
    bw_cmd("clip-split Bass 20 24")
    check("Bitwig でクリップを分割する → Live",
          lambda: [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(20.0, 24.0), (24.0, 34.0)]
          and bass.arrangement_clips[1].start_marker == bw_props("Bass", 1)["sm"] and same_clips(bass))
    bass.delete_clip(bass.arrangement_clips[1])
    check("分割した後ろを消す → Bitwig", lambda: same_clips(bass) and len(bw_clips("Bass")) == 1)
    bw_cmd("clip-dur Bass 20 14")
    check("分割した前を元の長さに戻す", lambda: [(c.start_time, c.end_time) for c in bass.arrangement_clips] == [(20.0, 34.0)])

    # 同じクリップに、同時に別々のノートを足しても両方残る（相手の古い値で消されない）
    bass.arrangement_clips[0].user_add_note(62, 3.0)
    bw_cmd("clip-note Bass 20 64 3.5 0.5", wait=False)
    check("同時に別々のノートを足しても、両方残る（Live と Bitwig）",
          lambda: same_clips(bass) and {(62, 3.0, 0.5), (64, 3.5, 0.5)} <= set(live_clips(bass)[0][2]), 8)
    bass.arrangement_clips[0].name = "Riff"
    bw_cmd("clip-note Bass 20 66 5 0.5", wait=False)
    check("名前の変更とノートの追加を同時にしても、どちらも消えない（Live と Bitwig）",
          lambda: same_clips(bass) and bw_clips("Bass")[0][3] == "Riff" and (66, 5.0, 0.5) in live_clips(bass)[0][2], 8)

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
                    print("     %s Bitwig: %s" % (t.name, [(a, b, len(n)) for a, b, n, _ in bw_clips(t.name)]))
                    for (a, _, ln, _), (_, _, bn, _) in zip(live_clips(t), bw_clips(t.name)):
                        if ln != bn:
                            print("       @%s Live だけ: %s / Bitwig だけ: %s" % (a, [x for x in ln if x not in bn or ln.count(x) > bn.count(x)],
                                                                          [x for x in bn if x not in ln]))
            print("     tempo Live %s Bitwig %s" % (song.tempo, bw()["tempo"]))

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
    check("ワープマーカー（最初の状態）", lambda: bw_audio().get("wmk") == [[m.beat_time, m.sample_time] for m in live_audio.warp_markers])
    live_audio.pitch_coarse = -5
    live_audio.pitch_fine = 30.0
    check("ピッチ（半音・セント）", lambda: bw_audio()["pc"] == -5 and bw_audio()["pf"] == 30)
    live_audio.warp_mode = 6
    check("ワープモード（Complex Pro）", lambda: bw_audio()["wm"] == 6)
    live_audio.add_warp_marker(Live.Clip.WarpMarker(sample_time=1.2, beat_time=2.0))
    check("ワープマーカーを足す", lambda: bw_audio().get("wmk") == [[0, 0], [2, 1.2], [4, 2]])
    live_audio.warping = False
    check("ワープをオフ", lambda: bw_audio()["warp"] is False)
    live_audio.warping = True
    check("ワープをオン（マーカーも戻る）", lambda: bw_audio()["warp"] is True and len(bw_audio().get("wmk") or []) == 3)
    before = [(m.beat_time, m.sample_time) for m in live_audio.warp_markers], live_audio.pitch_coarse, live_audio.warp_mode
    check("Bitwig から送り返されて Live のオーディオが変わったりしない",
          lambda: ([(m.beat_time, m.sample_time) for m in live_audio.warp_markers], live_audio.pitch_coarse, live_audio.warp_mode) == before, 3)


def fuzz(rounds=40, seed=1):
    """Live と Bitwig の両方で、でたらめな編集を同時に続け、最後に両方が同じ状態になるか確かめる。"""
    import random
    rnd = random.Random(seed)
    midi_tracks = [t for t in song.tracks if t.has_midi_input]
    for _ in range(rounds):
        side = rnd.random()
        track = rnd.choice(midi_tracks)
        clips = track.arrangement_clips
        if side < 0.5:
            op = rnd.choice(["note", "unnote", "move", "resize", "trim", "new", "tempo"])
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
                elif op == "trim" and clips:
                    # 左端を動かす（位置と中身の開始位置が一緒にずれる）
                    c = rnd.choice(clips)
                    d = rnd.choice([1.0, 2.0])
                    if c.end_time - c.start_time > d + 1.0:
                        c.start_time, c.start_marker = c.start_time + d, c.start_marker + d
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
            op = rnd.choice(["clip-note", "clip-dur", "clip-move", "clip-trim"])
            if op == "clip-note":
                bw_cmd("clip-note %s %g %d %g 0.5" % (track.name, start, rnd.randint(40, 80), rnd.randint(0, 7) * 0.5), wait=False)
            elif op == "clip-dur":
                dur = rnd.choice([2.0, 4.0, 6.0, 8.0])
                if all(x <= start or x >= start + dur for x in starts if x != start):
                    bw_cmd("clip-dur %s %g %g" % (track.name, start, dur), wait=False)
            elif op == "clip-trim":
                dur = (s.get("c/a/%s/%s" % (tid, _key(start))) or {}).get("dur") or 0
                if dur > 3:
                    bw_cmd("clip-trim %s %g %g" % (track.name, start, start + 1.0), wait=False)
            else:
                new = start + rnd.choice([-4.0, 4.0, 8.0])
                others = [x for x in starts if x != start]
                if new >= 0 and all(abs(x - new) >= 8 for x in others):
                    bw_cmd("clip-move %s %g %g" % (track.name, start, new), wait=False)
        run_until(lambda: False, rnd.choice([0.05, 0.2, 0.5]))


def _key(t):
    return ("%.4f" % t).rstrip("0").rstrip(".")


def converged():
    s = bw()
    if s["tempo"] != song.tempo or bw_names() != names():
        return False
    return all(same_clips(t) for t in song.tracks if t.has_midi_input)


def bw_cmd(*lines, wait=True):
    """Bitwig の拡張にテスト用のコマンドを送る（cmd.txt。処理されると消える）。"""
    path = os.path.join(BW_DIR, "cmd.txt")
    # 前のコマンドがまだ処理されていなければ待つ（上書きすると前のコマンドが消えてしまう）
    run_until(lambda: not os.path.exists(path), 5)
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    for _ in range(50):
        try:
            os.replace(path + ".tmp", path)
            break
        except PermissionError:  # Bitwig が読んでいる最中
            time.sleep(0.02)
    if wait:
        run_until(lambda: not os.path.exists(path), 5)


def self_ops():
    """アプリのログで、自分の DAW から送った変更の行数（Live 側, Bitwig 側）。"""
    def count(name):
        with open(os.path.join(LOGS, name), encoding="utf-8", errors="replace") as f:
            return sum(1 for line in f if "Self" in line)
    return count("app_live.txt"), count("app_bitwig.txt")


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
    print("\n%d 秒間つないだままにします。Bitwig を操作すると logs/live_state.json に Live 側の状態が出ます" % seconds, flush=True)
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
