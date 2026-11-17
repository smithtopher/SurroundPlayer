# Surround Player

A native macOS music/video player (SwiftUI + an FFmpeg/CoreAudio engine in C) that
sends surround sound to an HDMI TV as a **Dolby Digital (AC3) or DTS bitstream**, so the
TV can forward it over optical (S/PDIF) to a surround amp.

## Build & run

```sh
brew install nasm              # assembler for the Intel FFmpeg build (optional but faster)
./scripts/build_app.sh         # first run also builds FFmpeg from source (~5 min)
open "build/arm64/Surround Player.app"
```

Only the Command Line Tools are needed, not Xcode. `build_app.sh` produces:

| Output | Runs on |
|---|---|
| `build/arm64/Surround Player.app` + `build/SurroundPlayer-<ver>-apple-silicon.zip` | Apple Silicon |
| `build/x86_64/Surround Player.app` + `build/SurroundPlayer-<ver>-intel.zip` | Intel |
| `build/universal/Surround Player.app` + `build/SurroundPlayer-<ver>-universal.zip` | both |

FFmpeg is compiled statically by `scripts/build_ffmpeg.sh` into `vendor/ffmpeg/<arch>`.
It's a slim LGPL build: every demuxer and decoder, plus only the AC3 and DTS encoders
and the spdif muxer. So the apps depend on nothing outside macOS (the build script checks this
with `otool`). `ARCHS=arm64 ./scripts/build_app.sh` builds a single architecture. Plain
`swift build` uses the vendored FFmpeg if it has been built, otherwise Homebrew's.

The apps are ad-hoc signed, not notarized. On another Mac, right-click → Open the first
time, or run `xattr -dr com.apple.quarantine "Surround Player.app"`.

The icon comes from `icon.png`. `scripts/make_icns.sh` clips its corners transparent,
fits it to the macOS icon grid and writes `Resources/AppIcon.icns`. The build reruns it
whenever `icon.png` changes.

Pick the TV in the toolbar's output menu (or Settings ⌘,). Drag files or whole folders
onto the window or into the playlist (folders are scanned recursively), or use ⌘O.
Type in the sidebar's filter field (⌘F) to search the playlist by file or folder name.
All words must match, so `pink wall` finds Pink Floyd/The Wall tracks. Press Return to
play the first match and Esc to clear. Click the **DOLBY DIGITAL / PCM** badge to see
exactly what is being sent.

Keys (in the player window, not while typing in the filter):

| Key | |
|---|---|
| Space | play / pause |
| ← → / ↑ ↓ | back/forward 10 s / 1 min |
| ⌘← ⌘→ | previous / next track |
| F (or double-click, or the toolbar button) | full-screen player; Esc leaves |
| V | visualiser on/off |
| ] / [ | next / previous visualiser preset |
| L | lock the current preset |

Full screen hides the toolbar and playlist. The transport controls float over the
picture, and they and the cursor fade out after 3 s without mouse movement.

### Visualiser

When enabled (off by default: V, the toolbar ✨ button, the Visualiser menu or
Settings), the stage shows a MilkDrop-2-style visualiser instead of the artwork. It also
replaces the picture of files that have a video track. That video keeps decoding in sync
but isn't drawn, so switching back is instant. It's built in Metal (in
`Sources/SurroundPlayer/Visualizer/`):
- **Feedback warp:** each frame re-draws the previous one through a per-pixel
  zoom/rotate/warp/stretch field and fades it (`decay`).
- **Drawn elements:** waveforms (circle, XY scope, lines, spectrum, dot spiral), custom
  shapes and borders are drawn into that feedback.
- **Composite:** a half-res blur adds glow, and a final pass adds kaleidoscope, video
  echo, hue rotation, chromatic aberration and brighten/darken/solarize/invert.

The 13 built-in presets react to relative bass/mid/treble levels and beats. Like MilkDrop,
the player auto-advances on a strong beat (or after 40 s) and blends presets into each other.
It's an original implementation, so it does not load `.milk` preset files.

The audio comes from a tap in the engine, timed to what is leaving the speakers, so it
also works while bitstreaming. Native Dolby Digital tracks are decoded a second time
just for the visualiser. The visuals follow the Audio latency setting, like video.
`SurroundPlayer --vis-snapshot <dir>` renders every preset to PNG with synthetic music
(handy when writing presets).

TV setup: set the TV's digital/optical audio output format to **Pass-through**
(or Auto/Bitstream), not PCM.

## How the audio path works (Kodi's WASAPI-exclusive approach, on CoreAudio)

| Kodi on Windows (WASAPI) | Here (CoreAudio HAL, `pc_output.c`) |
|---|---|
| Exclusive-mode stream | `kAudioDevicePropertyHogMode` (exclusive ownership) |
| No Windows mixer / APO processing | `kAudioDevicePropertySupportsMixing = 0` |
| `WAVEFORMATEXTENSIBLE` IEC61937 AC3 subtype, 2ch 48 kHz | physical format `cac3` (digital AC3) if offered, else 2ch 16-bit 48 kHz integer LPCM |
| ActiveAE "Dolby Digital transcoding" (`ac3` encoder) | FFmpeg `ac3` encoder (5.1, up to 640 kb/s), or `dca` for DTS (5.1, 1509 kb/s) |
| `CAEPackIEC61937` | FFmpeg `spdif` muxer → IEC 61937 bursts (AC3 6144 bytes, DTS 2048 bytes) |
| Keep-alive silence while paused | silence encoded in the stream's codec, sent while paused, seeking or between tracks |
| Sink kept open across tracks | Output reused when the next file needs the same device, mode and codec |

Per file, with **Settings → Transcode surround to** set to Dolby Digital (default) or DTS:
- **Multichannel sources** (E-AC3, DTS-HD, TrueHD, AAC, FLAC, PCM…): decode → resample to 48 kHz →
  downmix to 5.1 when needed → encode → IEC 61937 → exclusive output.
  - Dolby Digital: AC3 at 384/448/640 kb/s, in 6144-byte bursts.
  - DTS: DTS core at 1509 (full rate) or 754 kb/s, in 2048-byte type I bursts. FFmpeg's DTS
    encoder is marked experimental and is less refined than its AC3 encoder. It also
    puts out LFE about 3 dB lower than its input (plain `ffmpeg -c:a dca` does the same).
- **Native tracks pass through bit-for-bit untouched** (if the passthrough option is on):
  - 48 kHz AC3, in either mode.
  - In DTS mode, also 48 kHz DTS, plus the core of DTS-HD MA/HRA. The packer strips the
    HD extension, since optical can only carry the core. DTS Express has no core, so it
    is transcoded.
  - In Dolby Digital mode, DTS tracks are transcoded to AC3, in case the amp can't decode DTS.
- **Stereo sources**: shared-mode PCM by default. Turn on "Also encode stereo sources" if
  your amp drops audio when switching between PCM and a bitstream.
- **Non-digital outputs** (speakers, headphones, Bluetooth): the engine refuses to bitstream
  and plays PCM, because IEC 61937 data played as PCM is loud noise.

Some TVs don't forward DTS over optical, even in pass-through mode. If the amp goes
silent in DTS mode, switch back to Dolby Digital.

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
Sources/SurroundPlayer/    SwiftUI app (Visualizer/: Metal visualiser, presets, audio analysis)
Sources/pctest/            CLI harness
scripts/build_app.sh       arm64 / x86_64 / universal apps + zips
scripts/build_ffmpeg.sh    static FFmpeg per architecture -> vendor/ffmpeg/<arch>
scripts/make_icns.sh       icon.png -> Resources/AppIcon.icns
```

`pctest` is useful for checking a setup without the GUI:

```sh
swift build --product pctest
.build/debug/pctest devices                       # lists outputs; ac3fmt=1 means a digital AC3 format is offered
.build/debug/pctest dump movie.mkv out.spdif [--dts]  # writes the exact IEC 61937 stream to a file
ffprobe -f spdif out.spdif                        # -> ac3, 48000 Hz, 5.1, 640 kb/s
.build/debug/pctest play movie.mkv --device <UID> --debug
```
