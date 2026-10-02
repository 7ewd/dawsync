#!/bin/bash
# Mac 用の AbletonMulti.app を dist/ に作る（Mac 上で実行する。GitHub Actions でも使う）
# 使い方: ./build/publish-macos.sh [osx-arm64|osx-x64]
set -euo pipefail
RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH="$ROOT/dist/$RID"
APP="$ROOT/dist/$RID-app/AbletonMulti.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/AbletonMulti.App/AbletonMulti.App.csproj")"

rm -rf "$PUBLISH" "$(dirname "$APP")"
dotnet publish "$ROOT/src/AbletonMulti.App" -c Release -r "$RID" -o "$PUBLISH"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUBLISH/AbletonMulti" "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/AbletonMulti"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>AbletonMulti</string>
  <key>CFBundleDisplayName</key><string>AbletonMulti</string>
  <key>CFBundleIdentifier</key><string>com.abletonmulti.app</string>
  <key>CFBundleExecutable</key><string>AbletonMulti</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>同じネットワークにいる人のルームにつないだり、自分のルームに参加してもらったりするために使います。</string>
</dict>
</plist>
EOF

# Apple Silicon では署名が無いと起動できないので、アドホック署名だけしておく
# （公証はしていないので、受け取った人は初回だけ次の手順で開く）
#   macOS 15 以降: 一度ダブルクリックしてから「システム設定」→「プライバシーとセキュリティ」→「このまま開く」
#   macOS 14: AbletonMulti.app を右クリック →「開く」
codesign --force --deep --sign - "$APP"

(cd "$(dirname "$APP")" && ditto -c -k --keepParent AbletonMulti.app "$ROOT/dist/AbletonMulti-$RID.zip")
echo "完成: $ROOT/dist/AbletonMulti-$RID.zip"
echo "（macOS 14 以降が必要です。初回は「システム設定」→「プライバシーとセキュリティ」→「このまま開く」で開いてください）"
