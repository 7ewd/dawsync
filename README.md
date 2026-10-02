# Maltese

![Maltese dog](https://a-z-animals.com/media/maltese-1.jpg)

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

## Tested with

- Ableton Live Suite 12 (12.4.6)
- REAPER v7.81
- Bitwig Studio 6.1

## Project layout

- `src/Maltese.Core` — `.als` parsing, sync logic, dependency checks
- `src/Maltese.App` — Avalonia desktop app
- `src/Maltese.Cli` — CLI
- `bitwig/bridge` — Bitwig extension (Java)
- `reaper` — REAPER scripts (Lua)
- `tests` — Simulation and integration tests

## License

MIT. See [LICENSE](LICENSE).
