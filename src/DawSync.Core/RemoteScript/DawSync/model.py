# Live のセットを「キー → 値」の集まりとして読み書きする。
#
# キーの一覧（<id> はトラック・シーン・デバイスごとの ID。番号ではないので、追加・削除でずれない）
#   tempo, sig                     拍 0 のテンポ、拍子（古いバージョンとの互換。同じまとまりに tempo_map / sig_map があればそちらを使う）
#   locators                       アレンジメントのロケーター [[拍, 名前], ...]
#   tempo_map                      テンポの変化 [[拍, BPM] か [拍, BPM, 1（次の点まで直線）], ...]（normalize_tempo_map を参照）
#   sig_map                        拍子の変化 [[拍, 分子, 分母], ...]
#                                  Live はどちらも書き込めないので再生中に追従させ、Live で書いたものは保存した .als から読む
#   t/<id>                         トラックがあること {"k": midi|audio|group, "o": 並び順, "g": 入っているグループの id}（null = 削除）
#                                  グループは Live の API で作れないので、相手が作ったら手動で作ってもらい、自動でつなぐ
#   t/<id>/name|color              （ミキサー: 音量・パン・センド・ミュート・ソロは各自のものなので同期しない）
#   r/<id>, r/<id>/name|color      リターントラック
#   s/<id>, s/<id>/name|color      シーン
#   （以下の d/ は SYNC_DEVICES が True のときだけ）
#   d/<id>                         デバイス {"at": 置き場所, "o": 並び順, "k": native|plugin|rack|m4l, "c": 種類, "n": 名前}
#   d/<id>/p/<n>                   デバイスのパラメータ [値, 名前]
#   d/<id>/preset                  プラグインのプリセット番号
#   d/<id>/sample                  Simpler のサンプル {"file": パス}
#   d/<id>/c/<n>/vol|pan|mute|name ラックのチェーン
#   c/s/<トラック id>/<シーン id>  セッションのクリップ
#   c/a/<トラック id>/<開始位置>   アレンジメントのクリップ（"dur" がアレンジメント上の長さ）
#
# 並び順 "o" は小数。間に追加するときは前後の値の中間を使うので、同時に別々の場所へ追加しても衝突しない。
# サンプルなどのファイルはここではローカルのパスのまま扱い、アプリ側がハッシュに変換して送受信する。

import gzip
import os
import random

import xml.etree.ElementTree as ET

import collections

import Live

NOTE_TIME_SPAN = 1000000.0

# デバイス（音源・エフェクト・VST）を同期するか。
# False: 各自が自由にかける（音はバウンスしたオーディオで共有する）。True にすると追加・削除・つまみも同期する。
SYNC_DEVICES = False
MAX_ORPHANS = 20000


def _round(x, digits=5):
    return round(float(x), digits)


def _ptr(obj):
    return getattr(obj, "_live_ptr", None) or id(obj)


def _time_key(t):
    return ("%.4f" % float(t)).rstrip("0").rstrip(".")


def _norm_path(p):
    return os.path.normcase(os.path.normpath(p)) if p else p


def _norm_name(s):
    return "".join(ch for ch in (s or "").lower() if ch.isalnum())


def priority(key):
    parts = key.split("/")
    if len(parts) == 2 and parts[0] in ("t", "r", "s"):
        return 0
    if len(parts) == 2 and parts[0] == "d":
        return 1
    if parts[0] == "c":
        return 2
    return 3


def _lis_indices(keys):
    """keys（None は除外）の最長増加部分列の添字。並べ替えで「動かなかった」ものを見つけるのに使う。"""
    tails, tails_idx, prev = [], [], {}
    for idx, k in enumerate(keys):
        if k is None:
            continue
        lo, hi = 0, len(tails)
        while lo < hi:
            mid = (lo + hi) // 2
            if tails[mid] < k:
                lo = mid + 1
            else:
                hi = mid
        prev[idx] = tails_idx[lo - 1] if lo > 0 else None
        if lo == len(tails):
            tails.append(k)
            tails_idx.append(idx)
        else:
            tails[lo] = k
            tails_idx[lo] = idx
    result, i = [], (tails_idx[-1] if tails_idx else None)
    while i is not None:
        result.append(i)
        i = prev[i]
    return set(result)


# ---------------------------------------------------------------- テンポ・拍子・ロケーター
#
# tempo_map の行は [拍, BPM] か [拍, BPM, 1]。3 つ目が 1 の点からは次の点の BPM まで（拍に対して）直線で変わる
# （ランプ）。無い・0 なら次の点まで同じ BPM（段差）。同じ拍に 2 点あるときは、前の点がランプの行き先、後の点が
# その拍からの値（ランプの終わりで跳ぶ）。Live の .als・Bitwig（DAWproject）・REAPER の linear なテンポマーカーと
# 同じ考え方で、古い相手は 3 つ目を読まないので段差として扱う。
# REAPER の model.lua と Bitwig の TimingMaps.java にも同じ規則の実装がある（tests/timing で同じ例を確かめる）。

TEMPO_EPS = 0.002  # これより小さい BPM の差は同じとみなす（丸めや、ランプの途中に入れた点の誤差）


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
            # 同じ拍の点。直前の点がランプの行き先なら残して跳ぶ。そうでなければ、跳ぶ前の値は意味が無いので置き換える
            ramp_end = len(out) >= 2 and out[-2][2] and out[-2][0] < beat
            if ramp_end and out[-1][1] != bpm:
                out.append(current)
            else:
                out[-1] = current
            continue
        if out and not out[-1][2] and out[-1][1] == bpm and not ramp:
            continue  # 前と同じ BPM が続くだけ
        out.append(current)
    # ランプの途中に入った点（REAPER の拍子だけのマーカーなど）で、前後を結ぶ線の上にあるものは除く
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
    """決まった形のテンポマップで、拍 beat がランプの途中か（ちょうど終わりの点の上は含まない）。"""
    for n, row in enumerate(rows or ()):
        if n + 1 < len(rows) and float(row[0]) <= beat < float(rows[n + 1][0]):
            return len(row) > 2 and _flag(row[2])
    return False


RAMP_STEP = 0.25  # ランプに追従させるときの BPM の刻み


def tempo_map_varies(rows):
    """テンポが途中で変わるか（オートメーションがあるか）。"""
    return len(normalize_tempo_map(rows)) > 1


def tempo_maps_close(a, b, tol=0.01):
    """2 つのテンポマップが、どこでもほぼ同じ BPM になるか（書き方の違いは気にしない）。"""
    a, b = normalize_tempo_map(a), normalize_tempo_map(b)
    if not a or not b:
        return not a and not b
    beats = sorted(set(float(r[0]) for r in a) | set(float(r[0]) for r in b))
    samples = [beats[-1] + 1.0]
    for n, beat in enumerate(beats):
        samples.append(beat)
        if beat > 0:
            samples.append(beat - 0.001)
        if n + 1 < len(beats):
            samples.append((beat + beats[n + 1]) / 2.0)
    return all(abs(tempo_at(a, s) - tempo_at(b, s)) <= tol for s in samples)


def tempo_events_to_map(events):
    """[(拍, BPM), ...]（Live のエンベロープのように、点と点の間は直線）をテンポマップにする。"""
    return normalize_tempo_map([[t, v, 1] for t, v in events])


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


def sig_map_varies(rows):
    return len(normalize_sig_map(rows)) > 1


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


def _decode_als_sig(value):
    """.als の拍子の値（(分子 - 1) + 99 × log2(分母)。4/4 なら 201）。"""
    value = int(value)
    return value % 99 + 1, 2 ** (value // 99)


def _read_als_timing(path):
    """
    保存された .als（gzip の XML）から、アレンジメントのテンポと拍子の変化を読む。読めなければ None。
    メイントラックの Tempo / TimeSignature のオートメーション（AutomationTarget の Id で探す）を使う。
    Live は、オートメーションが無くても「最初の値」の点（Time="-63072000"）を 1 つ持っていて、点と点の間は直線。
    """
    try:
        with gzip.open(path, "rb") as stream:
            root = ET.parse(stream).getroot()
    except (OSError, EOFError, ET.ParseError, ValueError):
        return None
    track = None
    for name in ("MainTrack", "MasterTrack"):
        track = root.find("./LiveSet/%s" % name)
        if track is None:
            track = root.find(".//%s" % name)
        if track is not None:
            break
    if track is None:
        return None
    envelopes = track.find("./AutomationEnvelopes/Envelopes")

    def param(tag):
        node = track.find("./DeviceChain/Mixer/%s" % tag)
        return node if node is not None else track.find(".//%s" % tag)

    def events(node):
        target = node.find("./AutomationTarget")
        target_id = target.get("Id") if target is not None else None
        if envelopes is None or target_id is None:
            return []
        for envelope in list(envelopes):
            pointee = envelope.find("./EnvelopeTarget/PointeeId")
            if pointee is not None and pointee.get("Value") == target_id:
                found = envelope.find("./Automation/Events")
                return list(found) if found is not None else []
        return []

    def manual(node):
        value = node.find("./Manual")
        return value.get("Value") if value is not None else None

    tempo = None
    node = param("Tempo")
    if node is not None:
        points = []
        for event in events(node):
            if event.tag.rsplit("}", 1)[-1] != "FloatEvent":
                continue
            try:
                points.append((float(event.get("Time")), float(event.get("Value"))))
            except (TypeError, ValueError):
                continue
        if not points:
            try:
                points = [(0.0, float(manual(node)))]
            except (TypeError, ValueError):
                points = []
        points.sort(key=lambda p: p[0])
        tempo = tempo_events_to_map(points) or None

    sig = None
    node = param("TimeSignature")
    if node is not None:
        rows = []
        for event in events(node):
            if event.tag.rsplit("}", 1)[-1] != "EnumEvent":
                continue
            try:
                num, den = _decode_als_sig(event.get("Value"))
                rows.append([float(event.get("Time")), num, den])
            except (TypeError, ValueError):
                continue
        if not rows:
            try:
                num, den = _decode_als_sig(manual(node))
                rows = [[0.0, num, den]]
            except (TypeError, ValueError):
                rows = []
        sig = normalize_sig_map(rows) or None
    return tempo, sig


class Collection(object):
    """並び順のあるもの（トラック、リターン、シーン、ある場所のデバイス）の扱い方。"""

    def __init__(self, prefix, key, items, fallback, describe, create, delete, move):
        self.prefix = prefix        # "t" / "r" / "s" / "d"
        self.key = key              # デバイスの置き場所（"t/<id>" 等）。デバイス以外は None
        self.items = items          # () -> 今のリスト
        self.fallback = fallback    # index -> 最初に付ける ID
        self.describe = describe    # obj -> dict
        self.create = create        # (pos, value) -> obj or None
        self.delete = delete        # (obj, index)
        self.move = move            # (obj, pos) -> bool


class Model(object):

    def __init__(self, get_song, log, warn):
        self._get_song = get_song
        self.log = log
        self.warn = warn
        self.bindings = {}          # key -> (reader, writer)
        self.last = {}              # key -> 最後に送った / 反映した値
        self.dirty = set()
        self.removed = set()
        self.needs_rebuild = True
        self._removers = []
        self._ids = {}              # live ptr -> id
        self._used = set()
        self._order = {}            # id -> 並び順
        self._objects = {}          # id -> Live のオブジェクト
        self._clip_keys = {}        # クリップの ptr -> キー（開いているクリップを付け替え後も開いたままにするため）
        self._clip_by_key = {}      # キー -> クリップ
        self._arrangement = []      # [クリップ, キー, 開始, 終了]（位置・長さの変化を定期的に見る用）
        self._poll_ticks = 0
        self._cue_removers = []     # ロケーターごとのリスナー（ロケーターが増えたら付け直す）
        self._reset_timing()
        self._collections = {}      # id -> そのオブジェクトが属する Collection
        self._containers = {}       # 置き場所のキー -> (Track / Chain, Collection)
        self._unplaced = set()      # 並べ替えができず、並び順どおりに置けていないもの
        self._initialized = False
        self._baseline_next = False
        self._tag = "%06x" % random.getrandbits(24)
        self._counter = 0
        self._orphans = {}          # まだ無いもの宛ての値（後で作られたら反映）
        self._pending_loads = []    # ブラウザからの読み込み待ち
        self._browser_index = None
        self._warned = set()
        self._pending_groups = {}   # 相手が作ったがこちらではまだ無いグループ id -> 値
        self._desired_group = {}    # トラック id -> 相手側で入っているグループ id
        self._batch_deletes = set() # 今反映しているまとまりで消されるトラック

    @property
    def song(self):
        return self._get_song()

    # ------------------------------------------------------------- ids

    def _new_id(self):
        self._counter += 1
        return "%s%d" % (self._tag, self._counter)

    def id_of(self, obj):
        return self._ids.get(_ptr(obj))

    def _assign(self, obj, fallback):
        p = _ptr(obj)
        i = self._ids.get(p)
        if i is None:
            i = fallback if (not self._initialized and fallback not in self._used) else self._new_id()
            self._ids[p] = i
            self._used.add(i)
        return i

    def adopt(self, mapping):
        """
        ルームと同じセットだとわかったとき、ルームの ID に付け替える。
        対応が取れなかったもの（ルームに無いもの）には新しい ID を振る。そうしないと、
        最初に付けた仮の ID が、ルームにある別のものの ID と偶然同じになることがある。
        """
        renamed = {}
        for i in set(self._ids.values()):
            renamed[i] = mapping[i] if i in mapping else self._new_id()
        self._ids = dict((p, renamed[i]) for p, i in self._ids.items())
        self._order = dict((renamed[i], o) for i, o in self._order.items() if i in renamed)
        self._unplaced = set()
        self._used = self._used | set(self._ids.values())
        self._baseline_next = True
        self.needs_rebuild = True

    def make_blank(self):
        """
        今開いているセットを「まっさら」にする（MIDI トラック 1 本、リターン無し、マスターのエフェクト無し、
        シーン 8 個、テンポ 120、4/4）。人によってテンプレートが違っても同じ状態から始められるように。
        Live の API には「新規セットを開く」が無いので、今のセットの中身を消して作る。
        """
        song = self.song
        song.create_midi_track(len(song.tracks))
        keep = song.tracks[len(song.tracks) - 1]
        while len(song.tracks) > 1:
            # グループを消すと中のトラックも消えるので、毎回数え直す
            index = next(n for n, t in enumerate(song.tracks) if _ptr(t) != _ptr(keep))
            song.delete_track(index)
        while len(song.return_tracks) > 0:
            song.delete_return_track(len(song.return_tracks) - 1)
        master = song.master_track
        while len(master.devices) > 0:
            master.delete_device(len(master.devices) - 1)
        while len(song.scenes) > 8:
            song.delete_scene(len(song.scenes) - 1)
        while len(song.scenes) < 8:
            song.create_scene(len(song.scenes))
        for scene in song.scenes:
            scene.name = ""
        self._reset_timing()
        self._runtime_applying = True
        try:
            song.tempo = 120.0
            song.signature_numerator = 4
            song.signature_denominator = 4
        finally:
            self._runtime_applying = False
        self._tempo_map_shadow = [[0.0, 120.0]]
        self._sig_map_shadow = [[0.0, 4, 4]]
        # 保存されている .als は消す前のセットのもの。そのオートメーションを「Live で書いた」と取り違えないよう、今の中身を覚えておく
        self._poll_saved_timing(song, announce=False)
        self.needs_rebuild = True

    def _reset_timing(self):
        self._last_locators = None
        # 再生中に届いたロケーター（Live は再生位置を動かさないとロケーターを作れないので、止まってから反映する）
        self._pending_locators = None
        self._cues_changed = False
        # このセットのテンポ・拍子の変化（ルームと同じもの）。Live の API ではアレンジメントのオートメーションを
        # 書き込めないので、ほかの DAW から届いたものはここに持っておき、再生中に song.tempo などを追従させる。
        # Live で書いたオートメーションは、保存された .als から読む（_poll_saved_timing）
        self._tempo_map_shadow = None
        self._sig_map_shadow = None
        self._timing_file_stamp = None
        self._file_tempo_map = None   # 最後に読んだ .als のテンポ（Live 自身のエンベロープ）
        self._file_sig_map = None
        self._tempo_follow_cache = None
        self._runtime_last_beat = None
        self._runtime_pending_apply = False
        self._runtime_applying = False
        self._runtime_set_tempo = None
        self._runtime_set_sig = None

    # --------------------------------------------------------- rebuild

    def rebuild(self, announce):
        self.needs_rebuild = False
        self._resolve_pending_loads()
        old_keys = set(self.bindings)
        old_clips = dict((k, _ptr(c)) for k, c in self._clip_by_key.items())
        self._unbind()
        self.bindings = {}
        self._objects = {}
        self._clip_keys = {}
        self._clip_by_key = {}
        self._arrangement = []
        self._collections = {}
        self._containers = {}
        changed_order = self._bind_all()

        baseline = not self._initialized or self._baseline_next or not announce
        self._initialized = True
        self._baseline_next = False
        new_keys = set(self.bindings) - old_keys
        gone = old_keys - set(self.bindings)
        # 同じ位置（キー）のまま、クリップが別のものに置き換わった（分割した前半など）。中身が変わっているかもしれないので見直す
        replaced = set(k for k, c in self._clip_by_key.items() if k in old_clips and old_clips[k] != _ptr(c))

        # 相手の変更を反映した結果、そのまとまりに無いクリップが切り分けられた・消えた（Live は重なったクリップを
        # 切り詰める）。相手の DAW（Bitwig など）は重なったまま置いておくので、こちらの結果を送って揃える
        applying = getattr(self, "_applying", None)
        if announce and not baseline:
            self.dirty.update(replaced)
        if baseline and not announce and applying is not None:
            self.dirty.update(k for k in replaced if k not in applying)
            for k in new_keys:
                if k.startswith("c/") and k not in applying:
                    self.dirty.add(k)
            for k in gone:
                parts = k.split("/")
                if k.startswith("c/") and k not in applying and self.last.get(k) is not None                         and ("t/" + parts[2]) in self.bindings:
                    self.removed.add(k)
            new_keys = set(k for k in new_keys if k not in self.dirty)
            # 同じ位置のまま切り詰められたクリップもあるので、触ったトラックのほかのクリップも見直す（変わっていれば送る）
            touched = set(k.split("/")[2] for k in applying if k and k.startswith("c/") and k.count("/") == 3)
            self.dirty.update(k for k in self.bindings
                              if k.startswith("c/") and k not in applying and k.split("/")[2] in touched)

        if baseline:
            for k in new_keys:
                self.last[k] = self.read(k)
        else:
            self.dirty.update(new_keys)
            self.dirty.update("%s/%s" % (self._collections[i].prefix, i) for i in changed_order if i in self._collections)
            # グループに入れた／出した（並びは変わらない）も拾うため、トラックは全部見直す
            self.dirty.update(k for k in self.bindings if k.startswith("t/") and k.count("/") == 1)
            for k in gone:
                parts = k.split("/")
                if self.last.get(k) is None:
                    continue
                if len(parts) == 2 and parts[0] in ("t", "r", "s", "d"):
                    self.removed.add(k)
                elif parts[0] == "c" and ("t/" + parts[2]) in self.bindings:
                    # クリップの削除（トラックごと消えた場合は送らなくてよい）
                    self.removed.add(k)
        for k in gone:
            if k not in self.removed:
                self.last.pop(k, None)
        # 消えたオブジェクトの ID は忘れる
        alive = set(_ptr(o) for o in self._objects.values())
        self._ids = dict((p, i) for p, i in self._ids.items() if p in alive)
        self._apply_orphans()

    def _unbind(self):
        for remove in self._removers + self._cue_removers:
            try:
                remove()
            except Exception:
                pass
        self._removers = []
        self._cue_removers = []

    def _listen(self, obj, prop, callback, removers=None):
        try:
            getattr(obj, "add_%s_listener" % prop)(callback)
        except Exception:
            return

        def remover():
            if getattr(obj, "%s_has_listener" % prop)(callback):
                getattr(obj, "remove_%s_listener" % prop)(callback)
        (self._removers if removers is None else removers).append(remover)

    def _mark(self, key):
        return lambda: self.dirty.add(key)

    def _structure_changed(self):
        self.needs_rebuild = True

    def _bind(self, key, reader, writer, listen=()):
        self.bindings[key] = (reader, writer)
        for obj, prop in listen:
            self._listen(obj, prop, self._mark(key))

    def read(self, key):
        binding = self.bindings.get(key)
        if binding is None:
            return None
        try:
            return binding[0]()
        except Exception:
            self.log("failed to read %s" % key)
            return None

    # ------------------------------------------------- collections (順序付き)

    def _bind_collection(self, coll):
        """コレクションの各要素に ID と並び順を付け、d/<id> などのキーを作る。並び順を直したものの ID を返す。"""
        objs = list(coll.items())
        ids = [self._assign(o, coll.fallback(n)) for n, o in enumerate(objs)]
        changed = self._fix_orders(ids)
        for obj, i in zip(objs, ids):
            self._objects[i] = obj
            self._collections[i] = coll
            self._bind_item(coll, i, obj)
        return ids, changed

    def _fix_orders(self, ids):
        keys = [(self._order[i], i) if i in self._order and i not in self._unplaced else None for i in ids]
        keep = _lis_indices(keys)
        changed = []
        prev = None
        for n, i in enumerate(ids):
            if n in keep:
                prev = self._order[i]
                continue
            if i in self._unplaced and i in self._order:
                continue
            nxt = None
            for j in range(n + 1, len(ids)):
                if j in keep:
                    nxt = self._order[ids[j]]
                    break
            if prev is None and nxt is None:
                o = float(n)
            elif prev is None:
                o = nxt - 1.0
            elif nxt is None:
                o = prev + 1.0
            else:
                o = (prev + nxt) / 2.0
            self._order[i] = o
            prev = o
            changed.append(i)
        return changed

    def _bind_item(self, coll, i, obj):
        key = "%s/%s" % (coll.prefix, i)

        def read():
            value = {"o": self._order.get(i, 0.0)}
            value.update(coll.describe(obj))
            if coll.key is not None:
                value["at"] = coll.key
            return value

        self.bindings[key] = (read, lambda v: self._write_item(coll.prefix, i, v))

    def _target_index(self, coll, i, o):
        objs = list(coll.items())
        count = 0
        for obj in objs:
            other = self.id_of(obj)
            if other is None or other == i or other not in self._order:
                continue
            if (self._order[other], other) < (o, i):
                count += 1
        return count

    def _write_item(self, prefix, i, value):
        """d/<id> や t/<id> を反映する：無ければ作る、null なら消す、並び順が違えば動かす。"""
        coll = self._collections.get(i)
        obj = self._objects.get(i)
        if value is None:
            if obj is not None and coll is not None:
                objs = list(coll.items())
                for n, o in enumerate(objs):
                    if _ptr(o) == _ptr(obj):
                        coll.delete(o, n)
                        break
            self._order.pop(i, None)
            self.needs_rebuild = True
            return

        o = float(value.get("o", 0.0))
        self._order[i] = o
        if prefix == "d":
            target = self._containers.get(value.get("at"))
            if target is None:
                self._orphans["d/" + i] = value  # 置き場所がまだ無い
                return
            container, target_coll = target
            if obj is not None and coll is not target_coll:
                # 別のトラックへ移動
                self.song.move_device(obj, container, self._target_index(target_coll, i, o))
                self.needs_rebuild = True
                return
            coll = target_coll
        elif coll is None:
            coll = self._root_collection(prefix)
            if coll is None:
                return

        if prefix == "t":
            self._desired_group[i] = value.get("g")
            if obj is not None and value.get("g") != self._describe_track(obj).get("g") and value.get("k") != "group"                     and value.get("g") not in self._pending_groups:
                self._warn_once("regroup:%s:%s" % (i, value.get("g")),
                                "相手がトラック「%s」のグループ分けを変えました。Live の制限で自動では変えられないので、"
                                "同じようにグループに入れる／出すと、自動でつながります" % obj.name)
        pos = self._target_index(coll, i, o)
        if prefix == "t" and obj is not None and value.get("k") in ("midi", "audio") \
                and self._describe_track(obj).get("k") != value.get("k"):
            if _track_has_content(obj):
                # 作り直すと、こちらで入れた音源・エフェクト・ミキサーの設定（同期していないもの）が消えてしまう
                self._warn_once("kind:%s:%s" % (i, value.get("k")),
                                "相手のトラック「%s」は%sトラックですが、こちらのトラックには中身があるので作り直しませんでした"
                                % (obj.name, "MIDI " if value.get("k") == "midi" else "オーディオ"))
            else:
                self._recreate_track(coll, obj, i, pos, value)
                return
        if obj is None:
            created = coll.create(pos, value)
            if created is not None:
                self._ids[_ptr(created)] = i
                self._used.add(i)
            self.needs_rebuild = True
            return

        objs = list(coll.items())
        current = next((n for n, x in enumerate(objs) if _ptr(x) == _ptr(obj)), None)
        if current is not None and current != pos:
            if coll.move(obj, pos):
                self._unplaced.discard(i)
            else:
                self._unplaced.add(i)
            self.needs_rebuild = True

    def _recreate_track(self, coll, old, i, pos, value):
        """バウンスなどで MIDI トラックがオーディオトラックに変わったとき、こちらでも作り直す。"""
        created = coll.create(pos, value)  # Live はトラックを 0 本にできないので、先に作ってから消す
        if created is None:
            return
        self._ids.pop(_ptr(old), None)
        self._ids[_ptr(created)] = i
        for n, x in enumerate(list(coll.items())):
            if _ptr(x) == _ptr(old):
                coll.delete(x, n)
                break
        # 古いトラックにあったクリップのキーは作り直し後に改めて反映させる
        for key in [k for k in self.last if k.startswith("c/s/%s/" % i) or k.startswith("c/a/%s/" % i)]:
            self.last.pop(key, None)
        self.needs_rebuild = True

    def _root_collection(self, prefix):
        for coll in self._root_collections:
            if coll.prefix == prefix:
                return coll
        return None

    # ------------------------------------------------------------ bind all

    def _bind_all(self):
        song = self.song
        changed = []

        # テンポ・拍子。"tempo" / "sig" は拍 0 の値（古い相手との互換）、"tempo_map" / "sig_map" が途中の変化も含めた全体。
        # Live の API ではアレンジメントのオートメーションを書き込めないので、届いた変化は再生中に追従させる
        # （_apply_runtime_maps）。Live で書いたオートメーションは保存された .als から読む（_poll_saved_timing）
        self._bind("tempo", lambda: _round(tempo_at(self._tempo_rows(song), 0.0), 3),
                   lambda v: self._write_tempo(song, v))
        self._bind("sig", lambda: sig_at(self._sig_rows(song), 0.0), lambda v: self._write_sig(song, v))
        self._bind("tempo_map", lambda: [list(r) for r in self._tempo_rows(song)],
                   lambda value: self._write_tempo_map(song, value))
        self._bind("sig_map", lambda: [list(r) for r in self._sig_rows(song)],
                   lambda value: self._write_sig_map(song, value))
        self._listen(song, "tempo", self._tempo_changed)
        self._listen(song, "signature_numerator", self._sig_changed)
        self._listen(song, "signature_denominator", self._sig_changed)

        # アレンジメントのロケーター。位置・名前は読めるが、作る・消すは「再生位置でのトグル」（set_or_delete_cue）
        # しか無く、位置は変えられない（time は読み取りのみ）。なので書き込むときは再生位置を一時的に動かす
        self._bind("locators", lambda: self._read_locators_value(song),
                   lambda value: self._write_locators(song, value))
        self._listen(song, "cue_points", self._cue_points_changed)
        self._relisten_cues(song)

        for prop in ("tracks", "return_tracks", "scenes"):
            self._listen(song, prop, self._structure_changed)

        tracks = Collection("t", None, lambda: song.tracks, lambda n: "t%d" % n,
                            self._describe_track, self._create_track, self._delete_track, self._move_track)
        returns = Collection("r", None, lambda: song.return_tracks, lambda n: "r%d" % n,
                             lambda t: {}, self._create_return, lambda t, n: song.delete_return_track(n),
                             lambda t, pos: False)
        scenes = Collection("s", None, lambda: song.scenes, lambda n: "s%d" % n,
                            lambda s: {}, self._create_scene, lambda s, n: song.delete_scene(n),
                            lambda s, pos: False)
        self._root_collections = [tracks, returns, scenes]

        return_ids, c = self._bind_collection(returns)
        changed += c
        scene_ids, c = self._bind_collection(scenes)
        changed += c
        self._link_manual_groups()
        track_ids, c = self._bind_collection(tracks)
        changed += c

        for sid, scene in zip(scene_ids, song.scenes):
            self._bind_named("s/" + sid, scene, mute_solo=False)

        for rid, track in zip(return_ids, song.return_tracks):
            self._bind_named("r/" + rid, track, mute_solo=True)
            if SYNC_DEVICES:
                changed += self._bind_devices("r/" + rid, track)

        for tid, track in zip(track_ids, song.tracks):
            self._bind_named("t/" + tid, track, mute_solo=True)
            if SYNC_DEVICES:
                changed += self._bind_devices("t/" + tid, track)
            if not getattr(track, "is_foldable", False):
                self._bind_clips(tid, track, scene_ids)

        if SYNC_DEVICES:
            changed += self._bind_devices("m", song.master_track)
        return changed

    # ------------------------------------------------------------ ロケーター

    @staticmethod
    def _read_locators(song):
        result = []
        for cue in list(getattr(song, "cue_points", ()) or ()):
            try:
                result.append([_round(cue.time), str(getattr(cue, "name", ""))])
            except Exception:
                continue
        return sorted(result, key=lambda x: (x[0], x[1]))

    def _read_locators_value(self, song):
        # 再生中に届いてまだ反映していないものは、反映したことにして読む（古い今の状態を「変更」として送らないように）
        if self._pending_locators is not None:
            return [list(r) for r in self._pending_locators]
        return self._read_locators(song)

    def _cue_points_changed(self):
        self._cues_changed = True
        self.dirty.add("locators")

    def _relisten_cues(self, song):
        """ロケーターごとに名前・位置のリスナーを付け直す（増えたロケーターにも付くように）。"""
        for remove in self._cue_removers:
            try:
                remove()
            except Exception:
                pass
        self._cue_removers = []
        self._cues_changed = False
        for cue in list(getattr(song, "cue_points", ()) or ()):
            self._listen(cue, "name", self._mark("locators"), self._cue_removers)
            self._listen(cue, "time", self._mark("locators"), self._cue_removers)

    def _write_locators(self, song, value):
        wanted = []
        for beat, name in normalize_locators(value):
            # Live では同じ位置に 2 つ置けない（同じ位置でもう一度トグルすると消える）。最初のものを使う
            if not wanted or wanted[-1][0] != beat:
                wanted.append([beat, name])
        if getattr(song, "is_playing", False):
            # 再生中に再生位置を動かすと、再生している人の音が飛ぶ。止まってから反映する
            self._pending_locators = wanted
            return
        self._pending_locators = None
        self._apply_locators_now(song, wanted)

    def _apply_locators_now(self, song, wanted):
        current = []
        for cue in list(getattr(song, "cue_points", ()) or ()):
            try:
                current.append((cue, _round(cue.time), str(getattr(cue, "name", ""))))
            except Exception:
                pass
        # 同じ位置・名前のもの、次に同じ位置のもの（名前だけ変わった）を残す。消して作り直さないので、
        # Live の中のロケーターはそのまま（選択なども外れない）
        used = set()
        matched = []
        for beat, name in wanted:
            hit = next((n for n, x in enumerate(current) if n not in used and x[1] == beat and x[2] == name), None)
            if hit is None:
                hit = next((n for n, x in enumerate(current) if n not in used and x[1] == beat), None)
            if hit is not None:
                used.add(hit)
            matched.append((beat, name, hit))
        old_time = getattr(song, "current_song_time", None)
        try:
            for n, (cue, beat, name) in enumerate(current):
                if n not in used:
                    song.current_song_time = beat
                    song.set_or_delete_cue()
            for beat, name, hit in matched:
                cue = current[hit][0] if hit is not None else None
                if cue is None:
                    song.current_song_time = beat
                    song.set_or_delete_cue()
                    cue = next((x for x in list(getattr(song, "cue_points", ()) or ())
                                if abs(float(x.time) - beat) < 1e-4), None)
                if cue is not None and str(getattr(cue, "name", "")) != name:
                    cue.name = name
        finally:
            if old_time is not None:
                try:
                    song.current_song_time = old_time
                except Exception:
                    pass
        self._last_locators = self._read_locators(song)
        self._relisten_cues(song)

    def _flush_pending_locators(self):
        """再生中に届いたロケーターを、止まったら反映する。"""
        song = self.song
        if self._pending_locators is None or getattr(song, "is_playing", False):
            return
        wanted, self._pending_locators = self._pending_locators, None
        undo = getattr(song, "begin_undo_step", None)
        try:
            if undo is not None:
                undo()
            self._apply_locators_now(song, wanted)
        finally:
            if undo is not None:
                try:
                    song.end_undo_step()
                except Exception:
                    pass
        # 届いた値を反映しただけなので送らない（Live が置けなかった分の違いだけは、次に読んだときに送られる）
        self.last["locators"] = [list(r) for r in wanted]
        self.dirty.discard("locators")

    # ------------------------------------------------------------ テンポ・拍子

    def _tempo_rows(self, song):
        self._ensure_timing(song)
        return self._tempo_map_shadow

    def _sig_rows(self, song):
        self._ensure_timing(song)
        return self._sig_map_shadow

    def _ensure_timing(self, song):
        """最初に読むとき、今のセットのテンポ・拍子の変化を決める（保存された .als にオートメーションがあればそれ）。"""
        if self._tempo_map_shadow is not None and self._sig_map_shadow is not None:
            return
        self._poll_saved_timing(song, announce=False)
        if self._tempo_map_shadow is None:
            if self._file_tempo_map and tempo_map_varies(self._file_tempo_map):
                self._tempo_map_shadow = [list(r) for r in self._file_tempo_map]
            else:
                self._tempo_map_shadow = [[0.0, _round(song.tempo, 3)]]
        if self._sig_map_shadow is None:
            if self._file_sig_map and sig_map_varies(self._file_sig_map):
                self._sig_map_shadow = [list(r) for r in self._file_sig_map]
            else:
                self._sig_map_shadow = [[0.0, int(song.signature_numerator), int(song.signature_denominator)]]

    def _cursor(self, song):
        try:
            return float(song.current_song_time)
        except (AttributeError, TypeError, ValueError):
            return 0.0

    def _cursor_moved(self, song):
        """前の tick から再生位置が動いたか（Live 自身のオートメーションが、動かした位置の値にテンポを変えることがある）。"""
        return self._runtime_last_beat is not None and abs(self._cursor(song) - self._runtime_last_beat) > 1e-6

    @staticmethod
    def _live_tempo_automation(song):
        """Live 自身のテンポのオートメーションの状態（0: 無い、1: 再生している、2: 手で変えて止まっている）。"""
        try:
            return int(song.master_track.mixer_device.song_tempo.automation_state)
        except Exception:
            return 0

    def _tempo_changed(self):
        song = self.song
        tempo = _round(song.tempo, 3)
        if self._runtime_applying or (self._runtime_set_tempo is not None and abs(tempo - self._runtime_set_tempo) < 1e-3):
            return  # 届いた変化に追従させたもの
        self._runtime_set_tempo = None
        if self._live_tempo_automation(song) != 0:
            # Live のオートメーションが動かしている（手で変えたものも、Live ではその場だけの上書き）。
            # オートメーション自体の変更は、保存したときに .als から読む
            return
        rows = self._tempo_rows(song)
        if not tempo_map_varies(rows):
            self._tempo_map_shadow = [[0.0, tempo]]
            self.dirty.update(("tempo", "tempo_map"))
            return
        # 途中でテンポが変わるセット。止まっているときに手で変えたら、再生位置の区間の BPM を変えたことにする
        if getattr(song, "is_playing", False) or self._cursor_moved(song):
            return
        beat = self._cursor(song)
        if abs(tempo_at(rows, beat) - tempo) <= TEMPO_EPS:
            return
        self._tempo_map_shadow = set_tempo_at(rows, beat, tempo)
        self.dirty.update(("tempo", "tempo_map"))

    def _sig_changed(self):
        song = self.song
        try:
            sig = [int(song.signature_numerator), int(song.signature_denominator)]
        except (AttributeError, TypeError, ValueError):
            return
        if self._runtime_applying or self._runtime_set_sig == sig:
            return
        self._runtime_set_sig = None
        if getattr(song, "is_playing", False) or self._cursor_moved(song):
            return  # 再生中・位置を動かしたときは、Live の拍子の変化に合わせて変わったもの
        rows = self._sig_rows(song)
        if not sig_map_varies(rows):
            self._sig_map_shadow = [[0.0, sig[0], sig[1]]]
            self.dirty.update(("sig", "sig_map"))
            return
        beat = self._cursor(song)
        if sig_at(rows, beat) == sig:
            return
        self._sig_map_shadow = set_sig_at(rows, beat, sig[0], sig[1])
        self.dirty.update(("sig", "sig_map"))

    def _batch_has(self, key):
        applying = getattr(self, "_applying", None)
        return applying is not None and key in applying

    def _write_tempo(self, song, value):
        if self._batch_has("tempo_map"):
            return  # 同じまとまりの tempo_map の方が詳しい（拍 0 の値もそちらに入っている）
        try:
            bpm = float(value)
        except (TypeError, ValueError):
            return
        rows = self._tempo_rows(song)
        if tempo_map_varies(rows):
            self._tempo_map_shadow = set_tempo_at(rows, 0.0, bpm)
        else:
            self._tempo_map_shadow = [[0.0, _round(bpm, 3)]]
        self._runtime_pending_apply = True
        self._apply_runtime_maps()

    def _write_sig(self, song, value):
        if self._batch_has("sig_map") or not isinstance(value, (list, tuple)) or len(value) < 2:
            return
        try:
            num, den = int(value[0]), int(value[1])
        except (TypeError, ValueError):
            return
        rows = self._sig_rows(song)
        if sig_map_varies(rows):
            self._sig_map_shadow = set_sig_at(rows, 0.0, num, den)
        else:
            self._sig_map_shadow = [[0.0, num, den]]
        self._runtime_pending_apply = True
        self._apply_runtime_maps()

    def _write_tempo_map(self, song, value):
        rows = normalize_tempo_map(value)
        if not rows:
            return
        self._ensure_timing(song)
        self._tempo_map_shadow = rows
        self._runtime_pending_apply = True
        if len(rows) > 1:
            self._warn_once("tempo-map", "Live の API ではアレンジメントのテンポ・オートメーションを書き込めないので、"
                                         "届いたテンポの変化には再生中に追従させています（Live のオートメーションには残りません）")
        self._apply_runtime_maps()

    def _write_sig_map(self, song, value):
        rows = normalize_sig_map(value)
        if not rows:
            return
        self._ensure_timing(song)
        self._sig_map_shadow = rows
        self._runtime_pending_apply = True
        if len(rows) > 1:
            self._warn_once("sig-map", "Live の API ではアレンジメントの拍子の変化を書き込めないので、"
                                       "届いた拍子の変化には再生中に追従させています")
        self._apply_runtime_maps()

    def _follow_tempo(self, song):
        """届いたテンポの変化に song.tempo を追従させるか。Live 自身のオートメーションが同じ変化を
        再生しているなら任せる（追従させて上書きすると、Live のオートメーションが止まってしまう）。"""
        key = (id(self._tempo_map_shadow), id(self._file_tempo_map))
        if self._tempo_follow_cache is None or self._tempo_follow_cache[0] != key:
            same = bool(self._file_tempo_map) and tempo_map_varies(self._file_tempo_map) \
                and tempo_maps_close(self._tempo_map_shadow, self._file_tempo_map, 0.05)
            self._tempo_follow_cache = (key, same)
        return not (self._tempo_follow_cache[1] and self._live_tempo_automation(song) == 1)

    def _apply_runtime_maps(self):
        """Live のテンポ・拍子を、このセットのテンポ・拍子の変化に追従させる。

        Live の API にはアレンジメントのオートメーションの点を書き込む方法が無いので、再生中は今の位置の値を
        song.tempo などに入れ続ける（Global Record と Automation Arm が有効なら、Live 側でオートメーションとして録音される）。
        止まっているときは、届いた直後と、再生位置を動かしたときだけ合わせる（手で変えた値をすぐ上書きしないように）。
        """
        song = self.song
        beat = self._cursor(song)
        moved = self._runtime_last_beat is None or abs(beat - self._runtime_last_beat) > 1e-6
        self._runtime_last_beat = beat
        playing = bool(getattr(song, "is_playing", False))
        tempo_rows, sig_rows = self._tempo_map_shadow, self._sig_map_shadow
        varies = (tempo_rows is not None and len(tempo_rows) > 1) or (sig_rows is not None and len(sig_rows) > 1)
        if not (playing or self._runtime_pending_apply or (moved and varies)):
            return
        self._runtime_pending_apply = False
        if tempo_rows and self._follow_tempo(song):
            target = _round(tempo_at(tempo_rows, beat), 3)
            # ランプの途中は 0.25 BPM ずつ変える（細かく変え続けると、Live の取り消しの履歴がテンポの変更で埋まる）
            step = RAMP_STEP if _in_ramp(tempo_rows, beat) else 1e-3
            try:
                if abs(float(song.tempo) - target) >= step:
                    self._runtime_applying = True
                    try:
                        song.tempo = target
                    finally:
                        self._runtime_applying = False
                    # 通知が後から来ても、自分の変更とわかるように
                    self._runtime_set_tempo = target
            except (AttributeError, TypeError, ValueError):
                pass
        if sig_rows:
            target = sig_at(sig_rows, beat)
            try:
                if [int(song.signature_numerator), int(song.signature_denominator)] != target:
                    self._runtime_applying = True
                    try:
                        song.signature_numerator = target[0]
                        song.signature_denominator = target[1]
                    finally:
                        self._runtime_applying = False
                    self._runtime_set_sig = list(target)
            except (AttributeError, TypeError, ValueError):
                pass

    # ------------------------------------------------- 保存された .als のテンポ・拍子

    def _poll_saved_timing(self, song=None, announce=True):
        """
        保存された .als から、Live のアレンジメントのテンポ・拍子のオートメーションを読む（保存したときだけ）。
        Live の API ではオートメーションの点を読めないので、Live で書いたオートメーションは保存したときに届く。
        .als には、ほかの DAW から届いた変化は入らない（書き込めない）ので、前に読んだときと比べて
        Live のオートメーションが変わったときだけ「変更」とする（そうしないと、保存するたびにルームの
        テンポの変化を古いもので上書きしてしまう）。
        """
        song = song or self.song
        path = getattr(song, "file_path", None)
        if not path:
            return
        try:
            stat = os.stat(path)
            stamp = (int(stat.st_mtime_ns), int(stat.st_size))
        except (OSError, TypeError, ValueError):
            return
        if stamp == self._timing_file_stamp:
            return
        self._timing_file_stamp = stamp
        parsed = _read_als_timing(path)
        if parsed is None:
            return
        tempo, sig = parsed
        old_tempo, old_sig = self._file_tempo_map, self._file_sig_map
        self._file_tempo_map, self._file_sig_map = tempo, sig
        if not announce:
            return
        if tempo and self._tempo_map_shadow is not None and self._edited_on_save(
                tempo, old_tempo, self._tempo_map_shadow, tempo_map_varies,
                lambda a, b: tempo_maps_close(a, b, 0.05)):
            self._tempo_map_shadow = tempo
            self._runtime_pending_apply = True
            self.dirty.update(("tempo", "tempo_map"))
        if sig and self._sig_map_shadow is not None and self._edited_on_save(
                sig, old_sig, self._sig_map_shadow, sig_map_varies,
                lambda a, b: normalize_sig_map(a) == normalize_sig_map(b)):
            self._sig_map_shadow = sig
            self._runtime_pending_apply = True
            self.dirty.update(("sig", "sig_map"))

    @staticmethod
    def _edited_on_save(new, old, shadow, varies, same):
        if same(new, shadow):
            return False  # もう同じ（届いた変化を Live で録音して保存した、など）
        if old is not None and same(new, old):
            return False  # Live のオートメーションは前に保存したときのまま
        if varies(new):
            return True   # Live でオートメーションを書いた・直した
        # オートメーションの無い .als。前はあったなら、Live でオートメーションを消した。
        # 前から無ければ、手で変えたテンポ（それはリスナーで送っている）か、届いた変化に追従させた値なので送らない
        return old is not None and varies(old)

    def _bind_named(self, prefix, obj, mute_solo):
        def setter(name, cast):
            def write(v):
                setattr(obj, name, cast(v))
            return write

        self._bind(prefix + "/name", lambda: obj.name, setter("name", str), [(obj, "name")])
        self._bind(prefix + "/color", lambda: obj.color, setter("color", int), [(obj, "color")])
        # ミュート・ソロは同期しない（聴きたいところを各自が自由に切り替えられるように）


    def _bind_param(self, key, param, named=True):
        def write(v):
            value = v[0] if isinstance(v, list) else v
            param.value = max(param.min, min(param.max, float(value)))

        if named:
            reader = lambda: [_round(param.value), param.name]
        else:
            reader = lambda: _round(param.value)
        self._bind(key, reader, write, [(param, "value")])

    # ------------------------------------------------------------- tracks

    def _describe_track(self, track):
        if getattr(track, "is_foldable", False):
            kind = "group"
        else:
            kind = "midi" if track.has_midi_input else "audio"
        group = getattr(track, "group_track", None) if getattr(track, "is_grouped", False) else None
        return {"k": kind, "g": self.id_of(group) if group is not None else None}

    def _children(self, group):
        return [t for t in self.song.tracks
                if getattr(t, "is_grouped", False) and getattr(t, "group_track", None) is not None
                and _ptr(t.group_track) == _ptr(group)]

    def _link_manual_groups(self):
        """
        相手が作ったグループ（こちらでは作れなかったもの）と同じトラックを、ユーザーが手動でグループにしたら、
        そのグループを相手のグループと同じものとしてつなぐ（以後、名前・音量なども同期される）。
        """
        if not self._pending_groups:
            return
        for track in self.song.tracks:
            if not getattr(track, "is_foldable", False) or _ptr(track) in self._ids:
                continue
            children = set(self.id_of(c) for c in self._children(track))
            for gid, value in list(self._pending_groups.items()):
                wanted = set(t for t, g in self._desired_group.items() if g == gid)
                if wanted and wanted == children:
                    self._ids[_ptr(track)] = gid
                    self._used.add(gid)
                    self._order[gid] = float(value.get("o", 0.0))
                    self.last["t/" + gid] = dict(value)
                    del self._pending_groups[gid]
                    self.warn("グループをつなぎました。これからはグループの名前なども同期されます")
                    break

    def _create_track(self, pos, value):
        kind = value.get("k")
        if kind == "group":
            gid = value.get("_id")
            if gid:
                self._pending_groups[gid] = dict((k, v) for k, v in value.items() if k != "_id")
            self._warn_once("group:%s" % gid,
                            "相手がグループを作りました。Live の制限で自動では作れないので、同じトラックを選んで "
                            "Ctrl+G（Mac は Cmd+G）でグループにしてください。自動でつながります")
            return None
        song = self.song
        pos = min(pos, len(song.tracks))
        if kind == "midi":
            song.create_midi_track(pos)
        else:
            song.create_audio_track(pos)
        self._note_created(song.tracks[pos])
        return song.tracks[pos]

    def _delete_track(self, track, n):
        song = self.song
        if len(song.tracks) <= 1:
            return  # Live はトラックを 0 本にできない
        if getattr(track, "is_foldable", False):
            # グループを消すと中のトラックも全部消えてしまう。相手が「グループ解除」しただけなら消してはいけない
            keep = [c for c in self._children(track) if self.id_of(c) not in self._batch_deletes]
            if keep:
                self._warn_once("ungroup:%s" % self.id_of(track),
                                "相手がグループ「%s」を解除しました。Live の制限で自動では解除できないので、"
                                "グループを選んで Ctrl+Shift+G（Mac は Cmd+Shift+G）で解除してください" % track.name)
                return
        song.delete_track(n)

    def _move_track(self, track, pos):
        mover = getattr(self.song, "move_track", None)
        if mover is None:
            self._warn_once("move_track", "トラックの並べ替えはこの Live では同期できません（中身は同期されます）")
            return False
        try:
            mover(track, pos)
            return True
        except Exception:
            self._warn_once("move_track", "トラックの並べ替えはこの Live では同期できません（中身は同期されます）")
            return False

    def _create_return(self, pos, value):
        song = self.song
        song.create_return_track()
        return song.return_tracks[len(song.return_tracks) - 1]

    def _create_scene(self, pos, value):
        song = self.song
        pos = min(pos, len(song.scenes))
        song.create_scene(pos)
        return song.scenes[pos]

    # ------------------------------------------------------------ devices

    def _bind_devices(self, key, container):
        """container（トラックやチェーン）の中のデバイスを登録する。"""
        self._listen(container, "devices", self._structure_changed)
        coll = Collection(
            "d", key, lambda: container.devices,
            lambda n: "%s.%d" % (key.replace("/", ""), n),
            self._describe_device,
            lambda pos, value: self._create_device(container, pos, value),
            lambda d, n: container.delete_device(n),
            lambda d, pos: self._move_device(d, container, pos))
        self._containers[key] = (container, coll)
        ids, changed = self._bind_collection(coll)
        for did, device in zip(ids, container.devices):
            changed += self._bind_device(did, device)
        return changed

    def _describe_device(self, d):
        cn = d.class_name
        if cn in ("PluginDevice", "AuPluginDevice"):
            kind = "plugin"
        elif cn.startswith("Mx"):
            kind = "m4l"
        elif getattr(d, "can_have_chains", False):
            kind = "rack"
        else:
            kind = "native"
        return {"k": kind, "c": cn, "n": getattr(d, "class_display_name", "") or d.name}

    def _bind_device(self, did, device):
        changed = []
        for n, param in enumerate(device.parameters):
            self._bind_param("d/%s/p/%d" % (did, n), param)

        if device.class_name in ("PluginDevice", "AuPluginDevice") and hasattr(device, "selected_preset_index"):
            def set_preset(v, d=device):
                d.selected_preset_index = int(v)
            self._bind("d/%s/preset" % did, lambda d=device: d.selected_preset_index, set_preset,
                       [(device, "selected_preset_index")])

        if device.class_name == "OriginalSimpler":
            self._bind("d/%s/sample" % did, lambda d=device: _read_sample(d),
                       lambda v, d=device: self._write_sample(d, v), [(device, "sample")])

        if getattr(device, "can_have_chains", False):
            for ci, chain in enumerate(device.chains):
                prefix = "d/%s/c/%d" % (did, ci)
                self._bind(prefix + "/name", lambda c=chain: c.name, lambda v, c=chain: setattr(c, "name", str(v)),
                           [(chain, "name")])
                self._bind(prefix + "/mute", lambda c=chain: bool(c.mute),
                           lambda v, c=chain: setattr(c, "mute", bool(v)), [(chain, "mute")])
                mixer = getattr(chain, "mixer_device", None)
                if mixer is not None:
                    self._bind_param(prefix + "/vol", mixer.volume, named=False)
                    self._bind_param(prefix + "/pan", mixer.panning, named=False)
                changed += self._bind_devices(prefix, chain)
        return changed

    def _move_device(self, device, container, pos):
        try:
            self.song.move_device(device, container, pos)
            return True
        except Exception:
            return False

    def _create_device(self, container, pos, value):
        """相手が追加したデバイスを、こちらでも追加する。VST は同じものが入っているときだけ。"""
        kind, name = value.get("k"), value.get("n", "")
        before = set(_ptr(d) for d in container.devices)

        if kind in ("native", "rack") and hasattr(container, "insert_device"):
            try:
                container.insert_device(name, pos)
                created = self._find_new_device(container, before)
                if created is not None:
                    return created
            except Exception:
                pass

        if kind == "m4l":
            self._warn_once("m4l:" + name, "Max for Live デバイス「%s」はまだ同期できません" % name)
            return None

        item = self._browser_find(kind, name)
        if item is None:
            self._warn_once("missing:" + name, "「%s」がこの PC に無いので追加できませんでした" % name)
            return None
        if not isinstance(container, Live.Track.Track):
            # ブラウザからは「選択中のトラック」にしか読み込めない
            self._warn_once("chainplugin", "ラックの中へのプラグイン追加はまだ同期できません")
            return None

        self._load_from_browser(container, item)
        created = self._find_new_device(container, before)
        if created is None:
            # 読み込みが後から終わることがあるので、しばらく待つ
            self._pending_loads.append([container, value, before, 50])
            return None
        self._move_device(created, container, pos)
        return created

    def _find_new_device(self, container, before):
        for d in container.devices:
            if _ptr(d) not in before and _ptr(d) not in self._ids:
                return d
        return None

    def _resolve_pending_loads(self):
        still = []
        for entry in self._pending_loads:
            container, value, before, ttl = entry
            try:
                created = self._find_new_device(container, before)
            except Exception:
                continue
            if created is None:
                if ttl > 0:
                    entry[3] = ttl - 1
                    still.append(entry)
                continue
            device_id = value.get("_id")
            if device_id:
                self._ids[_ptr(created)] = device_id
                self._used.add(device_id)
                self._order[device_id] = float(value.get("o", 0.0))
            self.needs_rebuild = True
        self._pending_loads = still

    def _load_from_browser(self, container, item):
        """ブラウザの load_item は「選択中のトラック」に読み込むので、一時的に選択を切り替えて戻す。"""
        song = self.song
        view = song.view
        previous = view.selected_track
        try:
            view.selected_track = container
            Live.Application.get_application().browser.load_item(item)
        finally:
            try:
                view.selected_track = previous
            except Exception:
                pass

    def _browser_find(self, kind, name, retry=True):
        if self._browser_index is None:
            self._browser_index = self._build_browser_index()
        wanted = _norm_name(name)
        candidates = self._browser_index.get((kind == "plugin", wanted), [])
        if not candidates and retry:
            self._browser_index = None
            return self._browser_find(kind, name, retry=False)
        if not candidates:
            return None
        # VST2 と VST3 の両方がある場合は VST3 を優先
        candidates.sort(key=lambda c: 0 if "vst3" in c[0] else 1)
        return candidates[0][1]

    def _build_browser_index(self):
        index = {}
        try:
            browser = Live.Application.get_application().browser
        except Exception:
            return index

        def walk(item, path, depth, is_plugin):
            for child in item.children:
                if child.is_loadable:
                    index.setdefault((is_plugin, _norm_name(child.name)), []).append((path.lower(), child))
                if child.is_folder and depth > 0:
                    walk(child, path + "/" + child.name, depth - 1, is_plugin)

        try:
            walk(browser.plugins, "plugins", 4, True)
        except Exception:
            self.log("failed to scan browser plugins")
        for root in ("audio_effects", "instruments", "midi_effects"):
            try:
                walk(getattr(browser, root), root, 0, False)
            except Exception:
                self.log("failed to scan browser %s" % root)
        return index

    def _write_sample(self, device, value):
        if not value or not value.get("file"):
            return
        current = _read_sample(device)
        if current and _norm_path(current.get("file")) == _norm_path(value["file"]):
            return
        device.replace_sample(value["file"])

    # -------------------------------------------------------------- clips

    def _bind_clips(self, tid, track, scene_ids):
        for sid, slot in zip(scene_ids, track.clip_slots):
            self._listen(slot, "has_clip", self._structure_changed)
            if slot.has_clip:
                self._bind_clip("c/s/%s/%s" % (tid, sid), slot.clip, lambda s=slot: s.delete_clip())
        if hasattr(track, "arrangement_clips"):
            self._listen(track, "arrangement_clips", self._structure_changed)
            for clip in track.arrangement_clips:
                key = "c/a/%s/%s" % (tid, _time_key(clip.start_time))
                # 動かしたら位置（= キー）が変わるので作り直す
                self._listen(clip, "start_time", self._structure_changed)
                self._arrangement.append([clip, key, clip.start_time, clip.end_time])
                self._bind_clip(key, clip, lambda c=clip, t=track: t.delete_clip(c))

    def _bind_clip(self, key, clip, delete):
        self._clip_keys[_ptr(clip)] = key
        self._clip_by_key[key] = clip

        def write(v):
            if v is None:
                delete()
                self.needs_rebuild = True
                return
            current = _read_clip(clip)
            if isinstance(v.get("delta"), dict):
                if v.get("k") != current["k"]:
                    # 種類（MIDI / オーディオ）が変わる前のクリップへの編集。ノート全部が付いていないので、
                    # これでは作り直せない（サーバー・ほかの DAW も同じく無視する）
                    return
                v = merge_delta(current, v)
            if v.get("k") != current["k"] or (v["k"] == "audio" and _norm_path(v.get("file")) != _norm_path(current.get("file"))) \
                    or (v.get("dur") is not None and current.get("dur") is not None and v.get("dur") != current.get("dur")):
                # 種類やサンプル・長さが変わった → 作り直す（アレンジメントのクリップの長さは API で変えられない）
                delete()
                self.needs_rebuild = True
                try:
                    created = self._create_clip(key, v)
                except Exception:  # noqa: BLE001
                    created = False
                if not created:
                    # 作れなかった（トラックの種類が違う・ファイルが無い等）。消えたままだと自分だけクリップが無くなり、
                    # しかもその削除が相手に送られてしまうので、元のクリップを作り直して戻す
                    self.log("failed to recreate clip %s, restoring" % key)
                    try:
                        self._create_clip(key, current)
                    except Exception:  # noqa: BLE001
                        self._remember_orphan(key, v)
                return
            if v["k"] == "midi" and v.get("n") != current.get("n"):
                _write_notes(clip, v.get("n", []))
            _write_clip_props(clip, v.get("p", {}), current.get("p", {}))

        listen = [(clip, p) for p in ("name", "color", "looping", "loop_start", "loop_end",
                                      "start_marker", "end_marker", "end_time")]
        if clip.is_midi_clip:
            listen.append((clip, "notes"))
        else:
            listen += [(clip, p) for p in ("gain", "pitch_coarse", "pitch_fine", "warping", "warp_mode", "warp_markers")]
        self._bind(key, lambda: _read_clip(clip), write, listen)

    def _create_clip(self, key, value):
        parts = key.split("/")
        track = self._objects.get(parts[2])
        if track is None or not hasattr(track, "clip_slots") or value is None:
            return False
        old_key = getattr(self, "_moves", {}).get(key)
        if old_key is not None:
            if self._move_clip(key, value, old_key):
                return True
            old = self._clip_by_key.get(old_key)
            if old is not None:
                # 複製できなかった → 元を消して作り直す（元を残すと切れ端になる）
                try:
                    self._objects.get(old_key.split("/")[2]).delete_clip(old)
                except Exception:
                    pass
        kind = value.get("k")
        if (kind == "midi") != bool(track.has_midi_input):
            self._warn_once("kind:" + parts[2] + ":" + str(kind),
                            "「%s」は%sトラックなので、相手の%sクリップは置けませんでした"
                            % (track.name, "MIDI " if track.has_midi_input else "オーディオ",
                               "MIDI " if kind == "midi" else "オーディオ"))
            return False
        if kind == "audio" and not value.get("file"):
            self._warn_once("nofile:" + key, "サンプルファイルが届いていないのでクリップを作れませんでした")
            return False
        length = max(float(value.get("len", 4.0)), 0.25)

        if parts[1] == "s":
            scene = self._objects.get(parts[3])
            index = next((n for n, s in enumerate(self.song.scenes) if scene is not None and _ptr(s) == _ptr(scene)), None)
            if index is None:
                return False
            slot = track.clip_slots[index]
            if slot.has_clip:
                slot.delete_clip()
            if kind == "midi":
                slot.create_clip(length)
            else:
                slot.create_audio_clip(value["file"])
            clip = slot.clip
        else:
            start = float(parts[3])
            if kind == "midi":
                clip = track.create_midi_clip(start, max(float(value.get("dur") or length), 0.25))
            else:
                clip = track.create_audio_clip(value["file"], start)
            if clip is None:
                clip = next((c for c in track.arrangement_clips if abs(c.start_time - start) < 1e-4), None)
            if kind == "audio" and clip is not None and value.get("dur"):
                _set_audio_length(clip, value)
        if clip is None:
            return False
        self._note_created(clip)
        if kind == "midi":
            _write_notes(clip, value.get("n", []))
        _write_clip_props(clip, value.get("p", {}), {})
        return True

    # -------------------------------------------------------------- apply

    def apply(self, ops, force, pending):
        # 自分の変更の方がサーバー上で後なので、自分がまだ ack を受け取っていないキーは無視してよい。
        # ただしクリップは、自分が変えた項目だけを無視する（ノートは足し引きなので、両方の編集を残せる）。
        # pending: key -> 送ったがまだ戻ってきていない変更それぞれの「変えた項目」の集合（"*" は全部）
        kept = []
        for o in ops:
            key, value = o.get("k"), o.get("v")
            waiting = pending.get(key) or []
            if force or not waiting:
                kept.append(o)
            elif value is None:
                # 相手の削除（サーバーには自分の編集より先に届いた）。自分の変更がノートなどの部分的な編集だけなら、
                # その編集はサーバーで「無いクリップへの編集」として捨てられるので、削除を反映する
                if "*" not in set().union(*waiting):
                    kept.append(o)
            elif isinstance(value, dict) and isinstance(value.get("delta"), dict):
                mine = set().union(*waiting)
                if "*" in mine:
                    continue
                fields = [f for f in value["delta"].get("f") or [] if f not in mine]
                kept.append({"k": key, "v": dict(value, delta=dict(value["delta"], f=fields))})
        ops = kept
        if not ops:
            return
        focus = self._remember_focus()
        self._created = set()
        undo = getattr(self.song, "begin_undo_step", None)
        try:
            if undo is not None:
                undo()  # 相手の 1 回の編集は、こちらの Undo でも 1 回で戻るようにする（途中までだけ戻ると壊れた状態になる）
            self._apply_ops(ops)
        finally:
            try:
                if undo is not None:
                    self.song.end_undo_step()
            except Exception:
                pass
            self._restore_focus(focus, ops)
            self._focus_guard = [focus, 10]

    # ------------------------------------------------ 開いているクリップ・トラックを保つ
    #
    # Live の API ではアレンジメントのクリップを動かしたり長さを変えたりできないので、相手がそうしたら
    # クリップを作り直す。そのままだと、こちらで開いて編集していたクリップが画面から消えてしまうので、
    # 作り直したクリップ（動かした場合は移動先のクリップ）を開き直す。選んでいたトラックも元に戻す。

    def _remember_focus(self):
        try:
            view = self.song.view
            clip = getattr(view, "detail_clip", None)
            key = self._clip_keys.get(_ptr(clip)) if clip is not None else None
            clip_view = False
            try:
                clip_view = bool(Live.Application.get_application().view.is_view_visible("Detail/Clip"))
            except Exception:
                pass
            selected = None
            if clip is not None and getattr(clip, "is_midi_clip", False):
                try:
                    selected = [(n.note_id, n.pitch, _round(n.start_time), _round(n.duration))
                                for n in clip.get_selected_notes_extended(0, 128, 0.0, NOTE_TIME_SPAN)]
                except Exception:
                    selected = None
            return {"clip": clip, "key": key, "track": getattr(view, "selected_track", None), "clip_view": clip_view,
                    "selected": selected}
        except Exception:
            return None

    def _restore_focus(self, focus, ops):
        """開いていたクリップ・選んでいたトラック・クリップビューを元に戻す。何か戻したら True。"""
        if not focus:
            return False
        changed = False
        try:
            view = self.song.view
            clip, key, track = focus["clip"], focus["key"], focus["track"]
            target = None
            if clip is not None and key is not None:
                alive = _ptr(clip) in self._clip_keys
                target = clip if alive else self._clip_by_key.get(self._replacement_key(key, ops))
            if track is not None and _ptr(track) in self._ids:
                current = getattr(view, "selected_track", None)
                if current is None or _ptr(current) != _ptr(track):
                    view.selected_track = track
                    changed = True
            if target is not None:
                current = getattr(view, "detail_clip", None)
                if current is None or _ptr(current) != _ptr(target):
                    view.detail_clip = target
                    changed = True
            if target is not None and focus.get("selected"):
                self._restore_note_selection(target, focus["selected"], same=target is clip)
            if changed and focus.get("clip_view"):
                # トラックを作ると Live はそちらを選んで、下の画面もデバイスに切り替える。ノートの画面に戻す
                app_view = Live.Application.get_application().view
                app_view.show_view("Detail/Clip")
        except Exception:
            pass
        return changed

    def _restore_note_selection(self, clip, selected, same):
        """
        選んでいたノートを選び直す（トラックが増えると Live が選択を外してしまうので）。
        クリップを作り直した場合はノートの id が変わるので、音程・位置・長さで探す。
        """
        try:
            notes = clip.get_notes_extended(0, 128, 0.0, NOTE_TIME_SPAN)
            if same:
                alive = set(n.note_id for n in notes)
                ids = [s[0] for s in selected if s[0] in alive]
            else:
                by_place = dict(((n.pitch, _round(n.start_time), _round(n.duration)), n.note_id) for n in notes)
                ids = [by_place[s[1:]] for s in selected if s[1:] in by_place]
            if not ids:
                return
            now = set(n.note_id for n in clip.get_selected_notes_extended(0, 128, 0.0, NOTE_TIME_SPAN))
            if now != set(ids):
                clip.select_notes_by_id(tuple(ids))
        except Exception:
            pass

    def check_focus(self):
        """
        相手の変更を反映した直後の数 tick は、Live が後から（非同期に）作ったトラック・クリップへ
        選択を移すことがあるので、見張って戻す。こちらのユーザーが作ったものへ移った場合はそのまま。
        """
        guard = getattr(self, "_focus_guard", None)
        if not guard:
            return
        focus, ticks = guard
        guard[1] = ticks - 1
        if guard[1] <= 0:
            self._focus_guard = None
        try:
            view = self.song.view
            track = getattr(view, "selected_track", None)
            clip = getattr(view, "detail_clip", None)
            moved = (track is not None and _ptr(track) in self._created) or (clip is not None and _ptr(clip) in self._created)
        except Exception:
            return
        if moved:
            self._restore_focus(focus, [])

    def _note_created(self, obj):
        if not hasattr(self, "_created"):
            self._created = set()
        self._created.add(_ptr(obj))

    def _replacement_key(self, key, ops):
        """消えたクリップ key の代わりのキー（同じキーで作り直した、または同じトラックの別の位置へ動いた）。"""
        values = dict((o.get("k"), o.get("v")) for o in ops)
        if values.get(key) is not None:
            return key
        parts = key.split("/")
        if len(parts) != 4 or parts[1] != "a" or key not in values:
            return None
        prefix = "c/a/%s/" % parts[2]
        created = [k for k, v in values.items() if k.startswith(prefix) and v is not None]
        if len(created) == 1:
            return created[0]
        if created:
            return None
        # 別のトラックへ動かした（消したのが 1 つ、作ったのも 1 つだけのとき）
        removed = [k for k, v in values.items() if k.startswith("c/a/") and v is None]
        created = [k for k, v in values.items() if k.startswith("c/a/") and v is not None]
        return created[0] if len(removed) == 1 and len(created) == 1 else None

    def _apply_ops(self, ops):

        # 並び順は先に全部更新してから、前から順に置いていく（途中の中途半端な順番で動かさないように）
        for op in ops:
            parts = op.get("k", "").split("/")
            value = op.get("v")
            if len(parts) == 2 and parts[0] in ("t", "r", "s", "d") and isinstance(value, dict) and parts[1] in self._objects:
                self._order[parts[1]] = float(value.get("o", 0.0))

        self._batch_deletes = set(o["k"].split("/")[1] for o in ops
                                  if o.get("v") is None and len(o.get("k", "").split("/")) == 2 and o["k"].startswith("t/"))

        def sort_key(op):
            value = op.get("v")
            key = op.get("k", "")
            order = value.get("o", 0.0) if isinstance(value, dict) and isinstance(value.get("o"), (int, float)) else 0.0
            # トラックは作ってから消す（Live はトラックを 0 本にできない）。
            # クリップは消してから作る（先に作ると、重なった古いクリップを Live が切り分けてしまい、切れ端が残る）
            deletions_first = priority(key) != 0
            return (priority(key), (value is not None) if deletions_first else (value is None), order, key)

        self._find_moves(ops)
        self._applying = set(o.get("k") for o in ops)
        try:
            for op in sorted(ops, key=sort_key):
                key, value = op.get("k"), op.get("v")
                self._apply_one(key, value)
                self.dirty.discard(key)
            if self.needs_rebuild:
                self.rebuild(announce=False)
                for key in self._applying:
                    if key in self.bindings and key.startswith("c/"):
                        self.last[key] = self.read(key)
                        self.dirty.discard(key)
        finally:
            self._moves, self._moved_away = {}, set()
            self._applying = None

    # ------------------------------------------------ クリップの移動
    #
    # 相手がクリップを動かすと「元の位置のキーを消す」「新しい位置のキーを作る」の 2 つが届く。
    # そのまま消して作り直すと、クリップの中のエンベロープや MPE（同期していない情報）が消えてしまうので、
    # 消す 1 つと作る 1 つが組になっていたら、duplicate_clip_to_arrangement でクリップごと複製してから元を消す。

    def _find_moves(self, ops):
        self._moves, self._moved_away = {}, set()
        removed, added = {}, {}
        for op in ops:
            parts = op.get("k", "").split("/")
            if len(parts) != 4 or parts[0] != "c" or parts[1] != "a":
                continue
            if op.get("v") is None and op["k"] in self._clip_by_key:
                removed.setdefault(parts[2], []).append(op["k"])
            elif op.get("v") is not None and op["k"] not in self._clip_by_key:
                added.setdefault(parts[2], []).append(op["k"])
        pairs = [(r[0], added[t][0]) for t, r in removed.items() if len(r) == 1 and len(added.get(t, [])) == 1]
        if not pairs and sum(len(r) for r in removed.values()) == 1 and sum(len(a) for a in added.values()) == 1:
            # 別のトラックへ動かした
            pairs = [(list(removed.values())[0][0], list(added.values())[0][0])]
        for old, new in pairs:
            self._moves[new] = old
            self._moved_away.add(old)

    def _move_clip(self, key, value, old_key):
        """old_key のクリップを key の位置へ複製して元を消す。できなかったら False（そのときは作り直す）。"""
        clip = self._clip_by_key.get(old_key)
        parts = key.split("/")
        track = self._objects.get(parts[2])
        old_track = self._objects.get(old_key.split("/")[2])
        if clip is None or track is None or old_track is None or not hasattr(track, "duplicate_clip_to_arrangement"):
            return False
        try:
            current = _read_clip(clip)
            if current.get("k") != value.get("k") or current.get("dur") != value.get("dur") \
                    or (value.get("k") == "audio" and _norm_path(current.get("file")) != _norm_path(value.get("file"))):
                return False  # 長さや中身の種類も変わった → 作り直す
            old_start, old_end = clip.start_time, clip.end_time
            start = float(parts[3])
            moved = track.duplicate_clip_to_arrangement(clip, start)
            if moved is None:
                moved = next((c for c in track.arrangement_clips if abs(c.start_time - start) < 1e-4), None)
            if moved is None:
                return False
            # 元のクリップ（重なって切り分けられた切れ端も）を消す
            for c in list(old_track.arrangement_clips):
                if _ptr(c) != _ptr(moved) and c.start_time >= old_start - 1e-6 and c.end_time <= old_end + 1e-6:
                    old_track.delete_clip(c)
            self._note_created(moved)
            if value.get("k") == "midi" and value.get("n") != current.get("n"):
                _write_notes(moved, value.get("n", []))
            _write_clip_props(moved, value.get("p", {}), current.get("p", {}))
            return True
        except Exception:
            self.log("failed to move clip %s -> %s" % (old_key, key))
            return False

    def _apply_one(self, key, value):
        if _is_local_only(key):
            return  # 古いバージョンの相手から届いても反映しない
        if value is None and key in getattr(self, "_moved_away", ()):
            return  # 動かしたクリップの元の位置は、移動先を作るときに消す
        if key in self._orphans:
            # まだ置けていない（トラックが無い等）クリップへの続きの変更。削除なら忘れ、編集なら覚えている値に重ねる
            if value is None:
                self._orphans.pop(key, None)
                return
            if isinstance(value, dict) and isinstance(value.get("delta"), dict) and isinstance(self._orphans[key], dict):
                self._orphans[key] = merge_delta(self._orphans[key], value)
                return
            self._orphans.pop(key, None)
        try:
            binding = self.bindings.get(key)
            if binding is not None:
                binding[1](value)
            elif key.startswith("c/") and isinstance(value, dict) and isinstance(value.get("delta"), dict)                     and key not in getattr(self, "_moves", {}):
                # 無いクリップへの編集（相手が編集している間に、こちらで消した・動かした）。サーバーと同じく反映しない
                # （反映すると、動かしたクリップが元の位置にも戻ってきて 2 つになる）
                return
            elif key.startswith("c/") and value is not None:
                if not self._create_clip(key, value):
                    self._remember_orphan(key, value)
                    return
                self.needs_rebuild = True
            elif value is not None and self._is_new_item(key):
                self._write_item(key.split("/")[0], key.split("/")[1], dict(value, _id=key.split("/")[1]))
            elif value is not None:
                self._remember_orphan(key, value)
                return
            # 新しくクリップを作っただけなら、読み直しはこのまとまりの最後に 1 回だけ行う（_apply_ops）
            created_only = binding is None and key.startswith("c/") and getattr(self, "_applying", None) is not None
            if self.needs_rebuild and not created_only:
                self.rebuild(announce=False)
            self.last[key] = self.read(key) if key in self.bindings else None if value is None else value
        except Exception:
            import traceback
            self.log("failed to apply %s\n%s" % (key, traceback.format_exc()))

    def _is_new_item(self, key):
        parts = key.split("/")
        return len(parts) == 2 and parts[0] in ("t", "r", "s", "d")

    def _remember_orphan(self, key, value):
        if len(self._orphans) < MAX_ORPHANS:
            self._orphans[key] = value

    def _apply_orphans(self):
        if not self._orphans:
            return
        ready = sorted((k for k in self._orphans if k in self.bindings or self._orphan_ready(k)), key=priority)
        if not ready:
            return
        # 相手の変更を反映したものなので、反映でできたクリップを「こちらの変更」として送り返さない
        outer = getattr(self, "_applying", None)
        self._applying = set(ready) if outer is None else outer | set(ready)
        try:
            for key in ready:
                value = self._orphans.pop(key)
                self._apply_one(key, value)
                self.dirty.discard(key)
        finally:
            self._applying = outer

    def _orphan_ready(self, key):
        parts = key.split("/")
        if parts[0] == "c":
            return ("t/" + parts[2]) in self.bindings and (parts[1] == "a" or ("s/" + parts[3]) in self.bindings)
        if len(parts) == 2 and parts[0] == "d":
            at = self._orphans[key].get("at") if isinstance(self._orphans[key], dict) else None
            return at in self._containers
        return False

    # ---------------------------------------------------- snapshot / flush

    def snapshot(self):
        ops = []
        for key in sorted(self.bindings, key=priority):
            value = self.read(key)
            self.last[key] = value
            ops.append({"k": key, "v": value})
        return ops

    def _poll_arrangement(self):
        """
        アレンジメントのクリップの位置・長さを直接見る（約 1 秒ごと）。
        Live のバージョンによっては start_time / end_time の変更が通知されないことがあり、
        そのままだと長さを変えても、そのクリップを他に触るまで相手に届かない。
        """
        self._poll_ticks += 1
        if "tempo_map" in self.bindings:
            self._poll_saved_timing()
        if self._cues_changed:
            self._relisten_cues(self.song)  # 増えたロケーターにもリスナーを付ける
        if self._poll_ticks < 10:
            return
        self._poll_ticks = 0
        # ロケーターをドラッグしても通知が来ない Live のバージョンがあるので、念のため直接見る
        if "locators" in self.bindings and self._pending_locators is None:
            now_locators = self._read_locators(self.song)
            if self._last_locators is None:
                self._last_locators = now_locators
            elif now_locators != self._last_locators:
                self._last_locators = now_locators
                self.dirty.add("locators")
        for entry in self._arrangement:
            clip, key, start, end = entry
            try:
                now_start, now_end = clip.start_time, clip.end_time
            except Exception:
                self.needs_rebuild = True  # 消えた
                continue
            if now_start != start:
                self.needs_rebuild = True  # 動いた → キーが変わる
            elif now_end != end:
                entry[3] = now_end
                self.dirty.add(key)

    def mark_all_sent(self):
        """今の状態を「送った／反映した」ことにする（変更として送らない）。"""
        for key in self.bindings:
            self.last[key] = self.read(key)
        self.dirty = set()
        self.removed = set()

    def collect_changes(self):
        # 届いたテンポ・拍子の変化に追従させる（これで変わった song.tempo などはリスナーが無視する）
        self._apply_runtime_maps()
        self._flush_pending_locators()
        self._poll_arrangement()
        ops = []
        for key in self.removed:
            self.last[key] = None
            ops.append({"k": key, "v": None})
        self.removed = set()
        dirty, self.dirty = self.dirty, set()
        for key in dirty:
            if key not in self.bindings:
                continue
            value = self.read(key)
            if value != self.last.get(key):
                previous = self.last.get(key)
                self.last[key] = value
                ops.append({"k": key, "v": with_delta(key, value, previous)})
        ops.sort(key=lambda o: priority(o["k"]))
        return ops

    def _warn_once(self, code, message):
        if code not in self._warned:
            self._warned.add(code)
            self.warn(message)


# ---------------------------------------------------------------- helpers

# クリップの変更は「値全体」に加えて、変わったところ（delta）も送る:
#   {"f": 変わった項目（"dur" や "p.name" など）, "a": 足したノート, "d": 消したノート}
# 受け取った側は、今の自分のクリップに delta だけを重ねる。こうすると、2 人が同じクリップを同時に
# 編集しても、相手の古い値で自分の編集（ドラッグ中のノートなど）が消されない。
# 値全体（"n" など）は、クリップを新しく作るときと、あとから参加した人のために残しておく。
# アプリのルームも同じ規則で状態を更新する（ClipDelta.cs）。


def _is_local_only(key):
    """各自のもので、同期しないキー（トラック・リターン・マスターの音量・パン・センド・ミュート・ソロ）。"""
    parts = key.split("/")
    if parts[0] == "m":
        return True
    return len(parts) >= 3 and parts[0] in ("t", "r") and parts[2] in ("mute", "solo", "vol", "pan", "send")


def _note_ident(row):
    return (int(row[0]), _round(row[1]), _round(row[2]))


def _row_diff(new_rows, old_rows):
    """new にあって old に無いノートと、old にあって new に無いノート（同じノートが複数あっても数える）。"""
    old = collections.Counter(tuple(_full_row(r)) for r in old_rows)
    added = []
    for r in (tuple(_full_row(r)) for r in new_rows):
        if old[r] > 0:
            old[r] -= 1
        else:
            added.append(list(r))
    return added, [list(r) for r, count in old.items() for _ in range(count)]


# 同じノートか。REAPER は 1 拍 960 の目盛りに丸めるので、その 1 目盛りくらいの差は同じとみなす
# （サーバー・Bitwig・REAPER と同じ規則。違うと、相手が動かした・消したノートが残って複製になる）
NOTE_TOLERANCE = 0.002


def _same_note(a, b):
    return int(a[0]) == int(b[0]) and abs(float(a[1]) - float(b[1])) <= NOTE_TOLERANCE \
        and abs(float(a[2]) - float(b[2])) <= NOTE_TOLERANCE


def with_delta(key, value, previous):
    if not key.startswith("c/") or not isinstance(value, dict) or not isinstance(previous, dict) \
            or value.get("k") != previous.get("k"):
        return value
    fields = sorted(f for f in set(value) | set(previous) if f not in ("n", "p", "delta") and value.get(f) != previous.get(f))
    p, pp = value.get("p") or {}, previous.get("p") or {}
    fields += sorted("p." + f for f in set(p) | set(pp) if p.get(f) != pp.get(f))
    delta = {"f": fields}
    if value.get("k") == "midi":
        delta["a"], delta["d"] = _row_diff(value.get("n") or [], previous.get("n") or [])
    return dict(value, delta=delta)


def apply_row_delta(rows, added, removed):
    by_pitch = {}
    for r in rows:
        row = list(_full_row(r))
        by_pitch.setdefault(int(row[0]), []).append(row)
    for r in removed or []:
        row = _full_row(r)
        bucket = by_pitch.get(int(row[0]), [])
        for n, x in enumerate(bucket):
            if _same_note(x, row):
                del bucket[n]
                break
    for r in added or []:
        row = list(_full_row(r))
        bucket = by_pitch.setdefault(int(row[0]), [])
        bucket[:] = [x for x in bucket if not _same_note(x, row)]
        bucket.append(row)
    return sorted(x for bucket in by_pitch.values() for x in bucket)


def merge_delta(current, incoming):
    """今の値 current に、届いた値 incoming の delta（変わったところ）だけを重ねる。"""
    delta = incoming.get("delta") or {}
    result = dict(current)
    result["p"] = dict(current.get("p") or {})
    for f in delta.get("f") or []:
        if f.startswith("p."):
            name = f[2:]
            if name in (incoming.get("p") or {}):
                result["p"][name] = incoming["p"][name]
            else:
                result["p"].pop(name, None)
        elif f in incoming:
            result[f] = incoming[f]
        else:
            result.pop(f, None)
    if "a" in delta or "d" in delta:
        result["n"] = apply_row_delta(current.get("n") or [], delta.get("a"), delta.get("d"))
    return result

def _read_sample(device):
    sample = getattr(device, "sample", None)
    path = getattr(sample, "file_path", None) if sample is not None else None
    return {"file": path} if path else None


CLIP_PROPS = (
    ("name", "name", str), ("color", "color", int), ("looping", "looping", bool),
    ("ls", "loop_start", float), ("le", "loop_end", float),
    ("sm", "start_marker", float), ("em", "end_marker", float),
)
AUDIO_PROPS = (
    ("gain", "gain", float), ("pc", "pitch_coarse", int), ("pf", "pitch_fine", float),
    ("warp", "warping", bool), ("wm", "warp_mode", int),
)


def _note_row(n):
    """ノート 1 つ: [音程, 開始, 長さ, ベロシティ, ミュート, 確率, ベロシティ範囲, リリースベロシティ]"""
    return [n.pitch, _round(n.start_time), _round(n.duration), _round(n.velocity, 3), 1 if n.mute else 0,
            _round(getattr(n, "probability", 1.0), 3), _round(getattr(n, "velocity_deviation", 0.0), 3),
            _round(getattr(n, "release_velocity", 64.0), 3)]


NOTE_DEFAULTS = [1.0, 0.0, 64.0]


def _full_row(r):
    r = list(r)
    return r + NOTE_DEFAULTS[len(r) - 5:] if len(r) < 8 else r


def _read_clip(clip):
    props = {}
    for short, attr, cast in CLIP_PROPS + (AUDIO_PROPS if clip.is_audio_clip else ()):
        try:
            v = getattr(clip, attr)
            props[short] = _round(v) if cast is float else cast(v)
        except Exception:
            pass
    extra = {}
    if getattr(clip, "is_arrangement_clip", False):
        try:
            extra["dur"] = _round(clip.end_time - clip.start_time)
        except Exception:
            pass
    if clip.is_audio_clip:
        markers = _read_warp_markers(clip)
        if markers is not None:
            props["wmk"] = markers
        return dict({"k": "audio", "len": _round(clip.length), "file": clip.file_path, "p": props}, **extra)
    rows = sorted(_note_row(n) for n in clip.get_notes_extended(0, 128, 0.0, NOTE_TIME_SPAN))
    return dict({"k": "midi", "len": _round(clip.length), "n": rows, "p": props}, **extra)


def _set_audio_length(clip, value):
    """
    オーディオクリップはファイルの長さで作られるので、アレンジメント上の長さ（dur）に合わせる。
    ループを切っている間は「開始マーカー〜終了マーカー」がそのまま長さになるので、それで決めてから、
    ループの設定（この後の _write_clip_props）を戻す。ワープしていない（秒で表す）クリップは合わせられない。
    """
    try:
        if not clip.warping:
            return
        sm = float((value.get("p") or {}).get("sm", clip.start_marker))
        clip.looping = False
        if sm + float(value["dur"]) > clip.start_marker:
            clip.end_marker = sm + float(value["dur"])
            clip.start_marker = sm
        else:
            clip.start_marker = sm
            clip.end_marker = sm + float(value["dur"])
    except Exception:  # noqa: BLE001
        pass


def _write_clip_props(clip, props, current):
    table = dict((s, (a, c)) for s, a, c in CLIP_PROPS + AUDIO_PROPS)
    # ループ範囲は「開始 < 終了」を保ったまま動かす必要があるので、広げる方向から設定する
    order = ["looping", "warp", "wm"]
    if props.get("ls", 0) >= current.get("le", 0):
        order += ["le", "ls", "em", "sm"]
    else:
        order += ["ls", "le", "sm", "em"]
    order += [s for s in props if s not in order]
    for short in order:
        if short not in props or short not in table or props[short] == current.get(short):
            continue
        attr, cast = table[short]
        try:
            setattr(clip, attr, cast(props[short]))
        except Exception:
            pass
    # 1 回目で順序の都合で設定できなかったものをもう一度
    for short in ("ls", "le", "sm", "em"):
        if short in props:
            try:
                if _round(getattr(clip, table[short][0])) != props[short]:
                    setattr(clip, table[short][0], float(props[short]))
            except Exception:
                pass
    if props.get("wmk") is not None and props.get("wmk") != current.get("wmk"):
        _write_warp_markers(clip, props["wmk"])


def _track_has_content(track):
    """デバイス・クリップのどれかがあるトラックか。"""
    try:
        if len(track.devices):
            return True
        if any(slot.has_clip for slot in track.clip_slots):
            return True
        return len(getattr(track, "arrangement_clips", ())) > 0
    except Exception:  # noqa: BLE001
        return True


def _read_warp_markers(clip):
    """ワープマーカー [[拍, 秒], ...]（オーディオクリップでワープがオンのときだけ。読めない Live では None）"""
    try:
        if not clip.warping:
            return None
        return [[_round(m.beat_time), _round(m.sample_time, 6)] for m in clip.warp_markers]
    except Exception:
        return None


def _make_warp_marker(beat, sample):
    make = getattr(Live.Clip, "WarpMarker", None)
    if make is not None:
        for build in (lambda: make(sample_time=sample, beat_time=beat), lambda: make(sample, beat)):
            try:
                return build()
            except Exception:
                pass
    return {"beat_time": beat, "sample_time": sample}


def _write_warp_markers(clip, wanted):
    """
    ワープマーカーを wanted の状態にする。違う位置のマーカーを消してから、足りないものを足す。
    Live の最後のマーカー（画面には出ない）など、消せない・置けないものがあっても続ける。
    """
    try:
        if not clip.warping:
            clip.warping = True
        current = [(_round(m.beat_time), _round(m.sample_time, 6)) for m in clip.warp_markers]
    except Exception:
        return
    want = [(float(b), float(t)) for b, t in wanted]
    for beat, sample in current:
        if (beat, sample) not in want:
            try:
                clip.remove_warp_marker(beat)
            except Exception:
                pass
    try:
        now = set((_round(m.beat_time), _round(m.sample_time, 6)) for m in clip.warp_markers)
    except Exception:
        now = set()
    for beat, sample in want:
        if (beat, sample) not in now:
            try:
                clip.add_warp_marker(_make_warp_marker(beat, sample))
            except Exception:
                pass


def _spec(r):
    return Live.Clip.MidiNoteSpecification(
        pitch=int(r[0]), start_time=float(r[1]), duration=float(r[2]), velocity=float(r[3]), mute=bool(r[4]),
        probability=float(r[5]), velocity_deviation=float(r[6]), release_velocity=float(r[7]))


def _write_notes(clip, rows):
    """
    クリップのノートを rows の状態にする。
    全部消して置き直すと、ノートに描いた MPE（ピッチベンド・スライド・プレッシャー）が消えてしまう。
    Live の API では MPE を読み書きできないので、せめて変わっていないノートはそのまま残し、
    変わったものだけ直す（apply_note_modifications は MPE を保ったまま直せる）。
    """
    want = {}
    for r in rows:
        r = _full_row(r)
        if float(r[2]) <= 0:
            continue  # 長さ 0 のノート（Bitwig の切れ端など）は Live に置けない
        want.setdefault((int(r[0]), _round(r[1]), _round(r[2])), []).append(r)

    if not hasattr(clip, "apply_note_modifications") or not hasattr(clip, "remove_notes_by_id"):
        clip.remove_notes_extended(0, 128, 0.0, NOTE_TIME_SPAN)
        specs = tuple(_spec(r) for bucket in want.values() for r in bucket)
        if specs:
            clip.add_new_notes(specs)
        return

    remove_ids, modify = [], {}
    for n in clip.get_notes_extended(0, 128, 0.0, NOTE_TIME_SPAN):
        bucket = want.get((n.pitch, _round(n.start_time), _round(n.duration)))
        if not bucket:
            remove_ids.append(n.note_id)
            continue
        r = bucket.pop(0)
        if _note_row(n) != r:
            modify[n.note_id] = r
    if remove_ids:
        clip.remove_notes_by_id(tuple(remove_ids))
    if modify:
        notes = clip.get_notes_extended(0, 128, 0.0, NOTE_TIME_SPAN)
        for n in notes:
            r = modify.get(n.note_id)
            if r is not None:
                n.velocity, n.mute = float(r[3]), bool(r[4])
                n.probability, n.velocity_deviation, n.release_velocity = float(r[5]), float(r[6]), float(r[7])
        clip.apply_note_modifications(notes)
    specs = tuple(_spec(r) for bucket in want.values() for r in bucket)
    if specs:
        clip.add_new_notes(specs)
