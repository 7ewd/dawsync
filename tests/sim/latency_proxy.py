"""
Wi-Fi やインターネット越しのような遅延（とゆらぎ）を入れる TCP の中継（テスト用）。

  python latency_proxy.py <待ち受けポート> <転送先ポート> <遅延ms> <ゆらぎms>

データの順番は変えずに、届いたものを「遅延 ± ゆらぎ」だけ遅らせて送る（TCP なので追い越しは起きない）。
"""
import random
import socket
import sys
import threading
import time


def pipe(src, dst, delay, jitter):
    queue = []
    cond = threading.Condition()
    done = [False]

    def reader():
        last_due = 0.0
        try:
            while True:
                data = src.recv(65536)
                if not data:
                    break
                due = max(time.time() + (delay + random.uniform(-jitter, jitter)) / 1000.0, last_due)
                last_due = due
                with cond:
                    queue.append((due, data))
                    cond.notify()
        except OSError:
            pass
        with cond:
            done[0] = True
            cond.notify()

    def writer():
        try:
            while True:
                with cond:
                    while not queue and not done[0]:
                        cond.wait()
                    if not queue and done[0]:
                        break
                    due, data = queue[0]
                wait = due - time.time()
                if wait > 0:
                    time.sleep(wait)
                with cond:
                    queue.pop(0)
                dst.sendall(data)
        except OSError:
            pass
        for s in (src, dst):
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

    threading.Thread(target=reader, daemon=True).start()
    threading.Thread(target=writer, daemon=True).start()


def main():
    listen, target, delay, jitter = int(sys.argv[1]), int(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", listen))
    server.listen()
    while True:
        a, _ = server.accept()
        b = socket.create_connection(("127.0.0.1", target))
        for s in (a, b):
            s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        pipe(a, b, delay, jitter)
        pipe(b, a, delay, jitter)


if __name__ == "__main__":
    main()
