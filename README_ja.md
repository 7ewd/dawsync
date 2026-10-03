# DAW Sync
[English](README_en.md) / [日本語](README_ja.md)

DAW Syncは、異なるOSやDAWを使う人同士が、簡単に合作できるように設計されたソフトウェアです。<br>
主にリアルタイムでMIDIを編集、オーディオサンプルの配置ができます。

## お金ないです

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

## 主な機能

- ロケーターの名前・位置、テンポ／拍子、トラック、シーン、MIDI／オーディオクリップを同期
- Live、Bitwig、REAPER のユーザーが同じルームで編集
- パスワード付きルームと Cloudflare Quick Tunnel による招待
- `.als` のサンプル、プラグイン、Pack などの依存関係チェック
- 英語 / 日本語の画面表示

ミキサー設定（音量、パン、センド、エフェクト）は参加者ごとに保持します。

テンポ・オートメーションは段差（瞬間的な変化）とランプ（なだらかな変化）の両方を同期します。REAPER と Bitwig はテンポマーカー／オートメーションを直接読み書きします。Live の Remote Script API にはアレンジメントのオートメーションを読み書きする機能がないため、届いたテンポ／拍子の変化には再生中にリアルタイムで追従し、停止中は再生位置の値に合わせます（Global Record と Automation Arm を有効にすれば、Live 側でその変化を記録できます）。Live で描いたテンポ／拍子のオートメーションは、セットを保存したときに共有されます。Live はロケーターを再生位置にしか作れないため、再生中に届いたロケーターは停止したときに配置します。

## ダウンロード

ソースコードとリリースノートは [DAW Sync 1.0.4 リリース](https://github.com/7ewd/dawsync/releases/tag/v1.0.4) で確認できます。

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
