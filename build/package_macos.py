"""
Windows で `dotnet publish -r osx-arm64 / osx-x64` した実行ファイルを、Mac 用の AbletonMulti.app にまとめて
tar.gz にする（Windows の zip だと実行権限が消えるため、tar で権限を付けて固める）。

Windows で作ったアプリは署名されていないので、Mac 側で最初に 1 回だけ
「最初に実行.command」を右クリック →「開く」で実行してもらう（アドホック署名と隔離属性の解除をする）。
Mac 上でビルドする場合は build/publish-macos.sh を使う（そちらは署名まで自動で行う）。

使い方: python build/package_macos.py
"""
import io
import os
import re
import tarfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CSPROJ = os.path.join(ROOT, "src", "AbletonMulti.App", "AbletonMulti.App.csproj")
VERSION = re.search(r"<Version>(.*?)</Version>", open(CSPROJ, encoding="utf-8").read()).group(1)

INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>AbletonMulti</string>
  <key>CFBundleDisplayName</key><string>AbletonMulti</string>
  <key>CFBundleIdentifier</key><string>com.abletonmulti.app</string>
  <key>CFBundleExecutable</key><string>AbletonMulti</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>{v}</string>
  <key>CFBundleVersion</key><string>{v}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
""".format(v=VERSION)

SETUP_COMMAND = """#!/bin/bash
# AbletonMulti を Mac で初めて使うときに 1 回だけ実行する。
# （インターネットから来たアプリとして止められないようにし、Mac で動くように署名する）
cd "$(dirname "$0")"
xattr -cr AbletonMulti.app
codesign --force --deep --sign - AbletonMulti.app && echo "準備できました。AbletonMulti.app をダブルクリックして起動してください。"
open AbletonMulti.app
"""


def add_file(tar, name, data, mode):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = mode
    info.mtime = int(time.time())
    tar.addfile(info, io.BytesIO(data))


def add_dir(tar, name):
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    info.mode = 0o755
    info.mtime = int(time.time())
    tar.addfile(info)


for rid in ("osx-arm64", "osx-x64"):
    binary = os.path.join(ROOT, "dist", rid, "AbletonMulti")
    if not os.path.exists(binary):
        print("skip %s (先に dotnet publish -r %s してください)" % (rid, rid))
        continue
    out = os.path.join(ROOT, "dist", "AbletonMulti-%s.tar.gz" % rid)
    top = "AbletonMulti"
    with tarfile.open(out, "w:gz", format=tarfile.PAX_FORMAT, encoding="utf-8") as tar:
        for d in (top, top + "/AbletonMulti.app", top + "/AbletonMulti.app/Contents", top + "/AbletonMulti.app/Contents/MacOS"):
            add_dir(tar, d)
        add_file(tar, top + "/AbletonMulti.app/Contents/Info.plist", INFO_PLIST.encode("utf-8"), 0o644)
        add_file(tar, top + "/AbletonMulti.app/Contents/MacOS/AbletonMulti", open(binary, "rb").read(), 0o755)
        add_file(tar, top + "/最初に実行.command", SETUP_COMMAND.encode("utf-8"), 0o755)
    print("完成: %s (%.1f MB)" % (out, os.path.getsize(out) / 1024 / 1024))
