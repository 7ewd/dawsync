"""
FL Studio（モックの API の上で本物のスクリプト）と Live（モックの上で本物の Remote Script）を、本物のアプリ（DawSync.Cli）と
ルームを通して同時に動かすテスト。run_fl.sh がアプリを 4 つ起動してから実行する:

  ルーム 1（FL Studio がホスト）
    A: session --host --bridge-port 47510 --port 47511   … FL Studio（ファイルでのやりとり → FlStudioRelay）
    B: session --join 127.0.0.1:47511 --bridge-port 47520 … Live（トラックのあるセット）
  ルーム 2（Live がホスト）
    C: session --host --bridge-port 47530 --port 47531   … Live
    D: session --join 127.0.0.1:47531 --bridge-port 47540 … FL Studio

FL Studio のスクリプトとアプリは、DAWSYNC_FL_IPC（run_fl.sh が logs/flstudio-ipc にする）/<ポート>/ のファイルでやりとりする
（本物はスクリプトのフォルダの ipc/）。
"""
import gzip
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
import mock_fl  # noqa: E402

mock_fl.install()
sys.path.insert(0, os.path.join(ROOT, "flstudio"))
sys.path.insert(0, os.path.join(ROOT, "tests", "sim", "mock_live"))
sys.path.insert(0, os.path.join(ROOT, "src", "DawSync.Core", "RemoteScript"))

import device_DawSync as dawsync  # noqa: E402
import Live  # noqa: E402  (モック)
from DawSync import multi  # noqa: E402

LOGS = os.path.join(HERE, "logs")
IPC = os.environ.get("DAWSYNC_FL_IPC") or os.path.join(LOGS, "flstudio-ipc")
results = []


class FlSide(object):
    """FL Studio 1 台（モックの API の状態と、その上で動くスクリプト）。"""

    def __init__(self, name, port, state):
        self.name = name
        self.port = port
        self.state = state
        self.script = None
        self.warnings = []
        self.applied_keys = []

    def start(self):
        mock_fl.current = self.state
        script = dawsync.DawSync(root=IPC)
        script.app_port = lambda: self.port
        original_warn = script.warn
        script.warn = lambda m: (self.warnings.append(m), original_warn(m))
        original_handle = script.handle

        def handle(msg):
            if msg.get("t") == "apply":
                self.applied_keys.extend(o.get("k") for o in msg.get("ops") or [])
            return original_handle(msg)
        script.handle = handle
        script.start()
        self.script = script

    def stop(self):
        """FL Studio を閉じた・スクリプトを読み込み直した（OnDeInit と同じく、古い時刻の印を書く）。"""
        mock_fl.current = self.state
        dawsync._write_text(self.script.link.out_path, "dawsync-fl 1 %s 0 - 0\nend\n" % self.script.link.instance)
        self.script = None

    def tick(self):
        if self.script is not None:
            mock_fl.current = self.state
            self.script.tick()


class CInstance(object):
    def __init__(self, name, song):
        self.name = name
        self._song = song

    def song(self):
        return self._song

    def log(self, message):
        if "error" in message.lower() or "failed" in message.lower():
            print("  [%s] %s" % (self.name, message))


class LiveSide(object):
    def __init__(self, name, song, port):
        self.name = name
        self.song = song
        self.port = port
        self.app = Live._App()
        self.app.browser = Live.Browser([])
        self.app.browser.song = song
        self.script = None

    def start(self):
        Live.Application.current = self.app
        self.script = multi.DawSync(CInstance(self.name, self.song))
        self.script._port = self.port

    def tick(self):
        if self.script is not None:
            Live.Application.current = self.app
            self.song._begin_tick()
            try:
                self.script.update_display()
            finally:
                self.song._end_tick()


def make_song(tempo):
    song = Live.Song(scenes=2)
    song.tempo = tempo
    bass = Live.Track("Bass", slots=2)
    bass.arrangement_clips = [Live.Clip(8.0, 16.0, [(40, 0.0, 1.0, 90)])]
    keys = Live.Track("Keys", slots=2)
    song.tracks = [bass, keys]
    return song


SIG_4_4 = 3 + 2 * 99


def save_als(side, tempo_events):
    """Live がセットを保存した（.als を書いた）ことにする（テンポのオートメーションは保存したときに届く）。"""
    path = os.path.join(LOGS, "%s.als" % side.name)
    tempo_xml = "".join('<FloatEvent Id="%d" Time="%s" Value="%s" />' % (n, t, v) for n, (t, v) in enumerate(tempo_events))
    xml = ('<?xml version="1.0" encoding="UTF-8"?><Ableton MajorVersion="5" Creator="Ableton Live 12.4.6"><LiveSet>'
           '<MainTrack><AutomationEnvelopes><Envelopes>'
           '<AutomationEnvelope Id="0"><EnvelopeTarget><PointeeId Value="10" /></EnvelopeTarget>'
           '<Automation><Events><EnumEvent Id="0" Time="-63072000" Value="%d" /></Events></Automation></AutomationEnvelope>'
           '<AutomationEnvelope Id="1"><EnvelopeTarget><PointeeId Value="8" /></EnvelopeTarget>'
           '<Automation><Events>%s</Events></Automation></AutomationEnvelope>'
           '</Envelopes></AutomationEnvelopes><DeviceChain><Mixer>'
           '<Tempo><Manual Value="%s" /><AutomationTarget Id="8" /></Tempo>'
           '<TimeSignature><Manual Value="201" /><AutomationTarget Id="10" /></TimeSignature>'
           '</Mixer></DeviceChain></MainTrack></LiveSet></Ableton>') % (SIG_4_4, tempo_xml, tempo_events[0][1])
    with gzip.open(path, "wb") as f:
        f.write(xml.encode("utf-8"))
    stamp = time.time() + 5
    os.utime(path, (stamp, stamp))
    side.song.file_path = path


def app_log(name):
    try:
        with open(os.path.join(LOGS, "app_%s.txt" % name), encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


FL_A = FlSide("A", 47510, mock_fl.FlState(tempo=140.0, num=3))
LIVE_B = LiveSide("B", make_song(128.0), 47520)
LIVE_C = LiveSide("C", make_song(128.0), 47530)
FL_D = FlSide("D", 47540, mock_fl.FlState(tempo=100.0))
sides = [FL_A, LIVE_B, LIVE_C, FL_D]


def tick():
    for side in sides:
        side.tick()


def run_until(cond, timeout=6.0):
    end = time.time() + timeout
    while time.time() < end:
        tick()
        try:
            if cond():
                for _ in range(10):  # 少し余分に回して、エコーや二重送信が起きないかも確かめる
                    tick()
                    time.sleep(0.01)
                return True
        except (IndexError, AttributeError, KeyError, TypeError):
            pass
        time.sleep(0.02)
    return False


def check(name, cond, timeout=6.0):
    ok = run_until(cond, timeout)
    results.append((name, ok))
    print(("PASS " if ok else "FAIL ") + name, flush=True)
    return ok


def sig(song):
    return (song.signature_numerator, song.signature_denominator)


for port in (47510, 47540):
    os.makedirs(os.path.join(IPC, str(port)), exist_ok=True)
    for leftover in ("from_fl.txt",):
        try:
            os.remove(os.path.join(IPC, str(port), leftover))
        except OSError:
            pass

# ---------------------------------------------------------------- ルーム 1: FL Studio がホスト

FL_A.start()
check("FL Studio（ホスト）がアプリにつながり、テンポ・拍子をルームに入れる", lambda: "このセットの状態をルームに共有しました" in app_log("a"), 15)
LIVE_B.start()
check("Live（ゲスト）がルームのテンポ・拍子になる", lambda: LIVE_B.song.tempo == 140.0 and sig(LIVE_B.song) == (3, 4), 15)
check("ルームにトラックが無いので、Live のトラックとクリップをルームに入れる（止めない）",
      lambda: "ルームにトラックが無かったので" in app_log("b") and "一時停止" not in app_log("b"))
check("FL Studio のアプリには、トラックを扱えないとお知らせが出る", lambda: "で同期できるのは、テンポ・拍子・ロケーター" in app_log("a"))

LIVE_B.song.tempo = 125.0
check("Live のテンポが FL Studio に届く", lambda: FL_A.state.tempo == 125.0)
FL_A.state.tempo = 150.0
check("FL Studio のテンポが Live に届く", lambda: LIVE_B.song.tempo == 150.0)
LIVE_B.song.signature_numerator = 6
LIVE_B.song.signature_denominator = 8
check("Live の拍子が FL Studio に届く（分母も）", lambda: (FL_A.state.num, FL_A.state.den) == (6, 8))
FL_A.state.num = 5
check("FL Studio の拍子（分子）が、分母と一緒に Live に届く", lambda: sig(LIVE_B.song) == (5, 8))

LIVE_B.song.current_song_time = 16.0
LIVE_B.song.set_or_delete_cue()
LIVE_B.song.cue_points[0].name = "Chorus"
LIVE_B.song.current_song_time = 0.0
check("Live のロケーターが FL Studio のマーカーになる", lambda: FL_A.state.markers == [(16 * 96, "Chorus")])
LIVE_B.song.current_song_time = 32.0
LIVE_B.song.set_or_delete_cue()
next(c for c in LIVE_B.song.cue_points if c.time == 32.0).name = "Drop"
LIVE_B.song.current_song_time = 0.0
check("ロケーターを足しても、前のマーカーは二重にならない",
      lambda: sorted(FL_A.state.markers) == [(16 * 96, "Chorus"), (32 * 96, "Drop")])

LIVE_B.song.tracks[1].arrangement_clips = [Live.Clip(4.0, 0.0, [(60, 0.0, 1.0, 100)])]
check("Live のクリップはルームに入る（FL Studio のアプリにも届く）", lambda: "クリップ" in app_log("a") or "Keys" in app_log("a"), 8)
check("FL Studio のスクリプトには、扱えないキーを渡さない",
      lambda: FL_A.applied_keys and all(k in dawsync.KEYS for k in FL_A.applied_keys), 2)

save_als(LIVE_B, [(-63072000, 150), (8, 150), (8, 170)])
check("Live のテンポの変化（8 拍目から 170）が FL Studio に届く", lambda: FL_A.script.model.tempo_rows == [[0.0, 150.0], [8.0, 170.0]])
FL_A.state.playing = True
FL_A.state.seek(12)
check("FL Studio は再生中、届いたテンポの変化に追従する", lambda: FL_A.state.tempo == 170.0, 3)
FL_A.state.playing = False
FL_A.state.seek(0)
check("止めて戻ると、その位置のテンポになる（送り返さない）",
      lambda: FL_A.state.tempo == 150.0 and LIVE_B.script._model.read("tempo_map") == [[0.0, 150.0], [8.0, 170.0]], 3)

# FL Studio のスクリプトを読み込み直した（FL Studio の再起動など）
FL_A.stop()
check("FL Studio のスクリプトが止まると、アプリはすぐに切れたとわかる", lambda: "との接続が切れました" in app_log("a"), 5)
FL_A.state.tempo = 110.0  # 止まっている間に FL Studio で変えた（ルームの値に戻される）
FL_A.start()
check("つなぎ直すと、ルームのテンポに合わせ直す", lambda: FL_A.state.tempo == 150.0 and app_log("a").count("と接続しました") >= 2, 15)
check("つなぎ直しても、マーカーは二重にならない", lambda: sorted(FL_A.state.markers) == [(16 * 96, "Chorus"), (32 * 96, "Drop")], 2)

# ---------------------------------------------------------------- ルーム 2: Live がホスト、FL Studio がゲスト

LIVE_C.start()
check("Live（ホスト）がルームを作る", lambda: "このセットの状態をルームに共有しました" in app_log("c"), 15)
FL_D.start()
check("FL Studio（ゲスト）がルームのテンポになる（トラック数の違いで止めない）",
      lambda: FL_D.state.tempo == 128.0 and "一時停止" not in app_log("d"), 15)
FL_D.state.tempo = 132.0
check("ゲストの FL Studio のテンポが、ホストの Live に届く", lambda: LIVE_C.song.tempo == 132.0)
LIVE_C.song.create_midi_track(-1)
check("ホストの Live がトラックを足しても、FL Studio は止まらない",
      lambda: "一時停止" not in app_log("d") and all(k in dawsync.KEYS for k in FL_D.applied_keys), 3)
LIVE_C.song.tempo = 90.0
check("そのあとも Live のテンポが FL Studio に届く", lambda: FL_D.state.tempo == 90.0)

errors = [w for side in (FL_A, FL_D) for w in side.warnings if "エラー" in w]
check("FL Studio のスクリプトでエラーが起きていない", lambda: not errors, 0.1)
if errors:
    print("     " + repr(errors))

failed = [n for n, ok in results if not ok]
print()
print("%d / %d passed" % (len(results) - len(failed), len(results)))
sys.exit(1 if failed else 0)
