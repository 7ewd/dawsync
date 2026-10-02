#!/bin/bash
# 中継サーバー + アプリ 4 つ + モックの Live 4 台で同時編集のテストを行う。
# 使い方: bash tests/sim/run_sim.sh
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LOG="$ROOT/tests/sim/logs"
rm -rf "$LOG"
mkdir -p "$LOG"

dotnet build "$ROOT/src/Maltese.Cli" -v q -nologo > "$LOG/build.txt" 2>&1 || { cat "$LOG/build.txt"; exit 1; }
CLI="$ROOT/src/Maltese.Cli/bin/Debug/net10.0/Maltese.Cli"

"$CLI" session --host --name A --bridge-port 47410 --port 47411 --key TEST42 --samples "$LOG/samples_a" > "$LOG/app_a.txt" 2>&1 &
PID_A=$!
sleep 1.5
# LATENCY_MS=150 JITTER_MS=80 を付けると、参加する人（B〜E）は Wi-Fi のような遅延のある中継を通してつなぐ
ROOM=47411
PID_PROXY=""
if [ -n "${LATENCY_MS:-}" ]; then
  python "$ROOT/tests/sim/latency_proxy.py" 47412 47411 "$LATENCY_MS" "${JITTER_MS:-0}" &
  PID_PROXY=$!
  ROOM=47412
  sleep 0.5
fi
"$CLI" session --join 127.0.0.1:$ROOM --key test42 --name B --bridge-port 47420 --samples "$LOG/samples_b" > "$LOG/app_b.txt" 2>&1 &
PID_B=$!
# C は WebSocket（インターネット公開時と同じ方式）で、D は招待コード（アドレス#合言葉）で参加する
"$CLI" session --join ws://127.0.0.1:$ROOM --key TEST42 --name C --bridge-port 47430 --samples "$LOG/samples_c" > "$LOG/app_c.txt" 2>&1 &
PID_C=$!
"$CLI" session --join "127.0.0.1:$ROOM#TEST42" --name D --bridge-port 47440 --samples "$LOG/samples_d" --overwrite > "$LOG/app_d.txt" 2>&1 &
PID_D=$!
# E は別のテンプレートのセットから「まっさら」にして参加する
"$CLI" session --join 127.0.0.1:$ROOM --key TEST42 --name E --bridge-port 47460 --samples "$LOG/samples_e" --blank > "$LOG/app_e.txt" 2>&1 &
PID_E=$!
# 合言葉が違うと入れないこと
"$CLI" session --join 127.0.0.1:$ROOM --key WRONG1 --name X --bridge-port 47450 --samples "$LOG/samples_x" > "$LOG/app_x.txt" 2>&1 &
PID_X=$!
sleep 1.5

PYTHONIOENCODING=utf-8 python "$ROOT/tests/sim/two_lives.py"
STATUS=$?

kill $PID_A $PID_B $PID_C $PID_D $PID_E $PID_X $PID_PROXY 2>/dev/null
wait 2>/dev/null
if [ $STATUS -ne 0 ]; then
  echo
  echo "--- アプリ B のログ（最後の 30 行）---"
  tail -n 30 "$LOG/app_b.txt"
fi
if grep -q "合言葉が違います" "$LOG/app_x.txt"; then echo "PASS 合言葉が違うと参加できない"; else echo "FAIL 合言葉が違うと参加できない"; STATUS=1; fi
exit $STATUS
