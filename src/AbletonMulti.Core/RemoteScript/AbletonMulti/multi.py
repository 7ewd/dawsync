# AbletonMulti Remote Script 本体（通信と全体の流れ）。セットの中身の読み書きは model.py。
#
# Live の中で動き、同じ PC で動いている AbletonMulti アプリと 127.0.0.1:47400 で通信する。
#   - Live 上の変更を「キーと値」の組として送る
#   - アプリから届いた変更を Live に反映する
#
# 同時編集の食い違い対策：
#   自分が送った変更がサーバーを一周して戻ってくる（ack）までの間に届いた他人の変更は、
#   サーバー上では自分の変更より前なので無視する（どうせ自分の変更で上書きされる）。
#   この判定には「自分の操作」と「届いた操作」の正確な順番が必要なので、アプリではなくここで行う。
#
# 通信は 1 行 1 JSON。Live のメインスレッドをブロックしないよう、ソケットはノンブロッキングで
# update_display()（約 100ms ごとに Live から呼ばれる）の中だけで読み書きする。
# Live の仕様上、リスナーの中では曲を変更できないので、リスナーは「変わった」印を付けるだけにしている。
#
# Live 12 の Python 3.11 で動くように書くこと。

import errno
import json
import select
import socket
import traceback

import Live
from _Framework.ControlSurface import ControlSurface

from .model import Model

APP_HOST = "127.0.0.1"
APP_PORT = 47400
PROTOCOL = 2
SCRIPT_VERSION = "0.4.3"

RETRY_TICKS = 20          # 接続できなかったとき、次に試すまでの tick 数（約 2 秒）
CONNECT_TIMEOUT_TICKS = 30


class AbletonMulti(ControlSurface):

    def __init__(self, c_instance):
        ControlSurface.__init__(self, c_instance)
        self._port = APP_PORT
        self._sock = None
        self._connected = False
        self._connect_ticks = 0
        self._retry_ticks = 0
        self._rx = b""
        self._tx = b""
        self._pending = {}       # key -> 送ったがまだ ack が来ていない変更それぞれの「変えた項目」（"*" は全部）
        self._model = Model(self.song, self._log, self._warn)
        self._log("AbletonMulti %s loaded" % SCRIPT_VERSION)

    def disconnect(self):
        self._model._unbind()
        self._close()
        ControlSurface.disconnect(self)

    def update_display(self):
        ControlSurface.update_display(self)
        try:
            self._tick()
        except Exception:
            self._log("error: " + traceback.format_exc())

    def _log(self, message):
        self.log_message("AbletonMulti: " + message)

    def _warn(self, message):
        self._log("warn: " + message)
        self._send({"t": "warn", "msg": message})

    # ------------------------------------------------------------------ tick

    def _tick(self):
        # 届いた変更を反映する前に、自分の変更を先に送る（ユーザーの操作が上書きされて消えないように）
        self._flush_local()
        self._pump_network()
        self._pump_send()

    def _flush_local(self):
        model = self._model
        model.check_focus()
        if model.needs_rebuild:
            model.rebuild(announce=True)
        self._send_ops(model.collect_changes())

    def _send_ops(self, ops):
        if not ops or not self._connected:
            return
        for op in ops:
            delta = op["v"].get("delta") if isinstance(op.get("v"), dict) else None
            fields = set(delta.get("f") or []) if isinstance(delta, dict) else {"*"}
            self._pending.setdefault(op["k"], []).append(fields)
        self._send({"t": "ops", "ops": ops})

    # --------------------------------------------------------------- network

    def _handle(self, msg):
        kind = msg.get("t")
        model = self._model
        if kind == "apply":
            self._flush_local()
            model.apply(msg.get("ops", []), bool(msg.get("force")), self._pending)
        elif kind == "ack":
            for key in msg.get("keys", []):
                waiting = self._pending.get(key)
                if waiting:
                    waiting.pop(0)
                if not waiting:
                    self._pending.pop(key, None)
        elif kind == "reset":
            self._pending = {}
        elif kind == "snapshot_req":
            self._flush_local()
            self._send({"t": "snapshot", "id": msg.get("id"), "ops": model.snapshot()})
        elif kind == "blank":
            model.make_blank()
            model.rebuild(announce=False)
            model.mark_all_sent()  # まっさらにした変更そのもの（テンポ 120 など）は送らない（ルームに入る前の準備なので）
            self._send({"t": "blanked", "id": msg.get("id")})
        elif kind == "adopt":
            model.adopt(msg.get("map", {}))
            model.rebuild(announce=False)
            self._send({"t": "adopted", "id": msg.get("id")})

    def _pump_network(self):
        if self._sock is None:
            if self._retry_ticks > 0:
                self._retry_ticks -= 1
                return
            self._open()
            return

        sock = self._sock
        try:
            readable, writable, errored = select.select([sock], [sock] if not self._connected else [], [sock], 0)
        except (OSError, ValueError):
            self._close(retry=True)
            return

        if not self._connected:
            self._connect_ticks += 1
            if writable or errored:
                if sock.getsockopt(socket.SOL_SOCKET, socket.SO_ERROR) == 0 and not errored:
                    self._on_connected()
                else:
                    self._close(retry=True)
            elif self._connect_ticks > CONNECT_TIMEOUT_TICKS:
                self._close(retry=True)
            return

        if errored:
            self._close(retry=True)
            return
        if readable:
            try:
                data = sock.recv(1 << 20)
            except (BlockingIOError, InterruptedError):
                data = None
            except OSError:
                self._close(retry=True)
                return
            if data == b"":
                self._close(retry=True)
                return
            if data:
                self._rx += data
                while b"\n" in self._rx:
                    line, self._rx = self._rx.split(b"\n", 1)
                    if line.strip():
                        try:
                            self._handle(json.loads(line.decode("utf-8")))
                        except ValueError:
                            self._log("bad message")

    def _open(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.setblocking(False)
        err = sock.connect_ex((APP_HOST, self._port))
        if err not in (0, errno.EINPROGRESS, errno.EWOULDBLOCK, errno.EALREADY, 10035):
            sock.close()
            self._retry_ticks = RETRY_TICKS
            return
        self._sock = sock
        self._connected = False
        self._connect_ticks = 0
        self._rx = b""
        self._tx = b""

    def _on_connected(self):
        self._connected = True
        self._pending = {}
        app = Live.Application.get_application()
        version = "%d.%d.%d" % (app.get_major_version(), app.get_minor_version(), app.get_bugfix_version())
        self._send({"t": "hello", "proto": PROTOCOL, "script": SCRIPT_VERSION, "live": version})
        self._send({"t": "api", "info": self._api_report()})
        self.show_message("AbletonMulti: アプリに接続しました")

    def _api_report(self):
        """実際の Live でどの機能が使えるかを調べてアプリに知らせる（不具合の調査用）。"""
        song = self.song()
        info = {}

        def has(name, obj, attr):
            try:
                info[name] = hasattr(obj, attr)
            except Exception:
                info[name] = False

        has("Song.move_track", song, "move_track")
        has("Song.move_device", song, "move_device")
        has("Song.create_scene", song, "create_scene")
        has("Song.get_data", song, "get_data")
        has("Song.begin_undo_step", song, "begin_undo_step")
        has("Song.end_undo_step", song, "end_undo_step")
        has("Song.View.detail_clip", song.view, "detail_clip")
        try:
            app_view = Live.Application.get_application().view
            has("Application.View.focus_view", app_view, "focus_view")
            has("Application.View.show_view", app_view, "show_view")
            has("Application.View.is_view_visible", app_view, "is_view_visible")
        except Exception:
            pass
        if len(song.tracks):
            track = song.tracks[0]
            for attr in ("insert_device", "delete_device", "create_audio_clip", "create_midi_clip",
                         "delete_clip", "arrangement_clips", "is_foldable"):
                has("Track." + attr, track, attr)
            if len(track.clip_slots):
                has("ClipSlot.create_audio_clip", track.clip_slots[0], "create_audio_clip")
        for track in list(song.tracks)[:64]:
            try:
                audio = [c for c in track.arrangement_clips if c.is_audio_clip]
            except Exception:
                continue  # グループのトラックは arrangement_clips を読むとエラーになる
            if audio:
                for attr in ("warp_markers", "add_warp_marker", "remove_warp_marker", "move_warp_marker"):
                    has("Clip." + attr, audio[0], attr)
                has("Live.Clip.WarpMarker", Live.Clip, "WarpMarker")
                break
        # アレンジメントのクリップの位置・長さの変更が通知されるか（されなければ model が定期的に見に行く）
        for track in list(song.tracks)[:64]:
            try:
                clips = list(track.arrangement_clips)
            except Exception:
                continue
            if clips:
                for attr in ("add_start_time_listener", "add_end_time_listener", "add_notes_listener",
                             "get_selected_notes_extended", "select_notes_by_id"):
                    has("Clip." + attr, clips[0], attr)
                break
        plugins = []
        for track in list(song.tracks)[:64]:
            for device in track.devices:
                if device.class_name in ("PluginDevice", "AuPluginDevice") and len(plugins) < 5:
                    plugins.append({
                        "class_name": device.class_name,
                        "name": device.name,
                        "class_display_name": getattr(device, "class_display_name", None),
                        "parameters": len(device.parameters),
                        "presets": len(getattr(device, "presets", []) or []),
                    })
                if device.class_name == "OriginalSimpler":
                    has("Simpler.replace_sample", device, "replace_sample")
        info["plugins"] = plugins
        try:
            browser = app_browser = Live.Application.get_application().browser
            info["browser.plugins"] = [c.name for c in browser.plugins.children][:20]
            info["browser.audio_effects"] = [c.name for c in app_browser.audio_effects.children][:10]
        except Exception:
            info["browser"] = "error: " + traceback.format_exc()[-300:]
        return info

    def _close(self, retry=False):
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
            if self._connected:
                self.show_message("AbletonMulti: アプリとの接続が切れました")
        self._sock = None
        self._connected = False
        self._tx = b""
        self._retry_ticks = RETRY_TICKS if retry else 0

    def _send(self, msg):
        if self._connected:
            self._tx += (json.dumps(msg, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")

    def _pump_send(self):
        if not self._connected or not self._tx:
            return
        try:
            sent = self._sock.send(self._tx)
            self._tx = self._tx[sent:]
        except (BlockingIOError, InterruptedError):
            pass
        except OSError:
            self._close(retry=True)
