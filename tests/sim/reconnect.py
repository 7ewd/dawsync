"""
通信が切れたときに、ルームへ自動でつなぎ直せるかのテスト（モックの Live 2 台）。

B はホスト A に中継（このスクリプト内の TCP プロキシ）を通してつなぐ。途中でプロキシを止めて回線断を再現し、
切れている間に A・B それぞれで編集してから、プロキシを戻す。
- B が自動でつなぎ直すこと
- 切れている間の B の変更が A に届くこと、A の変更が B に届くこと
使い方: python tests/sim/reconnect.py（事前に dotnet build src/DawSync.Cli）
"""
import os
import socket
import subprocess
import sys
import threading
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..", "..")
sys.path.insert(0, os.path.join(HERE, "mock_live"))
sys.path.insert(0, os.path.join(ROOT, "src", "DawSync.Core", "RemoteScript"))

import Live  # noqa: E402  (モック)
from DawSync import multi  # noqa: E402

LOGS = os.path.join(HERE, "logs", "reconnect")
os.makedirs(LOGS, exist_ok=True)
CLI = os.path.join(ROOT, "src", "DawSync.Cli", "bin", "Debug", "net10.0", "DawSync.Cli")
SERVER_PORT, PROXY_PORT = 47471, 47472
results = []


class Proxy:
    """止めたり戻したりできる TCP の中継（回線断の再現用）。"""

    def __init__(self, listen, target):
        self.listen, self.target = listen, target
        self.server = None
        self.sockets = []

    def start(self):
        self.server = socket.socket()
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind(("127.0.0.1", self.listen))
        self.server.listen()
        threading.Thread(target=self._accept, args=(self.server,), daemon=True).start()

    def _accept(self, server):
        while True:
            try:
                a, _ = server.accept()
            except OSError:
                return
            b = socket.create_connection(("127.0.0.1", self.target))
            self.sockets += [a, b]
            for x, y in ((a, b), (b, a)):
                threading.Thread(target=self._pipe, args=(x, y), daemon=True).start()

    @staticmethod
    def _pipe(x, y):
        try:
            while True:
                data = x.recv(65536)
                if not data:
                    break
                y.sendall(data)
        except OSError:
            pass
        for s in (x, y):
            try:
                s.close()
            except OSError:
                pass

    def stop(self):
        self.server.close()
        for s in self.sockets:
            try:
                s.shutdown(socket.SHUT_RDWR)
                s.close()
            except OSError:
                pass
        self.sockets = []


class CInstance:
    def __init__(self, name, song):
        self.name, self._song = name, song

    def song(self):
        return self._song

    def log(self, message):
        if "error" in message.lower() or "failed" in message.lower():
            print("  [%s] %s" % (self.name, message))


class Side:
    def __init__(self, name, port):
        self.name, self.port = name, port
        self.song = make_song()
        self.app = Live._App()
        self.app.browser = Live.Browser([])
        self.app.browser.song = self.song
        self.script = None

    def start(self):
        Live.Application.current = self.app
        self.script = multi.DawSync(CInstance(self.name, self.song))
        self.script._port = self.port

    def tick(self):
        if self.script is not None:
            Live.Application.current = self.app
            self.script.update_display()


def make_song():
    song = Live.Song(scenes=2)
    song.tempo = 128.0
    bass = Live.Track("Bass", slots=2)
    bass.clip_slots[0].create_clip(4.0)
    lead = Live.Track("Lead", slots=2)
    song.tracks = [bass, lead]
    return song


def tick():
    for side in (A, B):
        side.tick()


def check(name, cond, timeout=6.0):
    end = time.time() + timeout
    ok = False
    while time.time() < end:
        tick()
        try:
            ok = cond()
        except (IndexError, AttributeError, KeyError):
            ok = False
        if ok:
            break
        time.sleep(0.02)
    results.append((name, ok))
    print(("PASS " if ok else "FAIL ") + name)
    return ok


def notes(song):
    return sorted((n.pitch, n.start_time) for n in song.tracks[0].clip_slots[0].clip._notes)


def log_text(name):
    with open(os.path.join(LOGS, "app_%s.txt" % name.lower()), encoding="utf-8", errors="replace") as f:
        return f.read()


def spawn(name, args):
    out = open(os.path.join(LOGS, "app_%s.txt" % name.lower()), "w", encoding="utf-8")
    return subprocess.Popen([CLI, "session", "--name", name, "--samples", os.path.join(LOGS, "samples_" + name.lower())] + args,
                            stdout=out, stderr=subprocess.STDOUT, env=dict(os.environ, DOTNET_CLI_UI_LANGUAGE="ja"))


A = Side("A", 47470)
B = Side("B", 47480)
proxy = Proxy(PROXY_PORT, SERVER_PORT)
procs = [spawn("A", ["--host", "--port", str(SERVER_PORT), "--key", "TEST42", "--bridge-port", "47470"])]
try:
    time.sleep(1.5)
    proxy.start()
    procs.append(spawn("B", ["--join", "127.0.0.1:%d" % PROXY_PORT, "--key", "TEST42", "--bridge-port", "47480"]))
    time.sleep(1.5)
    A.start()
    B.start()

    A.song.tracks[0].clip_slots[0].clip.user_add_note(60, 0.0)
    check("つながっている: A のノートが B に届く", lambda: notes(B.song) == [(60, 0.0)])

    proxy.stop()
    check("回線が切れたことに気づく", lambda: "つなぎ直しています" in log_text("B"))

    # 切れている間にそれぞれ編集する
    B.song.tracks[1].name = "Lead B"
    B.song.tracks[0].clip_slots[0].clip.user_add_note(64, 1.0)
    A.song.tempo = 140.0
    for _ in range(50):
        tick()
        time.sleep(0.02)
    check("切れている間は届かない", lambda: A.song.tracks[1].name == "Lead" and B.song.tempo == 128.0, timeout=0.5)

    proxy.start()
    check("自動でつなぎ直す", lambda: "ルームにつなぎ直しました" in log_text("B"), timeout=20)
    check("切れている間の B の変更（名前）が A に届く", lambda: A.song.tracks[1].name == "Lead B")
    check("切れている間の B の変更（ノート）が A に届く", lambda: notes(A.song) == [(60, 0.0), (64, 1.0)])
    check("切れている間の A の変更が B に届く", lambda: B.song.tempo == 140.0)
    check("B のノートが消されていない", lambda: notes(B.song) == [(60, 0.0), (64, 1.0)])

    B.song.tracks[0].clip_slots[0].clip.user_add_note(67, 2.0)
    check("つなぎ直した後も同期が続く", lambda: notes(A.song) == [(60, 0.0), (64, 1.0), (67, 2.0)])
finally:
    for p in procs:
        p.kill()
    proxy.stop()

failed = [n for n, ok in results if not ok]
print("\n%d/%d 成功" % (len(results) - len(failed), len(results)))
if failed:
    print("--- アプリ B のログ（最後の 30 行）---")
    print("\n".join(log_text("B").splitlines()[-30:]))
sys.exit(1 if failed else 0)
