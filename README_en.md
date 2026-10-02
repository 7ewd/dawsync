# Maltese

[English](README_en.md) / [日本語](README_ja.md)

Maltese is an experimental tool for collaborative Ableton Live projects across macOS and Windows. Bitwig Studio and REAPER users can join the same room.

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

## Features

- Sync tempo, time signature, tracks, scenes, and MIDI / audio clips
- Edit together from Live, Bitwig, and REAPER
- Invite collaborators with password-protected rooms and Cloudflare Quick Tunnel
- Check `.als` dependencies such as samples, plugins, and Packs

Mixer settings (volume, pan, sends, and FX) are not synchronized. Back up important projects before using this experimental release.

## Usage

1. Start the app and create or join a room.
2. Install the Live Remote Script, Bitwig extension, or REAPER script from the app.
3. Share the invite code and start collaborating.

## Development

The .NET 10 SDK is required. Install the relevant DAW for Live / Bitwig / REAPER integration tests.

```bash
dotnet run --project src/Maltese.App -- path/to/project.als
dotnet run --project src/Maltese.Cli -- path/to/project.als --json out.json --hash
bash tests/sim/run_sim.sh
```

Integration tests: `python tests/live/real_live_bitwig.py` and `python tests/reaper/live_reaper.py`

## Packaging

```powershell
./build/publish-windows.ps1
```

```bash
./build/publish-macos.sh osx-arm64  # Intel Mac: osx-x64
```

## Project layout

- `src/Maltese.Core` — `.als` parsing, sync logic, dependency checks
- `src/Maltese.App` — Avalonia desktop app
- `src/Maltese.Cli` — CLI
- `bitwig/bridge` — Bitwig extension (Java)
- `reaper/Maltese` — REAPER script (Lua)
- `tests` — Simulation and integration tests

## License

GPL-3.0. See [LICENSE](LICENSE).
