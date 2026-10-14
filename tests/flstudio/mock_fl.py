"""
FL Studio の MIDI スクリプトの API（mixer・general・transport・arrangement・ui・midi）のモック。

install() で sys.modules に入れてから flstudio/device_DawSync.py を読み込む。モジュールは今の FlState（current）に
読み書きを回すので、1 つのプロセスで FL Studio を 2 台（ホストとゲスト）動かすときは、動かす前に current を切り替える。
値の形は FL Studio 2026（MIDI スクリプトの API 45）に合わせてある:
  - mixer.getCurrentTempo() は BPM（古い FL Studio は 1000 倍した int。tempo_x1000 で切り替える）
  - general.processRECEvent(midi.REC_Tempo, BPM の 1000 倍, flags) でテンポを変える
  - general.getRecPPB() は PPQ × 拍子の分子（分母は読めない）
  - arrangement.getMarkerName(i) は無ければ ""、addAutoTimeMarker(tick, name) で足す（消す API は無い）
"""
import sys
import types

REC_ITEM_RANGE = 0x10000
REC_GLOBAL_FIRST = 0x4000 * REC_ITEM_RANGE


class FlState(object):
    def __init__(self, tempo=140.0, num=4, den=4, ppq=96, api=45):
        self.tempo = float(tempo)
        self.num = num
        self.den = den
        self.ppq = ppq
        self.api = api
        self.tempo_x1000 = False
        self.playing = False
        self.loop_mode = 1          # 1 = ソング
        self.ticks = 0
        self.markers = []           # [(tick, name)]
        self.safe = True
        self.automation = None      # 拍 -> BPM（FL Studio 自身のテンポのオートメーションを真似る）
        self.rec_events = []        # 書き込まれたテンポ（BPM）

    # テストから FL Studio を操作する
    def seek(self, beat):
        self.ticks = int(round(beat * self.ppq))
        if self.automation is not None:
            self.tempo = float(self.automation(beat))

    def beat(self):
        return self.ticks / float(self.ppq)


current = FlState()


def _module(name, **functions):
    module = types.ModuleType(name)
    for key, value in functions.items():
        setattr(module, key, value)
    return module


def _get_tempo(asInt=False):
    if current.tempo_x1000:
        return int(round(current.tempo * 1000))
    return int(current.tempo) if asInt else float(current.tempo)


def _set_tempo(tempo, asInt=False):
    current.tempo = float(tempo)
    current.rec_events.append(current.tempo)


def _process_rec_event(event_id, value, flags):
    if event_id == REC_GLOBAL_FIRST + 5:
        current.tempo = value / 1000.0
        current.rec_events.append(current.tempo)
    return 0


def _set_numerator(value):
    current.num = int(value)


def _set_denominator(value):
    current.den = int(value)


def _song_pos(mode=-1):
    if mode == 2:
        return current.ticks
    return 0.0


def _marker_name(index):
    names = [n for _, n in sorted(current.markers, key=lambda m: m[0])]
    return names[index] if 0 <= index < len(names) else ""


def _add_marker(time, name):
    current.markers.append((int(time), name))


def install():
    modules = {
        "mixer": _module("mixer", getCurrentTempo=_get_tempo, setCurrentTempo=_set_tempo),
        "general": _module(
            "general",
            getRecPPQ=lambda: current.ppq,
            getRecPPB=lambda: current.ppq * current.num,
            setNumerator=_set_numerator,
            setDenominator=_set_denominator,
            processRECEvent=_process_rec_event,
            safeToEdit=lambda: current.safe,
            getVersion=lambda: current.api,
        ),
        "transport": _module(
            "transport",
            isPlaying=lambda: current.playing,
            getLoopMode=lambda: current.loop_mode,
            getSongPos=_song_pos,
        ),
        "arrangement": _module("arrangement", getMarkerName=_marker_name, addAutoTimeMarker=_add_marker),
        "ui": _module("ui", getVersion=lambda mode=4: "2026.1.7.5653"),
        "midi": _module(
            "midi",
            REC_Global_First=REC_GLOBAL_FIRST,
            REC_Tempo=REC_GLOBAL_FIRST + 5,
            REC_UpdateValue=1 << 0,
            REC_UpdateControl=1 << 4,
            REC_Control=973,
            REC_NoSaveUndo=1 << 15,
        ),
    }
    sys.modules.update(modules)
    return modules
