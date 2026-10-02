# DAW Sync

![Maltese dog](https://a-z-animals.com/media/maltese-1.jpg)

[English](README_en.md) / [日本語](README_ja.md)

DAW Sync lets Ableton Live users collaborate across macOS, Windows, and Linux. Bitwig Studio and REAPER users can join the same room.

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a>

## Features

- Sync locator names and positions, tempo / time-signature changes, tracks, scenes, and MIDI / audio clips
- Edit together from Live, Bitwig, and REAPER
- Password-protected rooms and Cloudflare Quick Tunnel invitations
- `.als` dependency checks for samples, plug-ins, and Packs
- English / Japanese interface

Mixer settings (volume, pan, sends, and effects) remain local to each participant. Live's Remote Script API cannot edit arrangement tempo or time-signature automation, so Live applies the beat-zero value while preserving the remaining map points for other hosts.

## Download

[Windows executable](https://github.com/7ewd/maltese/releases/download/v1.0/DawSync.exe) · [Windows zip](https://github.com/7ewd/maltese/releases/download/v1.0/DAW-Sync-windows.zip) · [macOS arm64](https://github.com/7ewd/maltese/releases/download/v1.0/DAW-Sync-osx-arm64.zip) · [macOS x64](https://github.com/7ewd/maltese/releases/download/v1.0/DAW-Sync-osx-x64.zip) · [Linux x64](https://github.com/7ewd/maltese/releases/download/v1.0/DAW-Sync-linux-x64.tar.gz) · [DAW Sync 1.0 release](https://github.com/7ewd/maltese/releases/tag/v1.0)

All three builds are self-contained; no .NET installation or additional runtime files are required. The app contains the Live, Bitwig, and REAPER integration files and installs them from the interface.

## Usage

1. Start DAW Sync and create or join a room.
2. Install the integration for your DAW from the app.
3. Share the invite code and collaborate.

When upgrading from Maltese, install the integration again so the renamed DAW Sync scripts are selected by the DAW.

## Tested with

- Ableton Live Suite 12 (12.4.6)
- REAPER v7.81
- Bitwig Studio 6.1

## License

MIT. See [LICENSE](LICENSE).