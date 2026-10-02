#!/usr/bin/env bash
# Bitwig 用の拡張（Maltese.bwextension）を作る。
# Bitwig の内部 API を使うので、Bitwig Studio がインストールされた PC でビルドする（bitwig.jar をクラスパスに使う）。
#   BITWIG_JAR  bitwig.jar の場所（省略時は標準のインストール先）
#   JAVA_HOME   JDK 21 以降
set -euo pipefail
cd "$(dirname "$0")"

if [ -z "${BITWIG_JAR:-}" ]; then
  for c in "/c/Program Files/Bitwig Studio/bin/bitwig.jar" \
           "/Applications/Bitwig Studio.app/Contents/app/bin/bitwig.jar" \
           "/opt/bitwig-studio/bin/bitwig.jar"; do
    [ -f "$c" ] && BITWIG_JAR="$c" && break
  done
fi
[ -f "${BITWIG_JAR:-}" ] || { echo "bitwig.jar が見つかりません（BITWIG_JAR を指定してください）" >&2; exit 1; }

JAVAC=javac; JAR=jar
if [ -n "${JAVA_HOME:-}" ]; then JAVAC="$JAVA_HOME/bin/javac"; JAR="$JAVA_HOME/bin/jar"; fi

rm -rf bridge/build
mkdir -p bridge/build/META-INF/services
"$JAVAC" -J-Duser.language=en --release 21 -encoding UTF-8 -cp "$BITWIG_JAR" -d bridge/build bridge/src/com/maltese/bitwig/*.java
cp bridge/src/META-INF/services/* bridge/build/META-INF/services/
OUT="../src/Maltese.Core/BitwigExtension/Maltese.bwextension"
mkdir -p "$(dirname "$OUT")"
(cd bridge/build && "$JAR" cf "../../$OUT" .)
echo "built $OUT"
