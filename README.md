# AbletonMulti

Ableton Live のプロジェクトを Mac / Windows 間で共同編集するためのツール（開発中）。

現在の機能（v0.1）: `.als` を読み込んで、共有する前に直すべきところをチェックする。

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
| `src/AbletonMulti.Cli` | 動作確認用のコマンドライン版 |

## 開発

```bash
dotnet run --project src/AbletonMulti.App -- path/to/project.als
dotnet run --project src/AbletonMulti.Cli -- path/to/project.als --json out.json --hash
```

## 配布ファイルを作る

- Windows: `./build/publish-windows.ps1` → `dist/AbletonMulti-windows.zip`（exe 1 つ、.NET 不要）
- Mac: Mac 上で `./build/publish-macos.sh osx-arm64`（Intel Mac は `osx-x64`）→ `dist/AbletonMulti-osx-arm64.zip`
- GitHub に push すると `.github/workflows/build.yml` が両方を自動でビルドする

Mac 版は Apple の公証（notarization）をしていないため、初回だけ右クリック →「開く」で起動する必要がある。
