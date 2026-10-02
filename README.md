# Maltese

Ableton Live のプロジェクトを Mac / Windows 間で共同編集する試作ツールです。Bitwig Studio と REAPER も同じルームに参加できます。

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

[日本語](#日本語) | [English](#english)

## 日本語

[Englishへ](#english)

### できること

- テンポ、拍子、トラック、シーン、MIDI / オーディオクリップを同期
- Live、Bitwig、REAPER のユーザーが同じルームで編集
- パスワード付きルームと Cloudflare Quick Tunnel による招待
- `.als` のサンプル、プラグイン、Pack などの依存関係チェック

```text
Live / Bitwig / REAPER ⇄ Maltese ⇄ 共有ルーム
```

ミキサー設定（音量、パン、センド、FX）は同期しません。試作版のため、重要なプロジェクトはバックアップして使用してください。

### 使い方

1. アプリを起動し、ルームを作成または参加します。
2. アプリから Live Remote Script、Bitwig 拡張、または REAPER スクリプトをインストールします。
3. 招待コードを共有して共同編集を始めます。

### 開発

.NET 10 SDK が必要です。Live / Bitwig / REAPER のテストには各アプリも必要です。

```bash
dotnet run --project src/Maltese.App -- path/to/project.als
dotnet run --project src/Maltese.Cli -- path/to/project.als --json out.json --hash
bash tests/sim/run_sim.sh
```

実機連携テスト: `python tests/live/real_live_bitwig.py`、`python tests/reaper/live_reaper.py`

### 配布

```powershell
./build/publish-windows.ps1
```

```bash
./build/publish-macos.sh osx-arm64  # Intel Mac: osx-x64
```

### 構成

- `src/Maltese.Core` — `.als` 解析、同期処理、依存関係チェック
- `src/Maltese.App` — Avalonia デスクトップアプリ
- `src/Maltese.Cli` — CLI
- `bitwig/bridge` — Bitwig 拡張（Java）
- `reaper/Maltese` — REAPER スクリプト（Lua）
- `tests` — シミュレーションと連携テスト

## English

[日本語へ](#日本語)

Maltese is an experimental tool for collaborative Ableton Live projects across macOS and Windows. Bitwig Studio and REAPER users can join the same room.

### Features

- Sync tempo, time signature, tracks, scenes, and MIDI / audio clips
- Edit together from Live, Bitwig, and REAPER
- Invite collaborators with password-protected rooms and Cloudflare Quick Tunnel
- Check `.als` dependencies such as samples, plugins, and Packs

Mixer settings (volume, pan, sends, and FX) are not synchronized. Back up important projects before using this experimental release.

### Usage

1. Start the app and create or join a room.
2. Install the Live Remote Script, Bitwig extension, or REAPER script from the app.
3. Share the invite code and start collaborating.

### Development

The .NET 10 SDK is required. Install the relevant DAW for Live / Bitwig / REAPER integration tests.

```bash
dotnet run --project src/Maltese.App -- path/to/project.als
dotnet run --project src/Maltese.Cli -- path/to/project.als --json out.json --hash
bash tests/sim/run_sim.sh
```

Integration tests: `python tests/live/real_live_bitwig.py` and `python tests/reaper/live_reaper.py`

### Packaging

```powershell
./build/publish-windows.ps1
```

```bash
./build/publish-macos.sh osx-arm64  # Intel Mac: osx-x64
```

### Project layout

- `src/Maltese.Core` — `.als` parsing, sync logic, dependency checks
- `src/Maltese.App` — Avalonia desktop app
- `src/Maltese.Cli` — CLI
- `bitwig/bridge` — Bitwig extension (Java)
- `reaper/Maltese` — REAPER script (Lua)
- `tests` — Simulation and integration tests

## License

GPL-3.0. See [LICENSE](LICENSE).
