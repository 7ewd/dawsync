# DAW Sync
[English](README_en.md) / [日本語](README_ja.md)

DAW Sync is composers to collab in real-time, just like in multiplay!<br>
even across different OSs and DAWs<br>
<b>so you can edit MIDI notes, drop in samples, and share them on the fly.</b>

## Before you use this

<a href="https://www.buymeacoffee.com/7ewd" target="_blank" rel="noopener noreferrer"><img src="https://cdn.buymeacoffee.com/buttons/v2/default-yellow.png" alt="Buy Me a Coffee" style="height: 60px !important;width: 217px !important;"></a><br>
↑I’m broke and jobless, so please toss me some cash for the ideas

## Features

- Sync locator names and positions, tempo / time-signature changes, tracks, scenes, and MIDI / audio clips
- Edit together from Live, Bitwig, REAPER, and FL Studio (FL Studio syncs the tempo, time signature, and locators only; see the table below)
- Password-protected rooms and Cloudflare Quick Tunnel invitations
- English / Japanese language

## Compatibility

| What is synced | Ableton Live | Bitwig Studio | REAPER | FL Studio |
|---|:-:|:-:|:-:|:-:|
| Tempo | ✅ | ✅ | ✅ | ✅ |
| Tempo changes (automation, ramps) | △ ¹ | ✅ | ✅ | △ ² |
| Time signature | ✅ | ✅ | ✅ | ✅ ³ |
| Time-signature changes | △ ¹ | ✅ | ✅ | ❌ ⁴ |
| Locators (markers) | ✅ | ✅ | ✅ | △ ⁵ |
| Tracks (add, delete, order, name, color) | ✅ | ✅ | ✅ | ❌ |
| Group tracks / folders | △ ⁶ | ✅ | ✅ | ❌ |
| Arrangement MIDI clips and notes | ✅ | ✅ | ✅ | ❌ |
| Audio clips and sample sharing (with warp / stretch markers) | ✅ | ✅ | ✅ | ❌ |
| Session (clip launcher) clips | ✅ | ❌ | — | ❌ |
| Return tracks, scenes | ✅ | ❌ | ❌ | ❌ |
| Instruments, effects, mixer (volume, pan, mute, solo) | ❌ | ❌ | ❌ | ❌ |

✅ synced both ways / △ partly (see notes) / ❌ not synced / — the DAW has no such feature

1. Live's API can't write Arrangement automation, so Live follows incoming tempo / time-signature changes while playing. Changes drawn in Live reach the others when you save the set.
2. Like Live, FL Studio follows incoming tempo changes while playing. FL Studio's own tempo automation is not sent.
3. Writing the time signature needs FL Studio 2024 or later. FL Studio's API can't read the denominator, so DAW Sync uses the last one it set (4 until then).
4. Only the first time signature is applied.
5. Add only: other people's locators are added as FL Studio markers. FL Studio's API can't move, rename, or delete markers or read their positions, so markers you place in FL Studio are not sent.
6. Live's API can't create group tracks. Create the group by hand and DAW Sync links it.

FL Studio's scripting API can't read or write piano-roll notes or playlist clips, and can't create tracks. FL Studio users can still join any room: everyone else's tracks and clips stay in the room untouched, and if FL Studio hosts, the first Live / Bitwig / REAPER user to join shares their tracks.

## Download

Source code and release notes are available in the [DAW Sync 1.1.0 release](https://github.com/7ewd/dawsync/releases/tag/v1.1.0).

## Usage

1. Start DAW Sync and create or join a room.
2. Install the integration for your DAW from the app.
3. Share the invite code and collaborate.

### FL Studio

FL Studio runs scripts only on a MIDI input. If you don't have a MIDI keyboard (or it already uses another script), create a virtual MIDI port first: [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) on Windows, or the IAC Driver in Audio MIDI Setup on macOS.

1. Click "Install the FL Studio script" in the app, then start (or restart) FL Studio.
2. In FL Studio, open Options → MIDI Settings (F10), select a MIDI input, set Controller type to "DAW Sync", and enable it.
3. Give that input and the output with the same name the same Port number (for example 1). Without a port number, FL Studio doesn't run the script.

## Tested with

- Ableton Live Suite 12 (12.4.6)
- REAPER v7.81
- Bitwig Studio 6.1
- FL Studio 2026 (26.1.7)

## License

MIT. See [LICENSE](LICENSE).
