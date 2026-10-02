# DAW Sync

![Maltese dog](https://a-z-animals.com/media/maltese-1.jpg)

[English](README_en.md) / [日本語](README_ja.md)

DAW Sync は Ableton Live のプロジェクトを macOS / Windows 間で共同編集するアプリです。Bitwig Studio と REAPER も同じルームに参加できます。

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

## 主な機能

- ロケーターの名前・位置、テンポ／拍子、トラック、シーン、MIDI／オーディオクリップを同期
- Live、Bitwig、REAPER のユーザーが同じルームで編集
- パスワード付きルームと Cloudflare Quick Tunnel による招待
- `.als` のサンプル、プラグイン、Pack などの依存関係チェック
- 英語 / 日本語の画面表示

ミキサー設定（音量、パン、センド、エフェクト）は参加者ごとに保持します。Live の Remote Script API の制限により、アレンジメントのテンポ／拍子オートメーションは拍 0 の値を反映し、他のマップ点は保持します。

## ダウンロード

[Windows 用単体実行ファイル（DawSync.exe）をダウンロード](https://github.com/7ewd/maltese/releases/download/v1.0/DawSync.exe) · [DAW Sync 1.0 リリース](https://github.com/7ewd/maltese/releases/tag/v1.0)

Windows 版は自己完結型です。.NET のインストールや追加ランタイムは必要ありません。Live、Bitwig、REAPER 用の連携ファイルも含まれており、アプリからインストールできます。

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