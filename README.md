# DAW Sync
[English](README_en.md) / [日本語](README_ja.md)

DAW Sync lets composers work together in real time<br>
even across different OSs and DAWs<br>
<b>so you can edit MIDI notes, drop in samples, and share them on the fly.</b>

## Before you use this

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a><br>
↑I’m broke and jobless, so please toss me some cash for the ideas

## Features

- Sync locator names and positions, tempo / time-signature changes, tracks, scenes, and MIDI / audio clips
- Edit together from Live, Bitwig, and REAPER
- Password-protected rooms and Cloudflare Quick Tunnel invitations
- `.als` dependency checks for samples, plug-ins, and Packs
- English / Japanese language

Mixer settings (volume, pan, sends, and effects) remain local to each participant.

Tempo automation is synced with both steps (instant changes) and ramps (gradual changes). REAPER and Bitwig read and write tempo markers / automation directly. Live's Remote Script API cannot read or write Arrangement automation, so Live follows incoming tempo / time-signature changes in real time during playback and matches the value at the playhead while stopped (with Global Record and Automation Arm enabled, Live can record those changes). Tempo / time-signature automation drawn in Live is shared when you save the set. Live can only create locators at the playhead, so locators received during playback are placed when playback stops.

## Download

Source code and release notes are available in the [DAW Sync 1.0.4 release](https://github.com/7ewd/dawsync/releases/tag/v1.0.4).

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
