# AbletonMulti - Ableton Live の共同編集用 Remote Script
# Live の「環境設定 → Link, Tempo & MIDI → Control Surface」で AbletonMulti を選ぶと動く。
from .multi import AbletonMulti


def create_instance(c_instance):
    return AbletonMulti(c_instance)
