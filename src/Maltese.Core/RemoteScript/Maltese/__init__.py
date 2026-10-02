# Maltese - Ableton Live の共同編集用 Remote Script
# Live の「環境設定 → Link, Tempo & MIDI → Control Surface」で Maltese を選ぶと動く。
from .multi import Maltese


def create_instance(c_instance):
    return Maltese(c_instance)
