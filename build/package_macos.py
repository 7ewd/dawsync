"""
Windows で `dotnet publish -r osx-arm64 / osx-x64` した実行ファイルを、Mac 用の Maltese.app にまとめて
tar.gz にする（Windows の zip だと実行権限が消えるため、tar で権限を付けて固める）。

Windows で作ったアプリは署名されていないので、Mac 側で最初に 1 回だけ「最初に実行.command」を実行してもらう
（アドホック署名と隔離属性の解除をする）。そのままダブルクリックすると止められるので:
  - macOS 15 以降: 一度ダブルクリックしてから「システム設定」→「プライバシーとセキュリティ」→「このまま開く」
  - macOS 14: 右クリック →「開く」
同じ説明を「はじめにお読みください.txt」として一緒に入れる。
.NET 10 は macOS 14 以降でしか動かないので、LSMinimumSystemVersion は 14.0 にしている。
Mac 上でビルドする場合は build/publish-macos.sh を使う（そちらは署名まで自動で行う）。

使い方: python build/package_macos.py
"""
import io
import os
import re
import tarfile
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CSPROJ = os.path.join(ROOT, "src", "Maltese.App", "Maltese.App.csproj")
VERSION = re.search(r"<Version>(.*?)</Version>", open(CSPROJ, encoding="utf-8").read()).group(1)

INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Maltese</string>
  <key>CFBundleDisplayName</key><string>Maltese</string>
  <key>CFBundleIdentifier</key><string>com.maltese.app</string>
  <key>CFBundleExecutable</key><string>Maltese</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>{v}</string>
  <key>CFBundleVersion</key><string>{v}</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>同じネットワークにいる人のルームにつないだり、自分のルームに参加してもらったりするために使います。</string>
</dict>
</plist>
""".format(v=VERSION)

README = """Maltese（Mac 版）の始め方

macOS 14 以降が必要です。

1. 「最初に実行.command」をダブルクリックします（初回だけ）。
   「開けません」と出て止められたら:
   - macOS 15 以降: 「完了」で閉じてから、「システム設定」→「プライバシーとセキュリティ」を開き、
     下の方にある「このまま開く」を押します（パスワードを聞かれたら入力）。
   - macOS 14: 「最初に実行.command」を右クリック →「開く」→「開く」を押します。
2. 「準備できました」と出たら、Maltese.app が開きます。次からは Maltese.app をダブルクリックするだけで使えます。
3. ローカルネットワークへの接続を許可するか聞かれたら「許可」を押してください
   （同じ Wi-Fi の人とルームでつながるのに使います）。
"""

SETUP_COMMAND = """#!/bin/bash
# Maltese を Mac で初めて使うときに 1 回だけ実行する。
# （インターネットから来たアプリとして止められないようにし、Mac で動くように署名する）
cd "$(dirname "$0")"
xattr -cr Maltese.app
codesign --force --deep --sign - Maltese.app && echo "準備できました。Maltese.app をダブルクリックして起動してください。"
open Maltese.app
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
    binary = os.path.join(ROOT, "dist", rid, "Maltese")
    if not os.path.exists(binary):
        print("skip %s (先に dotnet publish -r %s してください)" % (rid, rid))
        continue
    out = os.path.join(ROOT, "dist", "Maltese-%s.tar.gz" % rid)
    top = "Maltese"
    with tarfile.open(out, "w:gz", format=tarfile.PAX_FORMAT, encoding="utf-8") as tar:
        for d in (top, top + "/Maltese.app", top + "/Maltese.app/Contents", top + "/Maltese.app/Contents/MacOS"):
            add_dir(tar, d)
        add_file(tar, top + "/Maltese.app/Contents/Info.plist", INFO_PLIST.encode("utf-8"), 0o644)
        add_file(tar, top + "/Maltese.app/Contents/MacOS/Maltese", open(binary, "rb").read(), 0o755)
        add_file(tar, top + "/最初に実行.command", SETUP_COMMAND.encode("utf-8"), 0o755)
        add_file(tar, top + "/はじめにお読みください.txt", README.encode("utf-8"), 0o644)
    print("完成: %s (%.1f MB)" % (out, os.path.getsize(out) / 1024 / 1024))
