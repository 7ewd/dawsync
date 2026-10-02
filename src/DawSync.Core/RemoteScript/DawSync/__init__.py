# DawSync - Ableton Live の共同編集用 Remote Script
# Live の「環境設定 → Link, Tempo & MIDI → Control Surface」で DawSync を選ぶと動く。
from .multi import DawSync


def create_instance(c_instance):
    return DawSync(c_instance)
