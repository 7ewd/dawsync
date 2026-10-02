# DAW Sync

![Maltese dog](https://a-z-animals.com/media/maltese-1.jpg)

[English](README_en.md) / [日本語](README_ja.md)

DAW Sync は、異なる OS や DAW を使うコンポーザー同士が、制作環境の違いを越えてスムーズにコラボレーションできるように設計されたソフトウェアです。

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

## 主な機能

- ロケーターの名前・位置、テンポ／拍子、トラック、シーン、MIDI／オーディオクリップを同期
- Live、Bitwig、REAPER のユーザーが同じルームで編集
- パスワード付きルームと Cloudflare Quick Tunnel による招待
- `.als` のサンプル、プラグイン、Pack などの依存関係チェック
- 英語 / 日本語の画面表示

ミキサー設定（音量、パン、センド、エフェクト）は参加者ごとに保持します。Live の Remote Script API の制限により、アレンジメントのテンポ／拍子オートメーションは拍 0 の値を反映し、他のマップ点は保持します。

## ダウンロード

[Windows exe](https://github.com/7ewd/dawsync/releases/download/v1.0/DawSync.exe) · [Windows zip](https://github.com/7ewd/dawsync/releases/download/v1.0/DAW-Sync-windows.zip) · [macOS arm64](https://github.com/7ewd/dawsync/releases/download/v1.0/DAW-Sync-osx-arm64.zip) · [macOS x64](https://github.com/7ewd/dawsync/releases/download/v1.0/DAW-Sync-osx-x64.zip) · [Linux x64](https://github.com/7ewd/dawsync/releases/download/v1.0/DAW-Sync-linux-x64.tar.gz) · [DAW Sync 1.0 リリース](https://github.com/7ewd/dawsync/releases/tag/v1.0)

各 OS 版は自己完結型です。.NET のインストールや追加ランタイムは必要ありません。Live、Bitwig、REAPER 用の連携ファイルも含まれており、アプリからインストールできます。

## 使い方

1. DAW Sync を起動して、ルームを作成または参加します。
2. アプリから使用する DAW の連携ファイルをインストールします。
3. 招待コードを共有して共同編集を始めます。

Maltese から更新する場合は、改名後の DAW Sync 用連携ファイルを選ぶため、DAW 連携をもう一度インストールしてください。

## 動作確認済み

- Ableton Live Suite 12（12.4.6）
- REAPER v7.81
- Bitwig Studio 6.1

## ライセンス

MIT。[LICENSE](LICENSE) を参照してください。