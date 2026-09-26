# AbletonMulti

Ableton Live のプロジェクトを Mac / Windows 間で共同編集するためのツール（開発中）。

## 共同編集（試作）

各自の Live に入れた Remote Script が操作を拾い、ルーム（ホストの PC のアプリ内で動く中継サーバー）経由で
相手の Live に同じ操作を再現する。

```
Live ⇄ Remote Script ⇄ アプリ ⇄ ルーム（ホスト） ⇄ アプリ ⇄ Remote Script ⇄ Live
```

- 同期するもの: テンポ・拍子、トラック/リターン/シーンの追加・削除・名前・色、MIDI クリップとノート
  （確率・ベロシティ範囲も）、オーディオクリップ（バウンス・録音のファイルは書き終わるのを待ってから自動で転送。
  ゲイン・ピッチ・ワープのオン/オフ・ワープモード・ワープマーカーも）
- 同期しないもの: ミキサー（音量・パン・センド・マスター・ミュート・ソロ。聴き方は各自で調整する）、音源・エフェクト・VST（各自が自由にかける。`model.py` の `SYNC_DEVICES = True` で同期も可能）、
  ノートの MPE（Live の API で読み書きできない。相手の編集で自分の MPE が消えないようにはしている）
- グループ: Live の API で作れない。相手が作ると案内が出て、同じトラックを手動でグループにすると自動でつながる
  （相手がグループを解除しても、中のトラックごと消えることはない）
- 同時に同じものを変えた場合は、サーバーに後から届いた方に全員が揃う（`multi.py` 冒頭のコメント参照）。
  ただしクリップは「変わったところ」だけを送って重ねる（足した／消したノート、変えた項目）ので、
  2 人が同じクリップを同時に編集しても、相手の古い値で自分の編集が消えない（`model.py` の with_delta、`ClipDelta.cs`）
- 相手の変更でトラックやクリップが作られても、開いていたクリップ・選んでいたトラック・ノートの画面は元に戻す。
  相手の 1 回の変更は Undo でも 1 回で戻る（Live の API では相手の変更を Undo の対象から外すことはできない）
- ルームには合言葉が付く。「インターネットに公開」で Cloudflare のクイックトンネル（cloudflared を初回ダウンロード）を使い、
  招待コード（xxx.trycloudflare.com#合言葉）だけで遠くの人も参加できる。通信は WebSocket（`Transport.cs`）
- ルームの始め方: 「まっさらから」か「今のセットの続きから」を選べる。参加する人はまっさらにしてから、ルームの内容を受け取る
- 途中参加: 同じセットなら自動で揃う。違うセットや空のセットからは「ルームの内容で上書き」で丸ごと受け取れる
- Remote Script は `src/AbletonMulti.Core/RemoteScript/AbletonMulti/`（アプリに同梱され、ボタン 1 つで User Library に入る）

テスト（Live 無しで、モックの Live 4 台 + アプリ 4 つを動かす）: `bash tests/sim/run_sim.sh`（`SYNC_DEVICES=1` を付けるとデバイス同期のテスト）

## Bitwig Studio とも一緒に使える（試作）

Bitwig に入れる拡張（`bitwig/bridge`、Java）が Live の Remote Script と同じやりとりをするので、
Live の人と Bitwig の人が同じルームで同時編集できる（アプリからは Live と同じに見える）。

- 入れ方: アプリの「Bitwig に拡張を入れる」→ Bitwig の「設定」→「コントローラー」→「Add Controller」→ AbletonMulti
- 同期するもの: テンポ・拍子、トラックの追加・削除・並べ替え・名前・色、グループ（Bitwig 側では自動で作る・解除する）、
  アレンジャーの MIDI クリップとノート、アレンジャーのオーディオクリップ（ファイル・ピッチ・ワープのオン/オフ・
  ワープモード・ワープマーカー。ワープモードは Beats⇔Slice、Tones⇔Elastique Solo、Texture⇔Stretch、
  Re-Pitch⇔Repitch、Complex⇔Elastique、Complex Pro⇔Elastique Pro、ワープオフ⇔Raw で対応させる）
- 同期しないもの: 音量・パン・ソロ・センド（各自で調整する）、リターン（エフェクトトラック）、クリップランチャー、
  ノートの確率、オーディオクリップのゲイン
- Bitwig の公開 API ではアレンジャーを触れないので、Bitwig の内部（難読化された名前）を使っている（`Internals.java` に調べた内容）。
  Bitwig のバージョンが変わると動かなくなることがある（そのときは Bitwig に「対応していません」と出る）。確認したのは Bitwig Studio 6.0
- 分かった注意点: トラックを消した後、エンジンに伝わる前にテンポ等のパラメータを変えると Bitwig のオーディオエンジンが落ちる。
  ノートは音程ごとのレーンから消す必要がある（`BitwigModel.java` の PARAMETERS_FIRST、`Internals.java` 参照）
- ビルド: `bash bitwig/build.sh`（Bitwig の入った PC で。`src/AbletonMulti.Core/BitwigExtension/` にできたものがアプリに同梱される）
- テスト（本物の Bitwig + モックの Live）: Bitwig で捨ててよいプロジェクトを開き、拡張を有効にして `python tests/bitwig/live_bitwig.py`
  （Bitwig 側はテスト用のポート 47490 につなぐので、本物の Live が起動していても影響しない。でたらめな同時編集のテストも入っている。
  `FUZZ_SEEDS=1,2,3` で回数を変えられる）

## プロジェクトチェック

`.als` を読み込んで、共有する前に直すべきところをチェックする。

- プロジェクト外にあるサンプル / 見つからないサンプル
- この PC に入っていないプラグイン、Mac 専用の AU、VST2
- プロジェクト外の Max for Live デバイス、外部 MIDI 機器
- 必要な Pack、Live のバージョン
- 依存関係をマニフェスト（JSON、SHA-256 付き）として書き出し

## 構成

| フォルダ | 内容 |
|---|---|
| `src/AbletonMulti.Core` | `.als` 解析・プラグイン検出・チェック（UI なし、Mac/Win 共通） |
| `src/AbletonMulti.App` | デスクトップアプリ（Avalonia） |
| `src/AbletonMulti.Cli` | 動作確認用のコマンドライン版（`session` / `server` コマンドもある） |
| `tests/sim` | Live のモックと同時編集のテスト |
| `bitwig/bridge` | Bitwig 用の拡張（Java） |
| `tests/bitwig` | 本物の Bitwig とモックの Live で同時編集のテスト |

## 開発

```bash
dotnet run --project src/AbletonMulti.App -- path/to/project.als
dotnet run --project src/AbletonMulti.Cli -- path/to/project.als --json out.json --hash
```

## 配布ファイルを作る

- Windows: `./build/publish-windows.ps1` → `dist/AbletonMulti-windows.zip`（exe 1 つ、.NET 不要）
- Mac: Mac 上で `./build/publish-macos.sh osx-arm64`（Intel Mac は `osx-x64`）→ `dist/AbletonMulti-osx-arm64.zip`
- Mac（Windows 上で作る場合）: `dotnet publish src/AbletonMulti.App -c Release -r osx-arm64 -o dist/osx-arm64` の後
  `python build/package_macos.py` → `dist/AbletonMulti-osx-arm64.tar.gz`（Mac で最初に「最初に実行.command」を右クリック →「開く」）
- GitHub に push すると `.github/workflows/build.yml` が両方を自動でビルドする

Mac 版は Apple の公証（notarization）をしていないため、初回だけ右クリック →「開く」で起動する必要がある。
