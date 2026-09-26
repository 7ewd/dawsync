"""Live の _Framework.ControlSurface の最小限のまね。"""


class ControlSurface:
    def __init__(self, c_instance):
        self._c_instance = c_instance

    def song(self):
        return self._c_instance.song()

    def log_message(self, message):
        self._c_instance.log(message)

    def show_message(self, message):
        self._c_instance.log("[status] " + message)

    def update_display(self):
        pass

    def disconnect(self):
        pass
