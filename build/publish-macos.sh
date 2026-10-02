#!/bin/bash
# Mac 用の Maltese.app を dist/ に作る（Mac 上で実行する。GitHub Actions でも使う）
# 使い方: ./build/publish-macos.sh [osx-arm64|osx-x64]
set -euo pipefail
RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PUBLISH="$ROOT/dist/$RID"
APP="$ROOT/dist/$RID-app/Maltese.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/Maltese.App/Maltese.App.csproj")"

rm -rf "$PUBLISH" "$(dirname "$APP")"
dotnet publish "$ROOT/src/Maltese.App" -c Release -r "$RID" -o "$PUBLISH"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUBLISH/Maltese" "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/Maltese"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Maltese</string>
  <key>CFBundleDisplayName</key><string>Maltese</string>
  <key>CFBundleIdentifier</key><string>com.maltese.app</string>
  <key>CFBundleExecutable</key><string>Maltese</string>
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
#   macOS 14: Maltese.app を右クリック →「開く」
codesign --force --deep --sign - "$APP"

(cd "$(dirname "$APP")" && ditto -c -k --keepParent Maltese.app "$ROOT/dist/Maltese-$RID.zip")
echo "完成: $ROOT/dist/Maltese-$RID.zip"
echo "（macOS 14 以降が必要です。初回は「システム設定」→「プライバシーとセキュリティ」→「このまま開く」で開いてください）"
