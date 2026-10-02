"""画面確認用: モックの Live 2 台をつなぎ、ゆっくりいくつか編集する（テストではない）。"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "mock_live"))
sys.path.insert(0, os.path.join(HERE, "..", "..", "src", "Maltese.Core", "RemoteScript"))
import Live  # noqa: E402
from Maltese import multi  # noqa: E402


class CInstance:
    def __init__(self, song):
        self._song = song

    def song(self):
        return self._song

    def log(self, message):
        pass


def make_song():
    song = Live.Song()
    song.tempo = 152.0
    names = ["Serum Bass", "Vital Lead", "Pad", "Drums"]
    song.tracks = [Live.Track(n, midi=(n != "Drums")) for n in names]
    song.tracks[0].clip_slots[0].create_clip(4.0)
    song.return_tracks = [Live.Track("Reverb", midi=False, slots=0)]
    return song


a, b = make_song(), make_song()
sa, sb = multi.Maltese(CInstance(a)), multi.Maltese(CInstance(b))
sa._port = int(os.environ.get("PORT_A", "47400"))
sb._port = int(os.environ.get("PORT_B", "47420"))


def run(seconds):
    end = time.time() + seconds
    while time.time() < end:
        sa.update_display()
        sb.update_display()
        time.sleep(0.03)


run(3)
steps = [
    lambda: setattr(b, "tempo", 150.0),
    lambda: b.tracks[0].clip_slots[0].clip.user_add_note(36, 0.0),
    lambda: b.tracks[0].clip_slots[0].clip.user_add_note(43, 1.5),
    lambda: setattr(a.tracks[1], "mute", True),
    lambda: setattr(b.tracks[2].mixer_device.volume, "value", 0.6),
    lambda: setattr(b.tracks[2].mixer_device.volume, "value", 0.5),
    lambda: setattr(a.tracks[3].mixer_device.panning, "value", -0.3),
    lambda: setattr(b.tracks[1], "name", "Vital Lead 2"),
    lambda: setattr(a.return_tracks[0].mixer_device.volume, "value", 0.7),
]
for step in steps:
    step()
    run(0.4)
run(float(os.environ.get("HOLD", "8")))
