# Maltese

[English](README_en.md) / [日本語](README_ja.md)

Ableton Live のプロジェクトを Mac / Windows 間で共同編集する試作ツールです。Bitwig Studio と REAPER も同じルームに参加できます。

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

## できること

- テンポ、拍子、トラック、シーン、MIDI / オーディオクリップを同期
- Live、Bitwig、REAPER のユーザーが同じルームで編集
- パスワード付きルームと Cloudflare Quick Tunnel による招待
- `.als` のサンプル、プラグイン、Pack などの依存関係チェック

```text
Live / Bitwig / REAPER ⇄ Maltese ⇄ 共有ルーム
```

ミキサー設定（音量、パン、センド、FX）は同期しません。試作版のため、重要なプロジェクトはバックアップして使用してください。

## 使い方

1. アプリを起動し、ルームを作成または参加します。
2. アプリから Live Remote Script、Bitwig 拡張、または REAPER スクリプトをインストールします。
3. 招待コードを共有して共同編集を始めます。

## 構成

- `src/Maltese.Core` — `.als` 解析、同期処理、依存関係チェック
- `src/Maltese.App` — Avalonia デスクトップアプリ
- `src/Maltese.Cli` — CLI
- `bitwig/bridge` — Bitwig 拡張（Java）
- `reaper` — REAPER スクリプト（Lua）
- `tests` — シミュレーションと連携テスト

## ライセンス

MIT。[LICENSE](LICENSE) を参照してください。
