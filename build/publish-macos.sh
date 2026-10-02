#!/bin/bash
# macOS 用の自己完結 DAW Sync.app を作る（Mac 上で実行する。GitHub Actions でも使う）
# 使い方: ./build/publish-macos.sh [osx-arm64|osx-x64]
set -euo pipefail
RID="${1:-osx-arm64}"
case "$RID" in
  osx-arm64|osx-x64) ;;
  *) echo "Unsupported runtime: $RID" >&2; exit 1 ;;
esac
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$ROOT/dist"
STAGE="$(mktemp -d "$ROOT/dist/.publish-$RID.XXXXXX")"
trap 'rm -rf -- "$STAGE"' EXIT
PUBLISH="$STAGE/publish"
APP="$STAGE/DAW Sync.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/DawSync.App/DawSync.App.csproj")"

dotnet publish "$ROOT/src/DawSync.App/DawSync.App.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH" \
  -p:BaseOutputPath="$STAGE/build/" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false \
  -p:DebugType=None -p:DebugSymbols=false

shopt -s nullglob dotglob
FILES=("$PUBLISH"/*)
if [ "${#FILES[@]}" -ne 1 ] || [ "${FILES[0]}" != "$PUBLISH/DawSync" ] || [ ! -f "$PUBLISH/DawSync" ]; then
  echo "Expected only DawSync in the publish output." >&2
  find "$PUBLISH" -type f >&2
  exit 1
fi

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PUBLISH/DawSync" "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/DawSync"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>DAW Sync</string>
  <key>CFBundleDisplayName</key><string>DAW Sync</string>
  <key>CFBundleIdentifier</key><string>com.dawsync.app</string>
  <key>CFBundleExecutable</key><string>DawSync</string>
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
#   macOS 14: DawSync.app を右クリック →「開く」
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

(cd "$STAGE" && ditto -c -k --keepParent "DAW Sync.app" "$STAGE/DAW-Sync-$RID.zip")
cp "$STAGE/DAW-Sync-$RID.zip" "$ROOT/dist/DAW-Sync-$RID.zip"
mkdir -p "$ROOT/dist/$RID-app"
ditto "$APP" "$ROOT/dist/$RID-app/DAW Sync.app"
echo "完成: $ROOT/dist/DAW-Sync-$RID.zip"
echo "（macOS 14 以降が必要です。初回は「システム設定」→「プライバシーとセキュリティ」→「このまま開く」で開いてください）"
