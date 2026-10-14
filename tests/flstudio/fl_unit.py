"""
FL Studio のスクリプト（flstudio/device_DawSync.py）を、モックの FL Studio の API の上で確かめる（アプリは使わない）。

  python tests/flstudio/fl_unit.py

テンポ・拍子・マーカーの読み書き、FL Studio 自身のオートメーションをユーザーの変更と取り違えないこと、
ルームのテンポの変化への追従、ファイルでのやりとり（to_fl.txt / from_fl.txt）を確かめる。
"""
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
import mock_fl  # noqa: E402

MODULES = mock_fl.install()
sys.path.insert(0, os.path.join(ROOT, "flstudio"))
import device_DawSync as dawsync  # noqa: E402

results = []


def check(name, ok, detail=None):
    results.append((name, bool(ok)))
    print(("PASS " if ok else "FAIL ") + name)
    if not ok and detail is not None:
        print("     " + repr(detail))


def new_model(**state):
    st = mock_fl.FlState(**state)
    mock_fl.current = st
    warnings = []
    model = dawsync.Model(dawsync.Fl(), lambda m: None, warnings.append)
    model.collect_changes()  # 最初に読んだ状態は送らない
    return st, model, warnings


def keys(ops):
    return dict((o["k"], o["v"]) for o in ops)


def settle(model, n=3):
    for _ in range(n):
        model.observe()


# ---------------------------------------------------------------- テンポを読む

st, model, _ = new_model(tempo=128.0)
check("テンポを BPM で読む", dawsync.Fl().tempo() == 128.0)
st.tempo_x1000 = True
check("古い FL Studio の 1000 倍の値も BPM にする", dawsync.Fl().tempo() == 128.0)
st.tempo_x1000 = False
check("最初の状態は送らない", model.collect_changes() == [])

# ---------------------------------------------------------------- FL Studio での変更

st, model, _ = new_model(tempo=120.0)
settle(model)
st.tempo = 130.0
model.observe()
got = keys(model.collect_changes())
check("止まっているときに変えたテンポを送る", got.get("tempo") == 130.0 and got.get("tempo_map") == [[0.0, 130.0]], got)
settle(model)
check("同じ値は二度送らない", model.collect_changes() == [])

st, model, _ = new_model(tempo=100.0)
st.automation = lambda beat: 100.0 if beat < 8 else 150.0
settle(model)
st.seek(10)
settle(model)
check("FL Studio のオートメーション: 止まったまま位置を動かして変わったテンポは送らない", model.collect_changes() == [], st.tempo)
st.seek(0)
settle(model)
st.playing = True
for beat in (2, 6, 9, 12):
    st.seek(beat)
    model.observe()
st.playing = False
st.seek(0)
settle(model, 4)
check("FL Studio のオートメーション: 再生中に変わったテンポは送らない（止めると元の値に戻る）", model.collect_changes() == [], st.tempo)

st, model, _ = new_model(tempo=120.0)
settle(model)
st.playing = True
model.observe()
st.seek(4)
model.observe()
st.tempo = 126.0  # 再生中にユーザーが変えた
model.observe()
st.seek(8)
model.observe()
st.playing = False
st.seek(0)
settle(model, 4)
got = keys(model.collect_changes())
check("再生中に変えたテンポは、止めて元の位置に戻ったときに送る", got.get("tempo") == 126.0, got)

st, model, _ = new_model(num=4)
settle(model)
st.num = 3
model.observe()
got = keys(model.collect_changes())
check("FL Studio で変えた拍子を送る（分母は読めないので 4）", got.get("sig") == [3, 4] and got.get("sig_map") == [[0.0, 3, 4]], got)

# ---------------------------------------------------------------- ルームからの変更

st, model, warnings = new_model(tempo=140.0)
model.apply([{"k": "tempo", "v": 125}], True, {})
model.follow()
check("届いたテンポを入れる", st.tempo == 125.0, st.tempo)
settle(model)
check("入れたテンポは送り返さない", model.collect_changes() == [])

model.apply([{"k": "tempo", "v": 90}, {"k": "tempo_map", "v": [[0, 100], [8, 140]]}], True, {})
model.follow()
check("同じまとまりに tempo_map があれば tempo は使わない", st.tempo == 100.0, st.tempo)
st.playing = True
for beat in (2, 6, 9):
    st.seek(beat)
    model.observe()
    model.follow()
check("再生中はテンポの変化に追従する", st.tempo == 140.0, st.tempo)
st.playing = False
st.seek(0)
model.observe()
model.follow()
settle(model)
check("止めて戻ると、その位置のテンポにする", st.tempo == 100.0, st.tempo)
check("追従させたテンポは送らない", model.collect_changes() == [])
check("テンポの変化は書き込めないと 1 回だけ知らせる", sum("オートメーション" in w for w in warnings) == 1, warnings)

model.apply([{"k": "tempo_map", "v": [[0, 100, 1], [16, 140]]}], True, {})
st.playing = True
st.seek(8)
model.observe()
model.follow()
check("ランプの途中の値に追従する", abs(st.tempo - 120.0) <= dawsync.RAMP_STEP, st.tempo)
st.playing = False
st.seek(0)
settle(model)
model.follow()
st.tempo = 111.0  # 止まっているときにユーザーが変えた
model.observe()
got = keys(model.collect_changes())
check("テンポの変化があるときにユーザーが変えたら、その区間の BPM を変える",
      got.get("tempo_map") == [[0.0, 111.0, 1], [16.0, 140.0]], got)

st, model, _ = new_model(tempo=120.0)
model.apply([{"k": "tempo_map", "v": [[0, 90]]}], False, {"tempo": 1})
model.follow()
check("自分の変更が戻ってくるまでは、相手のテンポで上書きしない", st.tempo == 120.0, st.tempo)

st, model, warnings = new_model(num=4)
model.apply([{"k": "sig_map", "v": [[0, 6, 8], [32, 4, 4]]}], True, {})
check("届いた拍子を入れる（分母も）", (st.num, st.den) == (6, 8), (st.num, st.den))
settle(model)
check("入れた拍子は送り返さない", model.collect_changes() == [])
check("途中の拍子の変化は書き込めないと知らせる", any("途中の拍子" in w for w in warnings), warnings)
st.num = 5
model.observe()
check("FL Studio で変えた分子は、最後に合わせた分母と一緒に送る", keys(model.collect_changes()).get("sig") == [5, 8])

st, model, warnings = new_model(num=4)
general = MODULES["general"]
saved = general.setNumerator
del general.setNumerator
try:
    model.apply([{"k": "sig", "v": [7, 8]}], True, {})
finally:
    general.setNumerator = saved
check("拍子を書き込めない古い FL Studio では、手で合わせるよう知らせる", st.num == 4 and any("7/8" in w for w in warnings), warnings)

st, model, warnings = new_model()
model.apply([{"k": "locators", "v": [[16, "Chorus"], [32, "Verse"], [48, ""]]}], True, {})
check("ロケーターをマーカーとして足す（名前が無いものは DAW Sync）",
      sorted(st.markers) == [(1536, "Chorus"), (3072, "Verse"), (4608, "DAW Sync")], st.markers)
model.apply([{"k": "locators", "v": [[16, "Chorus"], [32, "Verse"], [48, ""], [64, "Outro"]]}], True, {})
check("足したマーカーは二重に足さない", sorted(st.markers) == [(1536, "Chorus"), (3072, "Verse"), (4608, "DAW Sync"), (6144, "Outro")],
      st.markers)
other = dawsync.Model(dawsync.Fl(), lambda m: None, warnings.append)
other.apply([{"k": "locators", "v": [[16, "Chorus"], [32, "Verse"], [48, ""], [64, "Outro"]]}], True, {})
check("つなぎ直しても（FL Studio にもうあれば）二重に足さない", len(st.markers) == 4, st.markers)
model.apply([{"k": "locators", "v": [[20, "Chorus"]]}], True, {})
check("消せない・動かせないことを知らせる", len(st.markers) == 4 and any("マーカー" in w for w in warnings), warnings)

st, model, _ = new_model(tempo=140.0, num=3)
model.make_blank()
check("まっさら: テンポ 120・4/4 にする", (st.tempo, st.num, st.den) == (120.0, 4, 4))
settle(model)
check("まっさらにした変更は送らない", model.collect_changes() == [])

st, model, _ = new_model()
model.apply([{"k": "t/1", "v": {"o": 0, "k": "midi"}}, {"k": "c/a/1/0", "v": {"k": "midi", "n": []}}], True, {})
check("扱えないキー（トラック・クリップ）は無視する", model.collect_changes() == [])

# ---------------------------------------------------------------- ファイルでのやりとり

base = tempfile.mkdtemp(prefix="dawsync-fl-")
try:
    mock_fl.current = mock_fl.FlState()
    link = dawsync.Link(base, lambda m: None)
    to_fl = os.path.join(base, "to_fl.txt")
    now = 1000.0

    def write_app(text):
        with open(to_fl, "w", encoding="utf-8") as f:
            f.write(text)

    stamp = int(__import__("time").time())
    write_app("dawsync-app 1 s1 %d - 0\n1 {\"t\":\"reset\"}\n" % stamp)
    event, messages = link.poll(now)
    check("書いている途中（end が無い）は読まない", event is None and messages == [] and not link.connected)
    write_app("dawsync-app 1 s1 %d - 0\n1 {\"t\":\"reset\"}\n2 {\"t\":\"ack\",\"keys\":[]}\nend\n" % stamp)
    event, messages = link.poll(now)
    check("新しい接続を見つけて、メッセージを読む", event == "connect" and [m["t"] for m in messages] == ["reset", "ack"], (event, messages))
    event, messages = link.poll(now + 0.1)
    check("読んだメッセージは二度扱わない", event is None and messages == [])
    link.send({"t": "hello", "x": float("nan")})
    link.send({"t": "ops", "ops": []})
    link.write(now)
    with open(os.path.join(base, "from_fl.txt"), encoding="utf-8") as f:
        lines = f.read().splitlines()
    header = lines[0].split()
    check("from_fl.txt: 見出し・番号・end", header[0] == "dawsync-fl" and header[4] == "s1" and header[5] == "2"
          and lines[-1] == "end" and lines[1].startswith("1 ") and json.loads(lines[1][2:])["x"] == 0.0, lines)
    write_app("dawsync-app 1 s1 %d other 1\nend\n" % stamp)
    link.poll(now + 0.2)
    check("ほかのスクリプトあての受け取りの知らせでは消さない", len(link.outbox) == 2)
    write_app("dawsync-app 1 s1 %d %s 1\nend\n" % (stamp, link.instance))
    link.poll(now + 0.3)
    check("アプリが受け取ったものは送らない", [s for s, _ in link.outbox] == [2])
    write_app("dawsync-app 1 s2 %d - 0\n1 {\"t\":\"snapshot_req\",\"id\":1}\nend\n" % stamp)
    event, messages = link.poll(now + 0.4)
    check("アプリがつなぎ直したら、番号を数え直す", event == "connect" and link.outbox == [] and messages[0]["t"] == "snapshot_req")
    restarted = dawsync.Link(base, lambda m: None)
    write_app("dawsync-app 1 s2 %d %s 0\n1 {\"t\":\"snapshot_req\",\"id\":1}\nend\n" % (stamp, link.instance))
    event, messages = restarted.poll(now + 0.45)
    check("読み込み直したスクリプトは、前のスクリプトあてのメッセージを読まない", event == "connect" and messages == []
          and restarted.in_seq == 1, (event, messages))
    write_app("dawsync-app 1 s2 %d - 1\nend\n" % (stamp - 100))
    event, _ = link.poll(now + 0.5)
    check("古い印だけではすぐには切らない", event is None and link.connected)
    event, _ = link.poll(now + 0.5 + dawsync.ALIVE_TIMEOUT + 1)
    check("アプリの印が古くなったら切れたとみなす", event == "disconnect" and not link.connected)

    # 全体の流れ: hello・スナップショット・反映
    mock_fl.current = mock_fl.FlState(tempo=133.0)
    script = dawsync.DawSync(root=base)
    script.app_port = lambda: 47599
    script.start()
    os.makedirs(script.link.base, exist_ok=True)
    stamp = int(__import__("time").time())
    with open(os.path.join(script.link.base, "to_fl.txt"), "w", encoding="utf-8") as f:
        f.write("dawsync-app 1 abc %d - 0\n1 {\"t\":\"snapshot_req\",\"id\":7}\n"
                "2 {\"t\":\"apply\",\"force\":true,\"ops\":[{\"k\":\"tempo\",\"v\":99}]}\nend\n" % stamp)
    script.tick(2000.0)
    with open(os.path.join(script.link.base, "from_fl.txt"), encoding="utf-8") as f:
        sent = [json.loads(line.split(" ", 1)[1]) for line in f.read().splitlines()[1:-1]]
    hello = next((m for m in sent if m["t"] == "hello"), {})
    snap = next((m for m in sent if m["t"] == "snapshot"), {})
    check("hello で FL Studio と扱えるキーを知らせる", hello.get("daw") == "flstudio" and hello.get("script") == "flstudio-" + dawsync.SCRIPT_VERSION
          and hello.get("keys") == dawsync.KEYS, hello)
    check("スナップショットはテンポ・拍子だけ", snap.get("id") == 7 and keys(snap.get("ops", [])).get("tempo") == 133.0
          and sorted(keys(snap["ops"])) == ["sig", "sig_map", "tempo", "tempo_map"], snap)
    check("届いた変更を反映する", mock_fl.current.tempo == 99.0, mock_fl.current.tempo)
finally:
    shutil.rmtree(base, ignore_errors=True)

failed = [n for n, ok in results if not ok]
print()
print("%d / %d passed" % (len(results) - len(failed), len(results)))
sys.exit(1 if failed else 0)
