"""
テンポ・拍子の変化（tempo_map / sig_map）の共通の形を、Live（model.py）・REAPER（model.lua）・Bitwig（TimingMaps.java）
の 3 つの実装で同じ例（vectors.json）について確かめる。REAPER は reaper_mock.lua の上で、テンポマーカー・ロケーターの
読み書きも確かめる。

  python tests/timing/test_timing.py

Lua は lupa（pip install lupa）、Java は javac があるときだけ確かめる（無ければ飛ばす）。
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tests", "sim", "mock_live"))
sys.path.insert(0, os.path.join(ROOT, "src", "DawSync.Core", "RemoteScript"))

from DawSync import model  # noqa: E402

with open(os.path.join(HERE, "vectors.json"), encoding="utf-8") as f:
    VECTORS = json.load(f)
results = []


def check(name, ok, detail=None):
    results.append((name, bool(ok)))
    print(("PASS " if ok else "FAIL ") + name)
    if not ok and detail is not None:
        print("     " + str(detail))


def same(a, b, tol=1e-9):
    """数値は 1 と 1.0 を同じに、配列は中身で比べる。"""
    if isinstance(a, bool) or isinstance(b, bool):
        return a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return abs(a - b) <= tol
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same(x, y, tol) for x, y in zip(a, b))
    return a == b


def expected():
    """vectors.json の答えを、どの実装の結果とも比べられる形にする。"""
    out = {"tempo": [], "sig": [], "close": [c["close"] for c in VECTORS["close"]],
           "set_tempo_at": [c["out"] for c in VECTORS["set_tempo_at"]]}
    for c in VECTORS["tempo"]:
        out["tempo"].append({"out": c["out"], "at": [s[1] for s in c.get("at", [])]})
    for c in VECTORS["sig"]:
        out["sig"].append({"out": c["out"], "at": [s[1] for s in c.get("at", [])]})
    return out


def compare(label, got):
    want = expected()
    for n, c in enumerate(VECTORS["tempo"]):
        g = got["tempo"][n]
        check("%s テンポ: %s" % (label, c["name"]), same(g["out"], want["tempo"][n]["out"])
              and same(g["at"], want["tempo"][n]["at"], 1e-6), g)
    for n, c in enumerate(VECTORS["sig"]):
        g = got["sig"][n]
        check("%s 拍子: %s" % (label, c["name"]), same(g["out"], want["sig"][n]["out"]) and same(g["at"], want["sig"][n]["at"]), g)
    for n, c in enumerate(VECTORS["close"]):
        check("%s ほぼ同じか %d" % (label, n + 1), got["close"][n] == c["close"], got["close"][n])
    for n, c in enumerate(VECTORS["set_tempo_at"]):
        check("%s 区間の BPM を変える %d" % (label, n + 1), same(got["set_tempo_at"][n], c["out"]), got["set_tempo_at"][n])


# ------------------------------------------------------------------ Live（Python）

def python_results():
    out = {"tempo": [], "sig": [], "close": [], "set_tempo_at": []}
    for c in VECTORS["tempo"]:
        rows = model.normalize_tempo_map(c["in"])
        out["tempo"].append({"out": rows, "at": [model.tempo_at(rows, s[0]) for s in c.get("at", [])]})
    for c in VECTORS["sig"]:
        rows = model.normalize_sig_map(c["in"])
        out["sig"].append({"out": rows, "at": [model.sig_at(rows, s[0]) for s in c.get("at", [])]})
    for c in VECTORS["close"]:
        out["close"].append(model.tempo_maps_close(c["a"], c["b"]))
    for c in VECTORS["set_tempo_at"]:
        out["set_tempo_at"].append(model.set_tempo_at(c["in"], c["beat"], c["bpm"]))
    return out


compare("Live", python_results())
check("Live: Live のエンベロープ（点と点の間は直線）をテンポマップにする",
      model.tempo_events_to_map([(-63072000, 60), (4, 60), (8, 120), (12, 200)])
      == [[0.0, 60.0], [4.0, 60.0, 1], [8.0, 120.0, 1], [12.0, 200.0]])
check("Live: .als の拍子の値", model._decode_als_sig(201) == (4, 4) and model._decode_als_sig(200) == (3, 4)
      and model._decode_als_sig(6 + 3 * 99) == (7, 8))


def live_follow_checks():
    """Live の追従: ルームから届いたものだけに追従し、Live 自身のオートメーションはむやみに上書きしない。"""
    import Live as mock_live
    song = mock_live.Song()
    warnings = []
    m = model.Model(lambda: song, lambda s: None, warnings.append)
    m.rebuild(announce=False)
    timing = ("tempo", "sig", "tempo_map", "sig_map")

    def play(points):
        sent = []
        for beat, bpm in points:
            song.current_song_time = beat
            if bpm is not None:
                song.tempo = bpm  # Live のオートメーションが動かした
            sent += [o["k"] for o in m.collect_changes() if o["k"] in timing]
        return sent

    song.master_track.mixer_device.song_tempo.automation_state = 1
    song.is_playing = True
    sent = play([(0, 120.0), (4, 133.0), (8, 150.0)])
    check("Live: 何も届いていなければ、Live のオートメーションを上書きしない", song.tempo == 150.0 and not sent, (song.tempo, sent))
    m.apply([{"k": "tempo_map", "v": [[0, 128]]}], True, {})
    sent = play([(12, 90.0), (13, None)])
    check("Live: ルームにテンポの変化が無ければ、Live で書いた（まだ保存していない）オートメーションに任せる",
          song.tempo == 90.0 and not sent and any("保存" in w for w in warnings), (song.tempo, sent, warnings))
    m.apply([{"k": "tempo_map", "v": [[0, 100], [8, 140]]}], True, {})
    sent = play([(10, 90.0), (11, None)])
    check("Live: ルームにテンポの変化があれば、それに追従する", song.tempo == 140.0 and not sent, (song.tempo, sent))
    song.master_track.mixer_device.song_tempo.automation_state = 0
    song.is_playing = False


live_follow_checks()

# ------------------------------------------------------------------ REAPER（Lua）

try:
    from lupa import lua54
except ImportError:
    lua54 = None
    print("SKIP REAPER（lupa がありません: pip install lupa）")

if lua54 is not None:
    lua = lua54.LuaRuntime(unpack_returned_tuples=True)
    with open(os.path.join(ROOT, "reaper", "json.lua"), encoding="utf-8") as f:
        lua_json = lua.execute(f.read())
    with open(os.path.join(HERE, "reaper_mock.lua"), encoding="utf-8") as f:
        mock = lua.execute(f.read())
    lua.globals().reaper = mock
    with open(os.path.join(ROOT, "reaper", "model.lua"), encoding="utf-8") as f:
        M = lua.eval("function(src, json) return assert(load(src, '@model.lua'))(json) end")(f.read(), lua_json)

    def to_lua(value):
        return lua_json.decode(json.dumps(value))

    def to_py(value):
        return json.loads(lua_json.encode(value))

    out = {"tempo": [], "sig": [], "close": [], "set_tempo_at": []}
    for c in VECTORS["tempo"]:
        rows = M.normalize_tempo_map(to_lua(c["in"]))
        out["tempo"].append({"out": to_py(rows), "at": [M.tempo_at(rows, s[0]) for s in c.get("at", [])]})
    for c in VECTORS["sig"]:
        rows = M.normalize_sig_map(to_lua(c["in"]))
        out["sig"].append({"out": to_py(rows), "at": [list(M.sig_at(rows, s[0])) for s in c.get("at", [])]})
    # REAPER では「ほぼ同じか」は使わないので、Python の結果をそのまま使う
    out["close"] = [model.tempo_maps_close(c["a"], c["b"]) for c in VECTORS["close"]]
    for c in VECTORS["set_tempo_at"]:
        out["set_tempo_at"].append(to_py(M.set_tempo_at(to_lua(c["in"]), c["beat"], c["bpm"])))
    compare("REAPER", out)

    # --- テンポマーカー・ロケーターの読み書き（モックの REAPER）
    logs = []
    m = M.new(lambda s: logs.append(s), lambda s: logs.append("warn: " + s))

    def tempo_markers():
        """[(拍, BPM, linear, 分子, 分母)]"""
        rows = []
        for i in range(1, len(mock.mock.tempo()) + 1):
            t = mock.mock.tempo()[i]
            rows.append((round(mock.TimeMap2_timeToQN(0, t.time), 6), round(t.bpm, 6), bool(t.linear), t.num, t.den))
        return rows

    def locators():
        return to_py(m.read_locators(m))

    mock.mock.reset(120, 4, 4)
    check("REAPER: マーカーの無いプロジェクト", same(to_py(m.read_tempo_map(m)), [[0, 120]])
          and same(to_py(m.read_sig_map(m)), [[0, 4, 4]]))
    ramp = [[0, 100], [8, 120, 1], [16, 140]]
    m.set_tempo_map(m, to_lua(ramp))
    check("REAPER: 段差とランプを書いて読む", same(to_py(m.read_tempo_map(m)), ramp, 1e-4), to_py(m.read_tempo_map(m)))
    check("REAPER: ランプの後ろのマーカーも正しい拍にある（秒の位置を直す）",
          [r[0] for r in tempo_markers()] == [0, 8, 16] and [r[2] for r in tempo_markers()] == [False, True, False],
          tempo_markers())
    m.set_sig_map(m, to_lua([[0, 3, 4], [12, 7, 8]]))
    check("REAPER: ランプの途中で拍子を変えても、テンポの変化はそのまま", same(to_py(m.read_tempo_map(m)), ramp, 1e-3)
          and same(to_py(m.read_sig_map(m)), [[0, 3, 4], [12, 7, 8]]), (to_py(m.read_tempo_map(m)), to_py(m.read_sig_map(m))))
    check("REAPER: 拍子を変えないマーカーは拍子を持たない（小節がずれない）",
          [(r[0], r[3], r[4]) for r in tempo_markers()] == [(0, 3, 4), (8, 0, 0), (12, 7, 8), (16, 0, 0)], tempo_markers())
    jump = [[0, 100, 1], [16, 140], [16, 120]]
    m.set_tempo_map(m, to_lua(jump))
    got = to_py(m.read_tempo_map(m))
    check("REAPER: ランプの終わりで跳ぶ（行き先は 1 tick 手前）", len(got) == 3 and same(got[0], [0, 100, 1])
          and abs(got[1][0] - (16 - 1 / 960)) < 1e-4 and got[1][1] == 140 and same(got[2], [16, 120]), got)
    check("REAPER: 拍子の変化は残る", same(to_py(m.read_sig_map(m)), [[0, 3, 4], [12, 7, 8]]), to_py(m.read_sig_map(m)))

    m.set_locators(m, to_lua([[4, "Intro"], [16, "Chorus"]]))
    ids = {x.name: x.id for x in mock.mock.markers().values()}
    check("REAPER: ロケーターを書いて読む", locators() == [[4, "Intro"], [16, "Chorus"]], locators())
    m.set_locators(m, to_lua([[6, "Intro"], [16, "Hook"]]))
    after = {x.name: x.id for x in mock.mock.markers().values()}
    check("REAPER: 動かした・名前を変えたロケーターは作り直さない（番号が同じ）",
          locators() == [[6, "Intro"], [16, "Hook"]] and after == {"Intro": ids["Intro"], "Hook": ids["Chorus"]}, (locators(), after))
    m.set_locators(m, to_lua([[6, ""], [24, "Outro"]]))
    check("REAPER: 名前を空にする・消す・足す", locators() == [[6, ""], [24, "Outro"]], locators())
    m.set_tempo_map(m, to_lua([[0, 60]]))
    check("REAPER: テンポを変えてもロケーターは同じ拍のまま", same(locators(), [[6, ""], [24, "Outro"]], 1e-4), locators())

    m.batch = lua.table_from({"tempo_map": True})
    m.apply_one(m, "tempo", 90)
    check("REAPER: 同じまとまりに tempo_map があれば tempo は使わない", same(to_py(m.read_tempo_map(m)), [[0, 60]]))
    m.batch = None
    m.set_tempo_map(m, to_lua(ramp))
    m.apply_one(m, "tempo", 90)
    check("REAPER: 古い相手の tempo は拍 0 の区間だけ変える", same(to_py(m.read_tempo_map(m)), [[0, 90], [8, 120, 1], [16, 140]], 1e-4),
          to_py(m.read_tempo_map(m)))
    m.apply_one(m, "sig", to_lua([6, 8]))
    check("REAPER: 古い相手の sig は拍 0 の拍子だけ変える", same(to_py(m.read_sig_map(m)), [[0, 6, 8], [12, 7, 8]]),
          to_py(m.read_sig_map(m)))
    check("REAPER: エラーが無い", not any(l.startswith("warn") or "failed" in l for l in logs), logs)

# ------------------------------------------------------------------ Bitwig（Java）

javac = shutil.which("javac")
if javac is None:
    print("SKIP Bitwig（javac がありません）")
else:
    out_dir = tempfile.mkdtemp(prefix="dawsync-timing-")
    try:
        src = os.path.join(ROOT, "bitwig", "bridge", "src", "com", "dawsync", "bitwig")
        subprocess.run([javac, "-encoding", "UTF-8", "-d", out_dir, os.path.join(src, "Json.java"),
                        os.path.join(src, "TimingMaps.java"), os.path.join(HERE, "TimingMapsCheck.java")], check=True)
        text = subprocess.run([shutil.which("java") or "java", "-cp", out_dir, "com.dawsync.bitwig.TimingMapsCheck",
                               os.path.join(HERE, "vectors.json")], check=True, capture_output=True, text=True,
                              encoding="utf-8").stdout
        got = json.loads(text)
        compare("Bitwig", got)
        # Bitwig に書く点の並び: 段差は同じ時刻の 2 点、ランプは 1 点
        linear = dict((c["name"], got["linear"][n]) for n, c in enumerate(VECTORS["tempo"]))
        check("Bitwig: 段差は同じ時刻の 2 点にして書く", same(linear["段差"], [[0, 100], [16, 100], [16, 140]]))
        check("Bitwig: ランプはそのまま書く", same(linear["Live のエンベロープ（段差とランプ）"],
                                             [[0, 100], [8, 100], [8, 120], [16, 140]]))
        check("Bitwig: ランプの終わりで跳ぶ", same(linear["ランプの終わりで跳ぶ"], [[0, 100], [16, 140], [16, 120]]))
    finally:
        shutil.rmtree(out_dir, ignore_errors=True)

failed = [n for n, ok in results if not ok]
print()
print("%d / %d passed" % (len(results) - len(failed), len(results)))
sys.exit(1 if failed else 0)
