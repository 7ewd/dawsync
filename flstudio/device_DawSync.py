# name=DAW Sync
# url=https://github.com/7ewd/dawsync
#
# DAW Sync for FL Studio（MIDI スクリプト）
#
# FL Studio の中で動き、同じ PC の DawSync アプリとやりとりする。やりとりの中身は Live の Remote Script
# （multi.py）と同じ形なので、アプリからは Live と同じに見える（扱えるキーは hello の keys で知らせる）。
#
# FL Studio の MIDI スクリプトの API では、ピアノロールのノート・プレイリストのクリップは読み書きできず、
# トラックも作れない。同期するのは次のものだけ:
#   tempo / tempo_map   プロジェクトのテンポ。テンポの変化（tempo_map）は FL Studio に書き込めないので、
#                       再生中は今の位置の値に追従させる（Live の model.py と同じやり方）
#   sig / sig_map       プロジェクトの拍子（書き込みは FL Studio 2024 以降）。途中の拍子の変化は書き込めないので
#                       最初の拍子だけ。分母は API で読めないので、最後に合わせた値（無ければ 4）とみなす
#   locators            相手のロケーターを FL Studio のマーカーとして足す（API で消す・動かす・位置を読むことは
#                       できないので、足すだけ。FL Studio で置いたマーカーは相手に送らない）
#
# FL Studio 2025 以降のスクリプトは別のインタープリターで動き、通信（socket）もスレッドも使えない。ファイルも
# このスクリプトのフォルダの中しか読み書きできず（外は SystemError になる）、フォルダを作る・ファイルを消す・
# 置き換えることもできない（FL Studio 2026.1.7 で確かめた）。そのため、アプリとはこのフォルダの中の決まった
# 2 つのファイルを書き直し合ってやりとりする（アプリの側は src/DawSync.Core/Sync/FlStudioRelay.cs。フォルダもアプリが作る）:
#   <このフォルダ>/ipc/<ポート>/to_fl.txt    アプリ → FL Studio（アプリだけが書く）
#                              from_fl.txt  FL Studio → アプリ（このスクリプトだけが書く）
# 中身はどちらも「見出しの行」「番号 JSON」の行…「end」。相手がまだ受け取っていない（ack の来ていない）
# メッセージを毎回全部書き、読む側は前に読んだ番号より後のものだけを扱う。消せなくても、書いている途中を
# 読んでも（最後の end が無いので読み直す）壊れない。
#   to_fl.txt   の見出し: dawsync-app 1 <接続ごとの番号> <時刻> <アプリが読んだ FL の番号の持ち主> <その番号>
#   from_fl.txt の見出し: dawsync-fl 1 <このスクリプトの番号> <時刻> <アプリの接続ごとの番号> <読んだ番号>
#
# 入れ方: FL Studio のユーザーデータの Settings/Hardware/DawSync/ に置き（アプリのボタンで入る。FL Studio は起動したときに
# だけスクリプトを探すので、そのあと再起動する）、MIDI 設定（F10）で MIDI 入力ポートの Controller type を「DAW Sync」にし、
# 入力とそれと同じ名前の出力の Port に同じ番号を設定する（番号が無いと、FL Studio は OnIdle を呼ばないので何も動かない）。
#
# FL Studio 2026 の Python 3.12 で動き、古い FL Studio の Python 3.9 でも動くように書くこと。

import json
import os
import re
import sys
import time

try:
    import arrangement
    import general
    import midi
    import mixer
    import transport
    import ui
except ImportError:  # FL Studio の外（テストはモックを sys.modules に入れてから読み込む）
    arrangement = general = midi = mixer = transport = ui = None

# このスクリプトのフォルダ（FL Studio はここを今のフォルダにして読み込む。__file__ は無い）
SCRIPT_DIR = os.getcwd()

SCRIPT_VERSION = "1.1.0"
PROTOCOL = 2
APP_PORT = 47400
# 相手の印（見出しの時刻）がこれだけ古くなったら切れたとみなす。FL Studio はダイアログを開いている間などに
# スクリプトを呼ばないことがあるので、余裕を持たせる（アプリ側の FlStudioRelay も同じ長さ）
ALIVE_TIMEOUT = 15
POLL_INTERVAL = 0.05     # OnIdle は 1 秒に 20 回ほど呼ばれる。ファイルを読むのはこれより間をあけない
HEARTBEAT = 1.0          # 変わったことが無くても、これだけたったら from_fl.txt を書き直す（動いている印）
MAX_OUTBOX = 5000        # アプリが受け取らないまま溜まったら、つなぎ直す
LOG_LIMIT = 1 << 20      # flstudio.log がこれより大きくなったら空にする
# アプリに知らせる「扱えるキー」。アプリはこれ以外のキー（トラック・クリップなど）を送ってこない
KEYS = ["tempo", "tempo_map", "sig", "sig_map", "locators"]
SONG_MODE = 1            # transport.getLoopMode(): 0 = パターン、1 = ソング
ABS_TICKS = 2            # transport.getSongPos() の単位: 拍の頭からの tick
UNNAMED_MARKER = "DAW Sync"  # 名前の無いロケーターのマーカー名（FL Studio では名前の無いマーカーを数えられない）


# ---------------------------------------------------------------- テンポ・拍子・ロケーター
#
# tempo_map の行は [拍, BPM] か [拍, BPM, 1]。3 つ目が 1 の点からは次の点の BPM まで直線で変わる（ランプ）。
# 規則は Live の model.py・REAPER の model.lua・Bitwig の TimingMaps.java と同じ（tests/timing で同じ例を確かめる）。

TEMPO_EPS = 0.002
RAMP_STEP = 0.25  # ランプに追従させるときの BPM の刻み


def _round(x, digits=5):
    r = round(float(x), digits)
    return 0.0 if r == 0 else r


def _flag(value):
    if isinstance(value, bool):
        return value
    try:
        return float(value) != 0.0
    except (TypeError, ValueError):
        return False


def normalize_tempo_map(rows):
    """テンポの点を決まった形にする（並べる・意味の無い点を除く・拍 0 から始める）。読めなければ []。"""
    points = []
    for row in rows or ():
        if not isinstance(row, (list, tuple)) or len(row) < 2:
            continue
        try:
            beat, bpm = float(row[0]), float(row[1])
        except (TypeError, ValueError):
            continue
        if beat != beat or bpm != bpm or bpm <= 0 or abs(beat) == float("inf") or bpm == float("inf"):
            continue
        points.append([_round(max(0.0, beat)), _round(bpm, 3), len(row) > 2 and _flag(row[2])])
    if not points:
        return []
    points.sort(key=lambda p: p[0])
    if points[0][0] > 0:
        points.insert(0, [0.0, points[0][1], False])
    out = []
    for n, (beat, bpm, linear) in enumerate(points):
        nxt = points[n + 1] if n + 1 < len(points) else None
        ramp = bool(linear and nxt is not None and nxt[0] > beat and nxt[1] != bpm)
        current = [beat, bpm, ramp]
        if out and out[-1][0] == beat:
            ramp_end = len(out) >= 2 and out[-2][2] and out[-2][0] < beat
            if ramp_end and out[-1][1] != bpm:
                out.append(current)
            else:
                out[-1] = current
            continue
        if out and not out[-1][2] and out[-1][1] == bpm and not ramp:
            continue
        out.append(current)
    n = 1
    while n < len(out) - 1:
        a, q, c = out[n - 1], out[n], out[n + 1]
        if a[2] and q[2] and a[0] < q[0] < c[0] \
                and abs(a[1] + (c[1] - a[1]) * (q[0] - a[0]) / (c[0] - a[0]) - q[1]) <= TEMPO_EPS:
            del out[n]
            continue
        n += 1
    out[-1][2] = False
    return [[b, v, 1] if r else [b, v] for b, v, r in out]


def tempo_at(rows, beat):
    """決まった形のテンポマップの、拍 beat での BPM（ちょうど点の上なら、その点からの値）。"""
    if not rows:
        return None
    index = -1
    for n, row in enumerate(rows):
        if float(row[0]) <= beat + 1e-9:
            index = n
        else:
            break
    if index < 0:
        return float(rows[0][1])
    row = rows[index]
    if len(row) > 2 and _flag(row[2]) and index + 1 < len(rows):
        b0, v0 = float(row[0]), float(row[1])
        b1, v1 = float(rows[index + 1][0]), float(rows[index + 1][1])
        if b1 > b0:
            return v0 + (v1 - v0) * (beat - b0) / (b1 - b0)
    return float(row[1])


def _in_ramp(rows, beat):
    for n, row in enumerate(rows or ()):
        if n + 1 < len(rows) and float(row[0]) <= beat < float(rows[n + 1][0]):
            return len(row) > 2 and _flag(row[2])
    return False


def set_tempo_at(rows, beat, bpm):
    """拍 beat を含む区間（その前の最後の点）の BPM を変える。"""
    rows = [list(r) for r in normalize_tempo_map(rows)] or [[0.0, bpm]]
    index = 0
    for n, row in enumerate(rows):
        if float(row[0]) <= beat + 1e-9:
            index = n
    rows[index][1] = _round(bpm, 3)
    return normalize_tempo_map(rows)


def normalize_sig_map(rows):
    """拍子の変わり目 [[拍, 分子, 分母], ...] を決まった形にする。読めなければ []。"""
    points = []
    for row in rows or ():
        if not isinstance(row, (list, tuple)) or len(row) < 3:
            continue
        try:
            beat, num, den = float(row[0]), int(round(float(row[1]))), int(round(float(row[2])))
        except (TypeError, ValueError, OverflowError):
            continue
        if beat != beat or abs(beat) == float("inf") or num < 1 or den < 1:
            continue
        points.append([_round(max(0.0, beat)), num, den])
    if not points:
        return []
    points.sort(key=lambda p: p[0])
    if points[0][0] > 0:
        points.insert(0, [0.0, points[0][1], points[0][2]])
    same_beat = []
    for p in points:
        if same_beat and same_beat[-1][0] == p[0]:
            same_beat[-1] = p
        else:
            same_beat.append(p)
    out = []
    for p in same_beat:
        if not out or out[-1][1:] != p[1:]:
            out.append(p)
    return out


def sig_at(rows, beat):
    current = None
    for row in rows or ():
        if float(row[0]) <= beat + 1e-9 or current is None:
            current = [int(row[1]), int(row[2])]
        else:
            break
    return current


def set_sig_at(rows, beat, num, den):
    rows = [list(r) for r in normalize_sig_map(rows)] or [[0.0, num, den]]
    index = 0
    for n, row in enumerate(rows):
        if float(row[0]) <= beat + 1e-9:
            index = n
    rows[index][1], rows[index][2] = int(num), int(den)
    return normalize_sig_map(rows)


def normalize_locators(rows):
    """ロケーター [[拍, 名前], ...] を拍・名前の順に並べる。"""
    result = []
    for row in rows or ():
        if not isinstance(row, (list, tuple)) or len(row) < 1:
            continue
        try:
            beat = float(row[0])
        except (TypeError, ValueError):
            continue
        if beat != beat or abs(beat) == float("inf"):
            continue
        name = row[1] if len(row) > 1 and row[1] is not None else ""
        result.append([_round(max(0.0, beat)), str(name)])
    return sorted(result, key=lambda r: (r[0], r[1]))


def _finite(value):
    """NaN・無限大を 0 に置き換えた値（アプリ側の JSON で読めず、変更がまるごと捨てられないように）"""
    if isinstance(value, float):
        return value if value == value and value not in (float("inf"), float("-inf")) else 0.0
    if isinstance(value, dict):
        return dict((k, _finite(v)) for k, v in value.items())
    if isinstance(value, (list, tuple)):
        return [_finite(v) for v in value]
    return value


def _encode(msg):
    try:
        return json.dumps(msg, ensure_ascii=True, separators=(",", ":"), allow_nan=False)
    except ValueError:
        return json.dumps(_finite(msg), ensure_ascii=True, separators=(",", ":"))


# ---------------------------------------------------------------- FL Studio の API
#
# FL Studio のバージョンで無い関数があるので、ここでまとめて確かめて使う。

def _midi(name, fallback):
    return getattr(midi, name, fallback)


class Fl(object):

    def tempo(self):
        value = float(mixer.getCurrentTempo())
        if value > 1000:  # 古い FL Studio は BPM の 1000 倍を返す（FL Studio のテンポは 10〜522）
            value /= 1000.0
        return _round(value, 3)

    def set_tempo(self, bpm):
        # 値は BPM の 1000 倍。オートメーションの記録や取り消しの履歴に残さない
        flags = _midi("REC_Control", 973) | _midi("REC_UpdateControl", 1 << 4) | _midi("REC_NoSaveUndo", 1 << 15)
        general.processRECEvent(_midi("REC_Tempo", 0x4000 * 0x10000 + 5), int(round(bpm * 1000)), flags)

    def ppq(self):
        try:
            value = int(general.getRecPPQ())
        except Exception:  # noqa: BLE001
            value = 0
        return value if value > 0 else 96

    def numerator(self):
        """プロジェクトの拍子の分子（API では分母は読めない）。"""
        try:
            n = int(round(float(general.getRecPPB()) / self.ppq()))
        except Exception:  # noqa: BLE001
            return None
        return n if 1 <= n <= 64 else None

    def can_set_sig(self):
        return hasattr(general, "setNumerator") and hasattr(general, "setDenominator")

    def set_sig(self, num, den):
        general.setNumerator(int(num))
        general.setDenominator(int(den))

    def playing(self):
        try:
            return bool(transport.isPlaying())
        except Exception:  # noqa: BLE001
            return False

    def position(self):
        """ソングの再生位置（拍）。パターンを鳴らしているときはソングの位置が無いので 0。"""
        try:
            if transport.getLoopMode() != SONG_MODE:
                return 0.0
            return max(0.0, float(transport.getSongPos(ABS_TICKS)) / self.ppq())
        except Exception:  # noqa: BLE001
            return 0.0

    def safe(self):
        try:
            return bool(general.safeToEdit()) if hasattr(general, "safeToEdit") else True
        except Exception:  # noqa: BLE001
            return True

    def marker_names(self):
        """プレイリストのマーカーの名前（数を返す API が無いので、名前が空になるところまで読む）。"""
        names = []
        for i in range(1000):
            try:
                name = arrangement.getMarkerName(i)
            except Exception:  # noqa: BLE001
                break
            if not name:
                break
            names.append(name)
        return names

    def add_marker(self, beat, name):
        arrangement.addAutoTimeMarker(int(round(beat * self.ppq())), name)

    def version(self):
        """「26.1.7」の形（ui.getVersion() は「Producer Edition v26.1.7 [build 5653]」の形）。"""
        try:
            text = str(ui.getVersion())
        except Exception:  # noqa: BLE001
            return ""
        match = re.search(r"\d+\.\d+(\.\d+)?", text)
        return match.group(0) if match else text

    def api_version(self):
        try:
            return int(general.getVersion())
        except Exception:  # noqa: BLE001
            return 0


# ---------------------------------------------------------------- ファイルでのやりとり

# FL Studio が許さない読み書きは OSError ではなく SystemError になるので、どちらも失敗として扱う

def _read_text(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except (OSError, ValueError, SystemError):
        return None


def _write_text(path, text):
    try:
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        return True
    except (OSError, ValueError, SystemError):
        return False


class Link(object):
    """to_fl.txt / from_fl.txt でアプリとやりとりする（ファイルの形は最初のコメントを参照）。"""

    def __init__(self, base, log):
        self.base = base
        self.log = log
        self.instance = "%x%05x" % (int(time.time()), int(time.perf_counter() * 1000000) & 0xFFFFF)
        self.session = None     # 今つながっているアプリの接続ごとの番号
        self.connected = False
        self.in_seq = 0         # アプリから受け取った最後の番号
        self.out_seq = 0
        self.outbox = []        # [(番号, JSON)] アプリがまだ受け取っていないもの
        self.app_seen = float("-inf")  # 最後に新しいアプリの印を読めた時刻
        self.dirty = True
        self.last_write = 0.0
        self.last_text = None

    @property
    def in_path(self):
        return os.path.join(self.base, "to_fl.txt")

    @property
    def out_path(self):
        return os.path.join(self.base, "from_fl.txt")

    def poll(self, now):
        """
        to_fl.txt を読む。戻り値は (起きたこと, 届いたメッセージ)。
        起きたことは "connect"（新しくつながった・アプリがつなぎ直した）・"disconnect"・None。
        """
        event = None
        messages = []
        text = _read_text(self.in_path)
        lines = text.split("\n") if text else []
        while lines and lines[-1] == "":
            lines.pop()
        header = lines[0].split() if lines else []
        # 書いている途中（end が無い）・知らない形のものは読まない（次の回にもう一度読む）
        complete = len(lines) >= 2 and lines[-1] == "end" and len(header) >= 6 and header[0] == "dawsync-app"
        session = None
        if complete:
            try:
                stamp = float(header[3])
            except ValueError:
                stamp = 0.0
            if abs(time.time() - stamp) <= ALIVE_TIMEOUT:
                self.app_seen = now
                session = header[2]
        alive = now - self.app_seen <= ALIVE_TIMEOUT
        if session is not None and session != self.session:
            # アプリが新しくつながった（アプリの再起動・つなぎ直し）。前の接続のやりとりは捨てる
            self.session = session
            self.connected = True
            self.in_seq = 0
            self.out_seq = 0
            self.outbox = []
            self.dirty = True
            event = "connect"
            if header[4] not in ("-", self.instance):
                # このスクリプトが読み込み直された（前のスクリプトあてのメッセージが残っている）。それは読まない
                # （アプリは番号の持ち主が変わったのを見て、つなぎ直す）
                for line in lines[1:-1]:
                    try:
                        self.in_seq = max(self.in_seq, int(line.partition(" ")[0]))
                    except ValueError:
                        pass
        elif not alive and self.connected:
            self.connected = False
            self.session = None
            self.outbox = []
            self.dirty = True
            event = "disconnect"
        if not (complete and self.connected and header[2] == self.session):
            return event, messages
        # アプリが受け取ったもの（このスクリプトのものへの返事なら）は送らなくてよい
        if header[4] == self.instance:
            try:
                ack = int(header[5])
            except ValueError:
                ack = 0
            if self.outbox and self.outbox[0][0] <= ack:
                self.outbox = [m for m in self.outbox if m[0] > ack]
                self.dirty = True
        for line in lines[1:-1]:
            seq_text, _, body = line.partition(" ")
            try:
                seq = int(seq_text)
            except ValueError:
                continue
            if seq <= self.in_seq:
                continue
            self.in_seq = seq
            self.dirty = True
            try:
                msg = json.loads(body)
            except ValueError:
                self.log("bad message")
                continue
            if isinstance(msg, dict):
                messages.append(msg)
        return event, messages

    def send(self, msg):
        if not self.connected:
            return
        if len(self.outbox) >= MAX_OUTBOX:
            # アプリが受け取っていない。つなぎ直してもらう（次の to_fl.txt で、また新しくつながる）
            self.log("outbox overflow; reconnecting")
            self.session = None
            self.connected = False
            self.outbox = []
            self.dirty = True
            return
        self.out_seq += 1
        self.outbox.append((self.out_seq, _encode(msg)))
        self.dirty = True

    def write(self, now):
        if not self.dirty and now - self.last_write < HEARTBEAT:
            return
        parts = ["dawsync-fl 1 %s %d %s %d\n" % (self.instance, int(time.time()), self.session or "-", self.in_seq)]
        for seq, body in self.outbox:
            parts.append("%d %s\n" % (seq, body))
        parts.append("end\n")
        if _write_text(self.out_path, "".join(parts)):
            self.dirty = False
            self.last_write = now


# ---------------------------------------------------------------- プロジェクトの状態

def _group(key):
    """一緒に送り・一緒に待つキー（tempo と tempo_map は同じものの 2 つの書き方）。"""
    if key in ("tempo", "tempo_map"):
        return "tempo"
    if key in ("sig", "sig_map"):
        return "sig"
    return key


class Model(object):
    """FL Studio のプロジェクトを「キー → 値」として読み書きする（キーと値の形は Live の model.py と同じ）。"""

    def __init__(self, fl, log, warn):
        self.fl = fl
        self.log = log
        self.warn = warn
        self.tempo_rows = None      # このプロジェクトのテンポの変化（FL Studio には最初の値か今の位置の値しか無い）
        self.sig_rows = None
        self.last = {}              # key -> 最後に送った／反映した値
        self.baseline_next = True   # 最初に読んだ状態は送らない（アプリが必要ならスナップショットを取る）
        self.from_room = False      # ルームから届いたテンポがある（それまでは FL Studio のテンポのまま）
        self.pending_apply = False  # 届いたテンポをまだ FL Studio に入れていない
        self.runtime_tempo = None   # 自分で入れたテンポ（読み返したときにユーザーの変更と取り違えない）
        self.runtime_num = None
        self.applied_sig = None     # 最後に FL Studio に入れた拍子 (分子, 分母)
        self.observed_tempo = None  # 前の回に読んだ値
        self.observed_num = None
        self.last_beat = None
        self.moved = False          # 前の回から再生位置が動いたか
        self.was_playing = False
        self.before_play = None     # 再生を始める直前の (テンポ, 位置)
        self.settle = 0             # 止めたあと、再生位置が落ち着くまで待つ回数
        self.added = set()          # この接続で足したマーカー (拍, 名前)
        self.warned = set()

    def warn_once(self, code, message):
        if code not in self.warned:
            self.warned.add(code)
            self.warn(message)

    def ensure(self):
        if self.tempo_rows is None:
            self.tempo_rows = [[0.0, self.fl.tempo()]]
        if self.sig_rows is None:
            self.sig_rows = [[0.0, self.fl.numerator() or 4, 4]]

    def read(self):
        self.ensure()
        tempo_rows = normalize_tempo_map(self.tempo_rows) or [[0.0, self.fl.tempo()]]
        sig_rows = normalize_sig_map(self.sig_rows) or [[0.0, 4, 4]]
        return {
            "tempo": _round(tempo_at(tempo_rows, 0.0), 3),
            "tempo_map": tempo_rows,
            "sig": [sig_rows[0][1], sig_rows[0][2]],
            "sig_map": sig_rows,
        }

    # ------------------------------------------------------------ FL Studio での変更

    def observe(self):
        """
        FL Studio でユーザーがテンポ・拍子を変えたかを見る（毎回呼ぶ）。
        FL Studio の API ではテンポのオートメーションがあるかを知れないので、オートメーションが動かした値を
        ユーザーの変更と取り違えないよう、次のときだけを変更とする:
          - 止まっていて、再生位置も動いていないときに変わった
          - 再生中に変わり、止めて再生を始めた位置に戻ったときに、始める前と違う値になっている
            （オートメーションなら、同じ位置では同じ値に戻る）
        """
        fl = self.fl
        self.ensure()
        playing = fl.playing()
        beat = fl.position()
        tempo = fl.tempo()
        num = fl.numerator()
        self.moved = self.last_beat is not None and abs(beat - self.last_beat) > 1e-6
        own = self.runtime_tempo is not None and abs(tempo - self.runtime_tempo) < 1e-3
        if not own:
            self.runtime_tempo = None
        changed = False

        if playing and not self.was_playing:
            # 前の回は止まっていた。そのときの値と位置を覚えておく
            self.before_play = (self.observed_tempo, self.last_beat)
            self.settle = 0
        elif not playing and self.was_playing:
            self.settle = 2
        if not playing:
            if self.settle > 0:
                if self.moved:
                    self.settle = 2
                else:
                    self.settle -= 1
                    if self.settle == 0:
                        before = self.before_play
                        self.before_play = None
                        if before is not None and before[0] is not None and before[1] is not None \
                                and abs(beat - before[1]) < 1e-3 and not own and abs(tempo - before[0]) > TEMPO_EPS:
                            changed = self._local_tempo(tempo, beat)
            elif not self.moved and not own and self.observed_tempo is not None \
                    and abs(tempo - self.observed_tempo) > TEMPO_EPS:
                changed = self._local_tempo(tempo, beat)

        if num is not None:
            if self.runtime_num is not None and num != self.runtime_num:
                self.runtime_num = None
            if self.observed_num is not None and num != self.observed_num and num != self.runtime_num:
                den = self.sig_rows[0][2] if self.sig_rows else 4
                self.sig_rows = set_sig_at(self.sig_rows, 0.0, num, den)
                changed = True
            self.observed_num = num
        self.observed_tempo = tempo
        self.last_beat = beat
        self.was_playing = playing
        return changed

    def _local_tempo(self, tempo, beat):
        rows = normalize_tempo_map(self.tempo_rows)
        if len(rows) > 1:
            # 途中でテンポが変わるプロジェクト。今の位置の区間の BPM を変えたことにする
            if abs(tempo_at(rows, beat) - tempo) <= TEMPO_EPS:
                return False
            self.tempo_rows = set_tempo_at(rows, beat, tempo)
        else:
            self.tempo_rows = [[0.0, tempo]]
        return True

    # ------------------------------------------------------------ 届いたテンポへの追従

    def follow(self):
        """
        FL Studio のテンポを、ルームのテンポの変化に追従させる（FL Studio にはテンポのオートメーションを
        書き込めないので、再生中は今の位置の値を入れ続ける。止まっているときは、届いた直後と、再生位置を
        動かしたときだけ合わせる）。
        """
        if not self.from_room or self.tempo_rows is None:
            return
        fl = self.fl
        rows = self.tempo_rows
        varies = len(rows) > 1
        if not (self.pending_apply or (varies and (fl.playing() or self.moved))):
            return
        if not fl.safe():
            return  # プロジェクトを読み込んでいる途中など。次の回に
        self.pending_apply = False
        beat = fl.position()
        target = _round(tempo_at(rows, beat), 3)
        # ランプの途中は 0.25 BPM ずつ変える（細かく変え続けると FL Studio の負担になる）
        step = RAMP_STEP if _in_ramp(rows, beat) else 1e-3
        if abs(fl.tempo() - target) >= step:
            fl.set_tempo(target)
            self.runtime_tempo = target

    def _apply_sig(self):
        num, den = self.sig_rows[0][1], self.sig_rows[0][2]
        if len(self.sig_rows) > 1:
            self.warn_once("sig-map", "FL Studio には途中の拍子の変化を書き込めないので、最初の拍子（%d/%d）だけ合わせました"
                           % (num, den))
        if not self.fl.can_set_sig():
            self.warn_once("sig-api", "この FL Studio ではスクリプトから拍子を変えられないので、手で %d/%d にしてください"
                                      "（FL Studio 2024 以降は自動で合わせます）" % (num, den))
            return
        if self.applied_sig == (num, den) and self.fl.numerator() == num:
            return  # もう同じ（合わせ直すたびに書くと、プロジェクトが変更ありになる）
        self.fl.set_sig(num, den)
        self.applied_sig = (num, den)
        self.runtime_num = num
        self.observed_num = self.fl.numerator() or num

    # ------------------------------------------------------------ 届いた変更

    def apply(self, ops, force, pending):
        """届いた変更を反映する。pending: キー -> 送ったがまだ戻ってきていない変更の数（同じグループのキーも待つ）"""
        self.ensure()
        waiting = set(_group(k) for k, v in pending.items() if v)
        ops = [o for o in ops if isinstance(o, dict) and isinstance(o.get("k"), str)
               and (force or _group(o["k"]) not in waiting)]
        if not ops:
            return
        batch = set(o["k"] for o in ops)
        applied = set()
        for o in ops:
            try:
                if self.apply_one(o["k"], o.get("v"), batch):
                    applied.add(_group(o["k"]))
            except Exception as e:  # noqa: BLE001
                self.log("failed to apply %s: %r" % (o["k"], e))
        # 反映した結果を「最後の値」にする（送り返さない）
        state = self.read()
        for key in state:
            if _group(key) in applied:
                self.last[key] = state[key]

    def apply_one(self, key, value, batch):
        if key == "tempo_map":
            rows = normalize_tempo_map(value)
            if not rows:
                return False
            self.tempo_rows = rows
            self.from_room = True
            self.pending_apply = True
            if len(rows) > 1:
                self.warn_once("tempo-map", "FL Studio の API ではテンポのオートメーションを書き込めないので、"
                                            "届いたテンポの変化には再生中に追従させています（FL Studio のオートメーションには残りません）")
            return True
        if key == "tempo":
            if "tempo_map" in batch:
                return True  # 同じまとまりの tempo_map の方が詳しい（拍 0 の値もそちらに入っている）
            try:
                bpm = float(value)
            except (TypeError, ValueError):
                return False
            if bpm != bpm or bpm <= 0:
                return False
            rows = normalize_tempo_map(self.tempo_rows)
            self.tempo_rows = set_tempo_at(rows, 0.0, bpm) if len(rows) > 1 else [[0.0, _round(bpm, 3)]]
            self.from_room = True
            self.pending_apply = True
            return True
        if key == "sig_map":
            rows = normalize_sig_map(value)
            if not rows:
                return False
            self.sig_rows = rows
            self._apply_sig()
            return True
        if key == "sig":
            if "sig_map" in batch:
                return True
            if not isinstance(value, (list, tuple)) or len(value) < 2:
                return False
            try:
                num, den = int(value[0]), int(value[1])
            except (TypeError, ValueError):
                return False
            if num < 1 or den < 1:
                return False
            self.sig_rows = set_sig_at(self.sig_rows, 0.0, num, den)
            self._apply_sig()
            return True
        if key == "locators":
            if value is not None:
                self.apply_locators(value)
            return True
        return False

    def apply_locators(self, value):
        """
        相手のロケーターを FL Studio のマーカーとして足す。FL Studio の API ではマーカーを消す・動かす・位置を読む
        ことができないので、同じ名前のマーカーが FL Studio に（その数だけ）あれば、もうあるものとみなす。
        """
        rows = normalize_locators(value)
        wanted = set((r[0], r[1] or UNNAMED_MARKER) for r in rows)
        gone = [k for k in self.added if k not in wanted]
        if gone:
            self.warn_once("locators", "FL Studio のマーカーはスクリプトから消したり動かしたりできないので、相手が消した・"
                                       "動かした・名前を変えたロケーターは FL Studio に残っています（手で直してください）")
        if not self.fl.safe():
            return
        have = {}
        for name in self.fl.marker_names():
            have[name] = have.get(name, 0) + 1
        for beat, name in rows:
            name = name or UNNAMED_MARKER
            if have.get(name, 0) > 0:
                have[name] -= 1
                self.added.add((beat, name))
                continue
            if (beat, name) in self.added:
                continue  # 足したばかりで、まだ名前を読めない
            self.fl.add_marker(beat, name)
            self.added.add((beat, name))

    # ------------------------------------------------------------ まとめ

    def collect_changes(self):
        state = self.read()
        if self.baseline_next:
            self.baseline_next = False
            self.last = dict(state)
            return []
        ops = []
        for key in sorted(state):
            if self.last.get(key) != state[key]:
                self.last[key] = state[key]
                ops.append({"k": key, "v": state[key]})
        return ops

    def snapshot(self):
        state = self.read()
        self.last = dict(state)
        self.baseline_next = False
        return [{"k": key, "v": state[key]} for key in sorted(state)]

    def mark_all_sent(self):
        self.last = dict(self.read())
        self.baseline_next = False

    def make_blank(self):
        """まっさらにする。FL Studio のスクリプトではプロジェクトの中身は消せないので、テンポ 120・4/4 にするだけ。"""
        fl = self.fl
        self.tempo_rows = [[0.0, 120.0]]
        self.sig_rows = [[0.0, 4, 4]]
        self.from_room = False
        self.pending_apply = False
        if fl.safe():
            fl.set_tempo(120.0)
            self.runtime_tempo = 120.0
            if fl.can_set_sig():
                fl.set_sig(4, 4)
                self.applied_sig = (4, 4)
                self.runtime_num = 4
        self.observed_tempo = None
        self.observed_num = None
        self.added = set()
        self.mark_all_sent()


# ---------------------------------------------------------------- 全体の流れ

def _ipc_root():
    return os.path.join(SCRIPT_DIR, "ipc")


class DawSync(object):

    def __init__(self, root=None, fl=None):
        self.root = root or _ipc_root()
        self.fl = fl or Fl()
        self.port = APP_PORT
        self.link = None
        self.model = None
        self.pending = {}           # key -> 送ったがまだ ack が来ていない数
        self.last_poll = 0.0
        self.debug_cmd = None
        self.errors = set()
        self.busy = False           # 別の DAW がアプリにつながっていて待っている（2 秒ごとのつなぎ直しを記録しない）

    # ------------------------------------------------------------ 記録

    def log(self, message):
        line = "DAW Sync: " + message
        print(line)
        path = os.path.join(self.root, "flstudio.log")
        try:
            # 大きくなりすぎたら空にしてから書く（ファイルを消せないので）
            mode = "w" if os.path.getsize(path) > LOG_LIMIT else "a"
        except (OSError, SystemError):
            mode = "a"
        try:
            with open(path, mode, encoding="utf-8") as f:
                f.write(time.strftime("%H:%M:%S ") + message + "\n")
        except (OSError, ValueError, SystemError):
            pass

    def warn(self, message):
        self.log("warn: " + message)
        if self.link is not None:
            self.link.send({"t": "warn", "msg": message})

    def guard(self, stage, fn, *args):
        """1 つの処理が失敗しても、他の処理（送る・受け取る）は続ける。同じエラーは 1 回だけ知らせる。"""
        try:
            return fn(*args)
        except Exception as e:  # noqa: BLE001
            detail = "%s: %s: %s" % (stage, type(e).__name__, e)
            if detail not in self.errors and len(self.errors) < 50:
                self.errors.add(detail)
                self.warn("FL Studio のスクリプトでエラーが起きました（%s）。同期は続けています" % detail[:300])
            return None

    # ------------------------------------------------------------ 開発用

    def debug_enabled(self):
        # テストで FL Studio を起動するときだけ、環境変数と印のファイルの両方で有効にする
        # （普段の FL Studio で、ほかのプログラムがファイルを置くだけで操作できないように）
        return os.environ.get("DAWSYNC_DEBUG") == "1" and _read_text(os.path.join(self.root, "debug")) is not None

    def app_port(self):
        if self.debug_enabled():
            text = _read_text(os.path.join(self.root, "port")) or ""
            parts = text.split()
            try:
                if len(parts) >= 2 and float(parts[1]) > time.time() * 1000:
                    return int(parts[0])
            except ValueError:
                pass
        return APP_PORT

    def debug_tick(self):
        """開発用: <ポート>/cmd.py の 1 行目「# 番号」が新しければ実行し、state.json に状態を書く。"""
        if not self.debug_enabled():
            return
        base = self.link.base
        code = _read_text(os.path.join(base, "cmd.py"))
        if code:
            first = code.split("\n", 1)[0]
            if first.startswith("#") and first != self.debug_cmd:
                self.debug_cmd = first
                try:
                    exec(code, {"fl": self.fl, "model": self.model, "script": self})
                    result = "ok"
                except Exception as e:  # noqa: BLE001
                    result = "error: %r" % (e,)
                try:
                    with open(os.path.join(base, "cmd.log"), "a", encoding="utf-8") as f:
                        f.write("%s %s\n" % (first, result))
                except (OSError, ValueError, SystemError):
                    pass
        state = dict(self.model.read())
        state["fl_tempo"] = self.fl.tempo()
        state["fl_numerator"] = self.fl.numerator()
        state["fl_markers"] = self.fl.marker_names()
        _write_text(os.path.join(base, "state.json"), json.dumps(state))

    # ------------------------------------------------------------ 動き

    def attach(self):
        """プロジェクトを開いた（読み直した）。今のプロジェクトの値から始める。"""
        self.model = Model(self.fl, self.log, self.warn)
        self.pending = {}

    def start(self):
        self.port = self.app_port()
        self.link = Link(os.path.join(self.root, str(self.port)), self.log)
        self.attach()
        self.log("DAW Sync for FL Studio %s loaded (port %d)" % (SCRIPT_VERSION, self.port))

    def send_hello(self):
        fl = self.fl
        version = fl.version()
        self.link.send({"t": "hello", "proto": PROTOCOL, "script": "flstudio-" + SCRIPT_VERSION,
                        "live": ("FL Studio " + version).strip(), "daw": "flstudio", "keys": KEYS})
        self.link.send({"t": "api", "info": {
            "daw": "flstudio", "version": version, "api": fl.api_version(),
            "python": sys.version.split()[0],
            "setCurrentTempo": hasattr(mixer, "setCurrentTempo"),
            "setNumerator": hasattr(general, "setNumerator"),
            "addAutoTimeMarker": hasattr(arrangement, "addAutoTimeMarker"),
        }})

    def flush_local(self):
        if not self.link.connected:
            return
        ops = self.model.collect_changes()
        if ops:
            for op in ops:
                self.pending[op["k"]] = self.pending.get(op["k"], 0) + 1
            self.link.send({"t": "ops", "ops": ops})

    def handle(self, msg):
        kind = msg.get("t")
        model = self.model
        if kind != "busy":
            self.busy = False
        if kind == "apply":
            self.flush_local()
            model.apply(msg.get("ops") or [], bool(msg.get("force")), self.pending)
        elif kind == "ack":
            for key in msg.get("keys") or []:
                count = self.pending.get(key, 0) - 1
                if count > 0:
                    self.pending[key] = count
                else:
                    self.pending.pop(key, None)
        elif kind == "reset":
            self.pending = {}
        elif kind == "busy":
            # 同じ PC で別の DAW が先にアプリにつながっている（アプリ側の中継が 2 秒ごとにつなぎ直す）
            if not self.busy:
                self.log("waiting: another DAW (%s) is connected to the app" % msg.get("daw"))
            self.busy = True
        elif kind == "snapshot_req":
            self.flush_local()
            self.link.send({"t": "snapshot", "id": msg.get("id"), "ops": model.snapshot()})
        elif kind == "blank":
            model.make_blank()
            self.link.send({"t": "blanked", "id": msg.get("id")})
        elif kind == "adopt":
            model.mark_all_sent()  # FL Studio にはトラックの ID が無いので、付け替えるものは無い
            self.link.send({"t": "adopted", "id": msg.get("id")})

    def tick(self, now=None):
        now = time.monotonic() if now is None else now
        if now - self.last_poll < POLL_INTERVAL:
            return
        self.last_poll = now
        link = self.link
        if self.app_port() != self.port:
            self.start()  # テスト用のポートに切り替わった
            link = self.link
        event, messages = link.poll(now)
        if event == "connect":
            self.pending = {}
            self.model.mark_all_sent()
            self.model.added = set()  # 前のルームで足したマーカーは、これからのルームのものではない
            self.send_hello()
            if not self.busy:
                self.log("connected to the app")
        elif event == "disconnect":
            self.pending = {}
            if not self.busy:
                self.log("disconnected from the app")
        self.guard("debug", self.debug_tick)
        # 届いた変更を反映する前に、自分の変更を先に送る（ユーザーの操作が上書きされて消えないように）
        self.guard("observe", self.model.observe)
        self.guard("local", self.flush_local)
        for msg in messages:
            self.guard("apply " + str(msg.get("t")), self.handle, msg)
        self.guard("follow", self.model.follow)
        link.write(now)

    def project_loaded(self):
        self.attach()
        if self.link is not None and self.link.connected:
            self.send_hello()  # アプリがルームの状態に合わせ直す


_script = None


def OnInit():
    global _script
    _script = DawSync()
    _script.start()


def OnDeInit():
    global _script
    if _script is not None and _script.link is not None:
        # アプリにすぐ切れたとわかるよう、古い時刻の印を書いておく（消すことはできない）
        _write_text(_script.link.out_path, "dawsync-fl 1 %s 0 - 0\nend\n" % _script.link.instance)
    _script = None


def OnIdle():
    if _script is not None:
        _script.tick()


def OnProjectLoad(status):
    # 100 = 読み込みが終わった（0 = 読み込み開始、101 = 失敗）
    if _script is not None and status == 100:
        _script.guard("load", _script.project_loaded)
