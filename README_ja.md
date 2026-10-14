# DAW Sync
[English](README_en.md) / [日本語](README_ja.md)

DAW Syncは、異なるOSやDAWを使う人同士が、ゲームのマルチプレイみたいな感覚で、簡単に合作できるように設計されたソフトウェアです。<br>
主にリアルタイムでMIDIを編集、オーディオサンプルの配置ができます。

## お金ないです

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

## 主な機能

- ロケーターの名前・位置、テンポ／拍子、トラック、シーン、MIDI／オーディオクリップを同期
- Live、Bitwig、REAPER、FL Studio のユーザーが同じルームで編集（FL Studio はテンポ・拍子・ロケーターのみ。下の表を参照）
- パスワード付きルームと Cloudflare Quick Tunnel による招待
- 英語 / 日本語の画面表示

## 対応表

| 同期するもの | Ableton Live | Bitwig Studio | REAPER | FL Studio |
|---|:-:|:-:|:-:|:-:|
| テンポ | ✅ | ✅ | ✅ | ✅ |
| テンポの変化（オートメーション・ランプ） | △ ¹ | ✅ | ✅ | △ ² |
| 拍子 | ✅ | ✅ | ✅ | ✅ ³ |
| 拍子の変化 | △ ¹ | ✅ | ✅ | ❌ ⁴ |
| ロケーター（マーカー） | ✅ | ✅ | ✅ | △ ⁵ |
| トラック（追加・削除・並び・名前・色） | ✅ | ✅ | ✅ | ❌ |
| グループトラック／フォルダ | △ ⁶ | ✅ | ✅ | ❌ |
| アレンジメントの MIDI クリップ・ノート | ✅ | ✅ | ✅ | ❌ |
| オーディオクリップ・サンプルの共有（ワープ／ストレッチマーカー込み） | ✅ | ✅ | ✅ | ❌ |
| セッション（クリップランチャー）のクリップ | ✅ | ❌ | — | ❌ |
| リターントラック・シーン | ✅ | ❌ | ❌ | ❌ |
| 音源・エフェクト・ミキサー（音量・パン・ミュート・ソロ） | ❌ | ❌ | ❌ | ❌ |

✅ 双方向に同期 ／ △ 一部のみ（注を参照） ／ ❌ 同期しない ／ — その DAW に機能が無い

1. Live の API ではアレンジメントのオートメーションを書き込めないため、届いたテンポ・拍子の変化には再生中に追従します。Live で書いた変化は、セットを保存したときに相手に届きます。
2. Live と同じく、届いたテンポの変化に再生中に追従します。FL Studio 自身のテンポのオートメーションは送られません。
3. 拍子の書き込みには FL Studio 2024 以降が必要です。FL Studio の API では分母を読めないため、最後に合わせた分母（それまでは 4）として扱います。
4. 最初の拍子だけを合わせます。
5. 追加のみ: 相手のロケーターを FL Studio のマーカーとして追加します。FL Studio の API ではマーカーの移動・名前の変更・削除や位置の読み取りができないため、FL Studio で置いたマーカーは相手に送られません。
6. Live の API ではグループトラックを作れません。手でグループを作ると、DAW Sync が自動でつなぎます。

FL Studio のスクリプトの API では、ピアノロールのノートやプレイリストのクリップを読み書きできず、トラックも作れません。それでも FL Studio のユーザーはどのルームにも参加でき、ほかの人のトラックやクリップはルームにそのまま残ります。FL Studio がホストのときは、最初に参加した Live・Bitwig・REAPER のユーザーのトラックがルームに共有されます。

## ダウンロード

ソースコードとリリースノートは [DAW Sync 1.1.0 リリース](https://github.com/7ewd/dawsync/releases/tag/v1.1.0) で確認できます。

## 使い方

1. DAW Sync を起動して、ルームを作成または参加します。
2. アプリから使用する DAW の連携ファイルをインストールします。
3. 招待コードを共有して共同編集を始めます。

### FL Studio

FL Studio のスクリプトは MIDI 入力の上でしか動きません。MIDI キーボードが無い（またはほかのスクリプトで使っている）ときは、先に仮想 MIDI ポートを作ってください（Windows は [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html)、macOS は「Audio MIDI 設定」の IAC ドライバ）。

1. アプリの「FL Studio にスクリプトを入れる」を押し、FL Studio を起動（起動中なら再起動）します。
2. FL Studio の「オプション」→「MIDI 設定」（F10）で MIDI 入力を選び、Controller type を「DAW Sync」にして有効にします。
3. その入力と、同じ名前の出力の Port に同じ番号（例: 1）を設定します。番号が無いと、FL Studio はスクリプトを動かしません。

## 動作確認済み

- Ableton Live Suite 12（12.4.6）
- REAPER v7.81
- Bitwig Studio 6.1
- FL Studio 2026（26.1.7）

## ライセンス

MIT。[LICENSE](LICENSE) を参照してください。
