"""
本物の FL Studio（DawSync のスクリプトを MIDI 設定で割り当てておく）と、モックの Live で同時編集を確かめる。

  python tests/flstudio/real_fl.py            自動のテスト
  python tests/flstudio/real_fl.py --watch 120 テストの後、120 秒間つないだままにして Live 側の状態を
                                               logs/real_live_state.json に書き続ける（FL Studio を手で触って確かめる用）

準備:
  - FL Studio を環境変数 DAWSYNC_DEBUG=1 で起動し、スクリプトのフォルダの ipc/debug を置く（FL Studio の状態を
    ipc/47400/state.json に書いてもらうため）
  - MIDI 設定で、MIDI 入力（loopMIDI の仮想ポートなど）の Controller type を「DAW Sync」にして有効にする
  - DawSync のアプリは閉じておく（FL Studio はいつものポート 47400 につなぐので、このテストのアプリが使う）
FL Studio で開いているプロジェクトのテンポ・拍子は変わり、マーカーが足されるので、捨ててよいプロジェクトで行うこと。
"""
import json
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tests", "sim", "mock_live"))
sys.path.insert(0, os.path.join(ROOT, "src", "DawSync.Core", "RemoteScript"))

import Live  # noqa: E402  (モック)
from DawSync import multi  # noqa: E402

LOGS = os.path.join(HERE, "logs")
CLI = os.path.join(ROOT, "src", "DawSync.Cli", "bin", "Debug", "net10.0", "DawSync.Cli" + (".exe" if os.name == "nt" else ""))
results = []


def fl_user_data():
    if os.name == "nt":
        try:
            import winreg
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Image-Line\Shared\Paths") as key:
                return os.path.join(winreg.QueryValueEx(key, "Shared data")[0], "FL Studio")
        except OSError:
            pass
    return os.path.join(os.path.expanduser("~"), "Documents", "Image-Line", "FL Studio")


FL_IPC = os.path.join(fl_user_data(), "Settings", "Hardware", "DawSync", "ipc")
FL_STATE = os.path.join(FL_IPC, "47400", "state.json")


def fl():
    with open(FL_STATE, encoding="utf-8") as f:
        return json.load(f)


class CInstance(object):
    def __init__(self, song):
        self._song = song

    def song(self):
        return self._song

    def log(self, message):
        if "error" in message.lower() or "failed" in message.lower():
            print("  [Live] " + message)


song = Live.Song(scenes=2)
song.tempo = 128.0
bass = Live.Track("Bass", slots=2)
bass.arrangement_clips = [Live.Clip(8.0, 0.0, [(40, 0.0, 1.0, 90)])]
song.tracks = [bass, Live.Track("Keys", slots=2)]
app = Live._App()
app.browser = Live.Browser([])
app.browser.song = song
script = None


def tick():
    if script is not None:
        Live.Application.current = app
        song._begin_tick()
        try:
            script.update_display()
        finally:
            song._end_tick()


def run_until(cond, timeout):
    end = time.time() + timeout
    while time.time() < end:
        tick()
        try:
            if cond():
                return True
        except (OSError, ValueError, KeyError, IndexError, TypeError):
            pass
        time.sleep(0.03)
    return False


def check(name, cond, timeout=10.0):
    ok = run_until(cond, timeout)
    results.append((name, ok))
    print(("PASS " if ok else "FAIL ") + name, flush=True)
    return ok


def app_log(name):
    try:
        with open(os.path.join(LOGS, "real_app_%s.txt" % name), encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


os.makedirs(LOGS, exist_ok=True)
procs = [
    subprocess.Popen([CLI, "session", "--host", "--name", "FL", "--bridge-port", "47400", "--port", "47611", "--key", "TEST42",
                      "--samples", os.path.join(LOGS, "real_samples_a")],
                     stdout=open(os.path.join(LOGS, "real_app_a.txt"), "w", encoding="utf-8"), stderr=subprocess.STDOUT),
]
try:
    check("本物の FL Studio がアプリにつながり、テンポ・拍子をルームに入れる",
          lambda: "このセットの状態をルームに共有しました" in app_log("a") and os.path.exists(FL_STATE), 30)
    start_tempo = fl()["fl_tempo"]
    print("     FL Studio: %s BPM, 分子 %s, マーカー %s" % (start_tempo, fl()["fl_numerator"], fl()["fl_markers"]))
    procs.append(subprocess.Popen([CLI, "session", "--join", "127.0.0.1:47611", "--key", "TEST42", "--name", "LIVE",
                                   "--bridge-port", "47620", "--samples", os.path.join(LOGS, "real_samples_b")],
                                  stdout=open(os.path.join(LOGS, "real_app_b.txt"), "w", encoding="utf-8"),
                                  stderr=subprocess.STDOUT))
    time.sleep(1.5)
    Live.Application.current = app
    script = multi.DawSync(CInstance(song))
    script._port = 47620
    check("Live（ゲスト）が FL Studio のテンポになる", lambda: abs(song.tempo - start_tempo) < 1e-6, 20)
    check("ルームにトラックが無いので、Live のトラックをルームに入れる", lambda: "ルームにトラックが無かったので" in app_log("b"))

    song.tempo = 125.0
    check("Live のテンポが本物の FL Studio に届く", lambda: abs(fl()["fl_tempo"] - 125.0) < 1e-6)
    song.signature_numerator = 3
    check("Live の拍子が本物の FL Studio に届く", lambda: fl()["fl_numerator"] == 3)
    song.current_song_time = 16.0
    song.set_or_delete_cue()
    song.cue_points[0].name = "DAW Sync test"
    song.current_song_time = 0.0
    check("Live のロケーターが本物の FL Studio のマーカーになる", lambda: "DAW Sync test" in fl()["fl_markers"])
    song.tempo = 140.0
    check("もう一度 Live のテンポを変える", lambda: abs(fl()["fl_tempo"] - 140.0) < 1e-6)
    check("FL Studio は届いた値を送り返さない（Live のテンポがそのまま）", lambda: song.tempo == 140.0
          and app_log("a").count("テンポを 140 BPM") == 1, 3)

    watch = int(sys.argv[sys.argv.index("--watch") + 1]) if "--watch" in sys.argv else 0
    if watch:
        print("%d 秒間つないだままにします（Live の状態: logs/real_live_state.json）" % watch, flush=True)
        end = time.time() + watch
        last = 0.0
        while time.time() < end:
            tick()
            if time.time() - last > 0.5:
                last = time.time()
                state = {"tempo": song.tempo, "sig": [song.signature_numerator, song.signature_denominator],
                         "locators": [[c.time, c.name] for c in song.cue_points]}
                with open(os.path.join(LOGS, "real_live_state.json"), "w", encoding="utf-8") as f:
                    json.dump(state, f, ensure_ascii=False)
            time.sleep(0.03)
finally:
    for p in procs:
        p.kill()

failed = [n for n, ok in results if not ok]
print()
print("%d / %d passed" % (len(results) - len(failed), len(results)))
sys.exit(1 if failed else 0)
