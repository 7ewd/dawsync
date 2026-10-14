#!/bin/bash
# FL Studio のスクリプトのテスト（モックの FL Studio の API の上で本物のスクリプトを動かす）。
#   1. fl_unit.py: スクリプトだけ（アプリは使わない）
#   2. fl_live.py: 本物のアプリ（DawSync.Cli）4 つとルーム 2 つで、FL Studio と Live（モック）を同時に動かす
# 使い方: bash tests/flstudio/run_fl.sh
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
LOG="$ROOT/tests/flstudio/logs"
rm -rf "$LOG"
mkdir -p "$LOG"
# FL Studio とのやりとりの場所（本物の FL Studio のフォルダを使わない）
export DAWSYNC_FL_IPC="$(cygpath -w "$LOG/flstudio-ipc" 2>/dev/null || echo "$LOG/flstudio-ipc")"
mkdir -p "$DAWSYNC_FL_IPC/47510" "$DAWSYNC_FL_IPC/47540"

PYTHONIOENCODING=utf-8 python "$ROOT/tests/flstudio/fl_unit.py" || exit 1
echo

dotnet build "$ROOT/src/DawSync.Cli" -v q -nologo > "$LOG/build.txt" 2>&1 || { cat "$LOG/build.txt"; exit 1; }
CLI="$ROOT/src/DawSync.Cli/bin/Debug/net10.0/DawSync.Cli"

"$CLI" session --host --name FL --bridge-port 47510 --port 47511 --key TEST42 --samples "$LOG/samples_a" > "$LOG/app_a.txt" 2>&1 &
PID_A=$!
"$CLI" session --host --name LIVE2 --bridge-port 47530 --port 47531 --key TEST42 --samples "$LOG/samples_c" > "$LOG/app_c.txt" 2>&1 &
PID_C=$!
sleep 1.5
"$CLI" session --join 127.0.0.1:47511 --key TEST42 --name LIVE --bridge-port 47520 --samples "$LOG/samples_b" > "$LOG/app_b.txt" 2>&1 &
PID_B=$!
"$CLI" session --join 127.0.0.1:47531 --key TEST42 --name FL2 --bridge-port 47540 --samples "$LOG/samples_d" > "$LOG/app_d.txt" 2>&1 &
PID_D=$!
sleep 1.5

PYTHONIOENCODING=utf-8 python "$ROOT/tests/flstudio/fl_live.py"
STATUS=$?

kill $PID_A $PID_B $PID_C $PID_D 2>/dev/null
wait 2>/dev/null
if [ $STATUS -ne 0 ]; then
  for n in a b c d; do
    echo
    echo "--- アプリ $n のログ（最後の 20 行）---"
    tail -n 20 "$LOG/app_$n.txt"
  done
fi
exit $STATUS
