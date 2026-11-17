# Surround Player

A native macOS music/video player (SwiftUI + an FFmpeg/CoreAudio engine in C) that
sends surround sound to an HDMI TV as a **Dolby Digital (AC3) bitstream**, so the TV
can forward it over optical (S/PDIF) to an AC3-capable amp.

## Build & run

```sh
brew install ffmpeg            # FFmpeg 7+ (tested with 9.0)
./scripts/build_app.sh         # -> build/Surround Player.app
open "build/Surround Player.app"
```

Only the Command Line Tools are needed, not Xcode. The app links against Homebrew's
FFmpeg in `/opt/homebrew`, so it runs on Macs that have it installed.

Pick the TV in the toolbar's output menu (or Settings ⌘,). Drag in files or folders,
or use ⌘O. Shortcuts: Space play/pause, ←/→ ±10 s, ↑/↓ ±1 min, ⌘←/⌘→ previous/next.
Double-click the video to go full screen. Click the **DOLBY DIGITAL / PCM** badge to see
exactly what is being sent.

TV setup: set the TV's digital/optical audio output format to **Pass-through**
(or Auto/Bitstream), not PCM.

## How the audio path works (Kodi's WASAPI-exclusive approach, on CoreAudio)

| Kodi on Windows (WASAPI) | Here (CoreAudio HAL, `pc_output.c`) |
|---|---|
| Exclusive-mode stream | `kAudioDevicePropertyHogMode` (exclusive ownership) |
| No Windows mixer / APO processing | `kAudioDevicePropertySupportsMixing = 0` |
| `WAVEFORMATEXTENSIBLE` IEC61937 AC3 subtype, 2ch 48 kHz | physical format `cac3` (digital AC3) if offered, else 2ch 16-bit 48 kHz integer LPCM |
| ActiveAE "Dolby Digital transcoding" (`ac3` encoder) | FFmpeg `ac3` encoder, 5.1, 384/448/640 kb/s |
| `CAEPackIEC61937` | FFmpeg `spdif` muxer → 6144-byte IEC 61937 bursts |
| Keep-alive silence while paused | AC3-encoded silence bursts while paused, seeking or between tracks |
| Sink kept open across tracks | Output reused when the next file needs the same device and mode |

Per file:
- **Multichannel sources** (E-AC3, DTS, TrueHD, AAC, FLAC, PCM…): decode → resample to 48 kHz →
  downmix to 5.1 when needed → AC3 encode → IEC 61937 → exclusive output.
- **Native 48 kHz AC3 tracks**: packets go straight to IEC 61937, bit-for-bit untouched.
- **Stereo sources**: shared-mode PCM by default. Turn on "Also encode stereo sources as
  Dolby Digital" if your amp drops audio when switching between PCM and DD.
- **Non-digital outputs** (speakers, headphones, Bluetooth): the engine refuses to bitstream
  and plays PCM, because IEC 61937 data played as PCM is loud noise.

Video is decoded with VideoToolbox where possible and shown through
`AVSampleBufferDisplayLayer`, timed against the audio device clock. The clock is
built from the IOProc output timestamps plus device latency. If your amp adds decode delay,
**Settings → Audio latency** delays the picture to match.

## Layout

```
Sources/PlayerCore/        C engine
  include/playercore.h     public API
  engine.c                 demux / decode / AC3 encode / IEC 61937 / video / A-V sync
  pc_output.c              CoreAudio HAL: hog mode, format switching, IOProc ring, clock
Sources/SurroundPlayer/    SwiftUI app
Sources/pctest/            CLI harness
```

`pctest` is useful for checking a setup without the GUI:

```sh
swift build --product pctest
.build/debug/pctest devices                       # lists outputs; ac3fmt=1 means a digital AC3 format is offered
.build/debug/pctest dump movie.mkv out.spdif      # writes the exact IEC 61937 stream to a file
ffprobe -f spdif out.spdif                        # -> ac3, 48000 Hz, 5.1, 640 kb/s
.build/debug/pctest play movie.mkv --device <UID> --debug
```
