"""
本物の Live と本物の Bitwig で同時編集を確かめる（画面・マウス・キーボードは使わない）。

  事前に:
    Live   … 捨ててよいセットを開き、コントロールサーフェスで AbletonMulti を選んでおく（Remote Script は User Library に入れる）
    Bitwig … 捨ててよいプロジェクトを開き、AbletonMulti の拡張を有効にしておく
  python tests/live/real_live_bitwig.py

Live の Remote Script は、%TEMP%/abletonmulti-live/debug があるときだけテストから操作できる
（cmd.py を Live の中で実行し、state.json に今の状態を書く。port でつなぐ先のポートを変える）。
Live の API ではクリップを動かす・端を動かすなどはできないので、それは Bitwig 側で行い、Live に反映されるかを見る。
Live 側からは、ノートの追加・削除・移動、クリップの作成・削除・ループなど API でできる編集をする。
どちらもテスト用のポート（Live 47480 / Bitwig 47490）を使うので、本物のアプリ（47400）には影響しない。
"""
import json
import os
import random
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
LOGS = os.path.join(HERE, "logs")
LIVE_DIR = os.path.join(tempfile.gettempdir(), "abletonmulti-live")
BW_DIR = os.path.join(tempfile.gettempdir(), "bitwig", "abletonmulti-bitwig")
LIVE_PORT, BITWIG_PORT, ROOM_PORT = 47480, 47490, 47481
CLI = os.path.join(ROOT, "src", "AbletonMulti.Cli", "bin", "Debug", "net10.0", "AbletonMulti.Cli" + (".exe" if os.name == "nt" else ""))
results = []


def wait_until(cond, timeout):
    end = time.time() + timeout * (1.0 + float(os.environ.get("LATENCY_MS") or 0) / 100.0)
    while time.time() < end:
        try:
            if cond():
                return True
        except Exception:  # noqa: BLE001
            pass
        time.sleep(0.1)
    return False


def check(name, cond, timeout=10.0, detail=None):
    ok = wait_until(cond, timeout)
    results.append((name, ok))
    print(("PASS " if ok else "FAIL ") + name, flush=True)
    if not ok and detail:
        try:
            detail()
        except Exception as e:  # noqa: BLE001
            print("     (詳細を出せませんでした: %r)" % e)
    return ok


# ---------------------------------------------------------------- Live

def live_state():
    with open(os.path.join(LIVE_DIR, "state.json"), encoding="utf-8") as f:
        return json.load(f)


def live(code):
    """Live の中で Python を実行する（song・Live が使える）。終わるまで待ち、エラーなら例外にする。"""
    log = os.path.join(LIVE_DIR, "cmd.log")
    before = os.path.getsize(log) if os.path.exists(log) else 0
    path = os.path.join(LIVE_DIR, "cmd.py")
    wait_until(lambda: not os.path.exists(path), 10)
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        f.write(code)
    os.replace(path + ".tmp", path)
    if not wait_until(lambda: os.path.exists(log) and os.path.getsize(log) > before, 10):
        raise RuntimeError("Live がコマンドを実行しませんでした")
    with open(log, encoding="utf-8") as f:
        f.seek(before)
        result = f.read().strip()
    if result != "ok":
        raise RuntimeError(result)


# ---------------------------------------------------------------- Bitwig

def bw():
    with open(os.path.join(BW_DIR, "state.json"), encoding="utf-8") as f:
        return json.load(f)


def bw_cmd(*lines):
    path = os.path.join(BW_DIR, "cmd.txt")
    wait_until(lambda: not os.path.exists(path), 5)
    with open(path + ".tmp", "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    for _ in range(50):
        try:
            os.replace(path + ".tmp", path)
            break
        except PermissionError:
            time.sleep(0.02)
    wait_until(lambda: not os.path.exists(path), 5)


# ---------------------------------------------------------------- 比べる

def tracks(s):
    ids = [k.split("/")[1] for k in s if k.startswith("t/") and k.count("/") == 1 and s[k]]
    ids.sort(key=lambda i: s["t/" + i]["o"])
    return [(i, s.get("t/%s/name" % i)) for i in ids]


def clips(s, name):
    tid = next(i for i, n in tracks(s) if n == name)
    out = []
    for k, v in s.items():
        p = k.split("/")
        if p[0] == "c" and p[1] == "a" and p[2] == tid and v:
            pp = v.get("p") or {}
            notes = sorted((int(r[0]), round(float(r[1]), 3), round(float(r[2]), 3)) for r in v.get("n") or [])
            loop = (bool(pp.get("looping")), round(float(pp.get("ls", 0)), 3), round(float(pp.get("le", 0)), 3)) if pp.get("looping") else (False,)
            out.append((round(float(p[3]), 3), round(float(v.get("dur") or 0), 3), round(float(pp.get("sm", 0)), 3), loop, notes))
    return sorted(out)


def same(name):
    return clips(live_state(), name) == clips(bw(), name)


def show(name):
    def f():
        print("     Live:   %s" % [(a, b, c, d, len(n)) for a, b, c, d, n in clips(live_state(), name)])
        print("     Bitwig: %s" % [(a, b, c, d, len(n)) for a, b, c, d, n in clips(bw(), name)])
        for x, y in zip(clips(live_state(), name), clips(bw(), name)):
            if x[4] != y[4]:
                print("       @%s Live だけ %s / Bitwig だけ %s" % (x[0], [n for n in x[4] if n not in y[4]], [n for n in y[4] if n not in x[4]]))
    return f


def starts(name, s=None):
    return [c[0] for c in clips(s or live_state(), name)]


# ---------------------------------------------------------------- 準備

def main():
    os.makedirs(LOGS, exist_ok=True)
    os.makedirs(LIVE_DIR, exist_ok=True)
    os.makedirs(BW_DIR, exist_ok=True)
    for d in (LIVE_DIR, BW_DIR):
        open(os.path.join(d, "debug"), "w").close()
    until = int((time.time() + 3600) * 1000)
    with open(os.path.join(LIVE_DIR, "port"), "w") as f:
        f.write("%d %d" % (LIVE_PORT, until))

    build = subprocess.run(["dotnet", "build", os.path.join(ROOT, "src", "AbletonMulti.Cli"), "-v", "q", "-nologo"],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
    if build.returncode != 0:
        print(build.stdout[-3000:])
        sys.exit(1)

    def env(name):
        if os.environ.get("TRACE") != "1":
            return None
        path = os.path.join(LOGS, "trace_%s.jsonl" % name)
        if os.path.exists(path):
            os.remove(path)
        return dict(os.environ, ABLETONMULTI_TRACE=path)

    procs = [subprocess.Popen([CLI, "session", "--host", "--name", "Live", "--bridge-port", str(LIVE_PORT), "--port", str(ROOM_PORT),
                               "--key", "REALTEST", "--samples", os.path.join(LOGS, "samples_live"), "--blank"],
                              stdout=open(os.path.join(LOGS, "app_live.txt"), "w"), stderr=subprocess.STDOUT, env=env("live"))]
    try:
        if not wait_until(lambda: "まっさら" in open(os.path.join(LOGS, "app_live.txt"), encoding="utf-8", errors="replace").read(), 40):
            print("Live がつながりませんでした（AbletonMulti のコントロールサーフェスが選ばれているか確かめてください）")
            sys.exit(1)
        room = "127.0.0.1:%d" % ROOM_PORT
        if os.environ.get("LATENCY_MS"):
            procs.append(subprocess.Popen([sys.executable, os.path.join(ROOT, "tests", "sim", "latency_proxy.py"), "47482", str(ROOM_PORT),
                                           os.environ["LATENCY_MS"], os.environ.get("JITTER_MS", "0")]))
            room = "127.0.0.1:47482"
            time.sleep(0.5)
        with open(os.path.join(BW_DIR, "port"), "w") as f:
            f.write("%d %d" % (BITWIG_PORT, until))
        procs.append(subprocess.Popen([CLI, "session", "--join", room, "--key", "REALTEST", "--name", "Bitwig",
                                       "--bridge-port", str(BITWIG_PORT), "--samples", os.path.join(LOGS, "samples_bitwig"), "--blank"],
                                      stdout=open(os.path.join(LOGS, "app_bitwig.txt"), "w"), stderr=subprocess.STDOUT, env=env("bitwig")))
        run_tests()
    finally:
        for p in procs:
            p.kill()
        for name in ("port", "debug"):
            for d in (LIVE_DIR, BW_DIR):
                try:
                    os.remove(os.path.join(d, name))
                except OSError:
                    pass
    failed = [n for n, ok in results if not ok]
    print("\n%d/%d passed" % (len(results) - len(failed), len(results)))
    sys.exit(1 if failed else 0)


NOTE = "Live.Clip.MidiNoteSpecification(pitch=%d, start_time=%r, duration=%r, velocity=100)"


def run_tests():
    # Live で曲を作る（まっさらにしたセットには MIDI トラックが 1 本ある）
    live("""
t = song.tracks[0]
t.name = "Bass"
c = t.create_midi_clip(8.0, 8.0)
c.name = "Riff"
c.add_new_notes((%s, %s, %s))
k = song.create_midi_track(-1)
song.tracks[-1].name = "Keys"
c2 = song.tracks[-1].create_midi_clip(0.0, 4.0)
c2.add_new_notes((%s, %s))
""" % (NOTE % (40, 0.0, 1.0), NOTE % (43, 2.0, 1.0), NOTE % (47, 4.0, 0.5), NOTE % (60, 0.0, 2.0), NOTE % (64, 0.0, 2.0)))
    check("Bitwig が参加して、Live で作ったトラックとクリップがそろう",
          lambda: [n for _, n in tracks(bw())] == ["Bass", "Keys"] and same("Bass") and same("Keys"), 60, show("Bass"))

    # ---- Bitwig で動かす・端を動かす・分ける → 本物の Live に反映される
    bw_cmd("clip-move Bass 8 16")
    check("Bitwig でクリップを動かす → Live（複製して元を消す）", lambda: starts("Bass") == [16.0] and same("Bass"), detail=show("Bass"))
    bw_cmd("clip-trim Bass 16 18")
    check("Bitwig で左端を動かす → Live", lambda: starts("Bass") == [18.0] and same("Bass"), detail=show("Bass"))
    bw_cmd("clip-dur Bass 18 10")
    check("Bitwig で長さを変える → Live", lambda: clips(live_state(), "Bass")[0][1] == 10.0 and same("Bass"), detail=show("Bass"))
    bw_cmd("clip-loop Bass 18 on 0 4")
    check("Bitwig でループを入れる → Live", lambda: clips(live_state(), "Bass")[0][3] == (True, 0.0, 4.0) and same("Bass"), detail=show("Bass"))
    bw_cmd("clip-loop Bass 18 off")
    check("Bitwig でループを切る → Live", lambda: clips(live_state(), "Bass")[0][3] == (False,) and same("Bass"), detail=show("Bass"))
    bw_cmd("clip-split Bass 18 22")
    check("Bitwig で分割する → Live", lambda: starts("Bass") == [18.0, 22.0] and same("Bass"), detail=show("Bass"))
    bw_cmd("clip-note Bass 18 55 1 0.5")
    check("Bitwig でノートを足す → Live", lambda: same("Bass") and (55, 1.0, 0.5) in clips(live_state(), "Bass")[0][4], detail=show("Bass"))

    # ---- Live の API でできる編集 → Bitwig
    live("""
c = [c for c in song.tracks[0].arrangement_clips if abs(c.start_time - 18.0) < 1e-6][0]
notes = c.get_notes_extended(0, 128, 0.0, 1000.0)
for n in notes:
    if n.pitch == 55:
        n.pitch = 57
c.apply_note_modifications(notes)
""")
    check("Live でノートを上に動かす → Bitwig（複製にならない）",
          lambda: same("Bass") and (57, 1.0, 0.5) in clips(bw(), "Bass")[0][4] and (55, 1.0, 0.5) not in clips(bw(), "Bass")[0][4],
          detail=show("Bass"))
    live("""
c = [c for c in song.tracks[0].arrangement_clips if abs(c.start_time - 18.0) < 1e-6][0]
notes = c.get_notes_extended(0, 128, 0.0, 1000.0)
for n in notes:
    if n.pitch == 57:
        n.start_time = 1.5
c.apply_note_modifications(notes)
""")
    check("Live でノートを横に動かす → Bitwig", lambda: same("Bass") and (57, 1.5, 0.5) in clips(bw(), "Bass")[0][4], detail=show("Bass"))
    live("""
c = [c for c in song.tracks[0].arrangement_clips if abs(c.start_time - 22.0) < 1e-6][0]
song.tracks[0].delete_clip(c)
""")
    check("Live でクリップを消す → Bitwig", lambda: starts("Bass", bw()) == [18.0] and same("Bass"), detail=show("Bass"))
    live("""
c = song.tracks[1].arrangement_clips[0]
c.looping = True
c.loop_end = 2.0
c.name = "Pad"
""")
    check("Live でループと名前を変える → Bitwig", lambda: same("Keys"), detail=show("Keys"))

    # ---- 重なり: Bitwig でクリップを伸ばして、Live の別のクリップに重ねる（Live は重なった方を切り詰める）
    live("song.tracks[0].create_midi_clip(32.0, 4.0).add_new_notes((%s,))" % (NOTE % (70, 0.0, 1.0)))
    check("Live でクリップを作る → Bitwig", lambda: starts("Bass", bw()) == [18.0, 32.0] and same("Bass"), detail=show("Bass"))
    bw_cmd("clip-dur Bass 18 16")
    check("重なるように伸ばしても、最後は両方同じ", lambda: same("Bass"), 15, show("Bass"))

    # ---- 同時に編集
    live("""
c = [c for c in song.tracks[0].arrangement_clips if abs(c.start_time - 18.0) < 1e-6][0]
c.add_new_notes((%s,))
""" % (NOTE % (72, 3.0, 0.5)))
    bw_cmd("clip-move Bass 18 40")
    check("Live でノートを足すのと同時に Bitwig で動かしても、最後は両方同じ", lambda: same("Bass"), 15, show("Bass"))

    # ---- でたらめな同時編集
    for seed in [int(x) for x in os.environ.get("FUZZ_SEEDS", "1,2").split(",")]:
        fuzz(seed, 30)
        check("でたらめな同時編集のあと、両方同じ（%d 回目）" % seed, lambda: same("Bass") and same("Keys"), 30,
              lambda: (show("Bass")(), show("Keys")()))


def fuzz(seed, rounds):
    rnd = random.Random(seed)
    for _ in range(rounds):
        name = rnd.choice(["Bass", "Keys"])
        index = 0 if name == "Bass" else 1
        if rnd.random() < 0.5:
            op = rnd.choice(["note", "unnote", "pitch", "new", "del"])
            code = {
                "note": "cs = list(song.tracks[%d].arrangement_clips)\nif cs: cs[%d %% len(cs)].add_new_notes((%s,))"
                        % (index, rnd.randint(0, 9), NOTE % (rnd.randint(40, 80), rnd.randint(0, 7) * 0.5, 0.5)),
                "unnote": "cs = list(song.tracks[%d].arrangement_clips)\nif cs:\n    c = cs[%d %% len(cs)]\n    ns = c.get_notes_extended(0, 128, 0.0, 1000.0)\n"
                          "    if len(ns): c.remove_notes_by_id([ns[%d %% len(ns)].note_id])" % (index, rnd.randint(0, 9), rnd.randint(0, 9)),
                "pitch": "cs = list(song.tracks[%d].arrangement_clips)\nif cs:\n    c = cs[%d %% len(cs)]\n    ns = c.get_notes_extended(0, 128, 0.0, 1000.0)\n"
                         "    if len(ns):\n        n = ns[%d %% len(ns)]\n        n.pitch = min(127, n.pitch + 1)\n        c.apply_note_modifications(ns)"
                         % (index, rnd.randint(0, 9), rnd.randint(0, 9)),
                "new": "t = song.tracks[%d]\ns = %r\nif not any(c.start_time < s + 4 and c.end_time > s for c in t.arrangement_clips):\n"
                       "    t.create_midi_clip(s, 4.0).add_new_notes((%s,))" % (index, rnd.randint(0, 15) * 4.0, NOTE % (60, 0.0, 1.0)),
                "del": "cs = list(song.tracks[%d].arrangement_clips)\nif len(cs) > 1: song.tracks[%d].delete_clip(cs[%d %% len(cs)])"
                       % (index, index, rnd.randint(0, 9)),
            }[op]
            try:
                live(code)
            except RuntimeError as e:
                print("     (Live のコマンドが失敗: %s)" % str(e).splitlines()[-1])
        else:
            try:
                cs = clips(bw(), name)
            except Exception:  # noqa: BLE001
                continue
            if not cs:
                continue
            start, dur = rnd.choice(cs)[:2]
            others = [c for c in cs if c[0] != start]
            op = rnd.choice(["clip-note", "clip-dur", "clip-move", "clip-trim"])
            if op == "clip-note":
                bw_cmd("clip-note %s %g %d %g 0.5" % (name, start, rnd.randint(40, 80), rnd.randint(0, 7) * 0.5))
            elif op == "clip-dur":
                d = rnd.choice([2.0, 4.0, 6.0])
                if all(o[0] >= start + d or o[0] + o[1] <= start for o in others):
                    bw_cmd("clip-dur %s %g %g" % (name, start, d))
            elif op == "clip-trim" and dur > 3:
                bw_cmd("clip-trim %s %g %g" % (name, start, start + 1))
            elif op == "clip-move":
                new = start + rnd.choice([-8.0, 8.0, 16.0])
                if new >= 0 and all(o[0] >= new + dur or o[0] + o[1] <= new for o in others):
                    bw_cmd("clip-move %s %g %g" % (name, start, new))
        time.sleep(rnd.choice([0.05, 0.3, 0.8]))


if __name__ == "__main__":
    main()
