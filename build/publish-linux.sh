#!/usr/bin/env bash
# Linux x64 用の自己完結単一ファイルと tar.gz を作る。
set -euo pipefail

RID="${1:-linux-x64}"
if [[ "$RID" != "linux-x64" ]]; then
  echo "Unsupported runtime: $RID" >&2
  exit 1
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$ROOT/dist"
STAGE="$(mktemp -d "$ROOT/dist/.publish-$RID.XXXXXX")"
trap 'rm -rf -- "$STAGE"' EXIT
PUBLISH="$STAGE/publish"
mkdir -p "$PUBLISH"

dotnet publish "$ROOT/src/DawSync.App/DawSync.App.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH" \
  -p:BaseOutputPath="$STAGE/build/" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false \
  -p:DebugType=None -p:DebugSymbols=false

shopt -s nullglob dotglob
FILES=("$PUBLISH"/*)
if [[ "${#FILES[@]}" -ne 1 || "${FILES[0]}" != "$PUBLISH/DawSync" || ! -f "$PUBLISH/DawSync" ]]; then
  echo "Expected only DawSync in the publish output." >&2
  find "$PUBLISH" -type f >&2
  exit 1
fi

mkdir -p "$ROOT/dist/$RID"
cp "$PUBLISH/DawSync" "$ROOT/dist/$RID/DawSync"
tar -czf "$ROOT/dist/DAW-Sync-$RID.tar.gz" -C "$PUBLISH" DawSync
echo "EXE: $ROOT/dist/$RID/DawSync"
echo "TAR: $ROOT/dist/DAW-Sync-$RID.tar.gz"
