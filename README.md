# Surround Player

A music/video player for **macOS** and **Windows 11** that sends surround sound to an
HDMI TV as a **Dolby Digital (AC3) or DTS bitstream**, so the TV can forward it over
optical (S/PDIF) to a surround amp. Optical can't carry multichannel PCM, so surround has
to arrive compressed. The approach is modelled on Kodi's WASAPI-exclusive passthrough.

Download pre-compiled releases [here](https://github.com/smithtopher/SurroundPlayer/tree/releases).

Both apps share one C engine (`Sources/PlayerCore`, FFmpeg-based) with a native back-end
and front-end per OS:

| | macOS | Windows 11 |
|---|---|---|
| App | SwiftUI (`Sources/SurroundPlayer`) | WinUI 3 / C# .NET 8 (`Windows/SurroundPlayer`) |
| Audio output | CoreAudio (hog mode, `cac3` digital format) | WASAPI exclusive, IEC 61937 subtypes |
| Video decode | VideoToolbox → `AVSampleBufferDisplayLayer` | D3D11VA → SwapChainPanel |
| Visualiser | Metal | Direct3D 11 (same 13 presets) |
| Engine library | linked statically | `playercore.dll` via P/Invoke |
| Distributed as | `.app` + zip (arm64, x86_64, universal) | one self-contained `.exe` |

## Using the player

Pick the TV in the toolbar's output menu (or Settings). Drag files or whole folders onto
the window or into the playlist (folders are scanned recursively), or use Open. Type in
the sidebar's filter field to search the playlist by file or folder name. All words must
match, so `pink wall` finds Pink Floyd/The Wall tracks. Press Return to play the first match
and Esc to clear. Click the **DOLBY DIGITAL / PCM** badge to see exactly what is being sent.

TV setup: set the TV's digital/optical audio output format to **Pass-through**
(or Auto/Bitstream), not PCM.

Keys (in the player window, not while typing in the filter):

| Key (macOS) | Key (Windows) | |
|---|---|---|
| Space | Space | play / pause |
| ← → / ↑ ↓ | ← → / ↑ ↓ | back/forward 10 s / 1 min |
| ⌘← ⌘→ | Ctrl+← Ctrl+→ | previous / next track |
| ⌘O / ⌘F / ⌘, | Ctrl+O / Ctrl+F / Ctrl+, | open / search / settings |
| | Ctrl+Shift+O (or the folder button) | add folders |
| File ▸ Open Recent | Ctrl+R (or the history button) | recently opened files, folders and cue sheets |
| | Ctrl+S (or Save… under the playlist) | save the playlist as `.m3u8` |
| | Ctrl+L (or Load… under the playlist) | replace the playlist with an `.m3u`/`.m3u8` |
| F (or double-click) | F (or double-click) | full-screen player; Esc leaves |
| V | V | visualiser on/off |
| ] / [ | ] / [ (or N / P) | next / previous visualiser preset |
| L | L | lock the current preset |
| | A | next audio track (files with several) |
| | S (or the CC button) | subtitles: off → each track → off |

Full screen hides the toolbar and playlist. The transport controls float over the
picture, and they and the cursor fade out after 3 s without mouse movement.

### Features by platform

| | macOS | Windows |
|---|---|---|
| Dolby Digital / DTS transcode and passthrough | ✓ | ✓ |
| Multichannel PCM output (HDMI straight to an AVR, no transcoding) | ✓ (not yet tried on HDMI) | ✓ |
| Audio track menu (and the A key) | ✓ | ✓ |
| Embedded cover art | ✓ | ✓ |
| Folder art (`cover.jpg`, `folder.png`… next to the file) | ✓ | ✓ |
| `.cue` sheets (one big file split into tracks) | ✓ | ✓ |
| Open Recent (last 10 files, folders, cue sheets) | ✓ | ✓ |
| Playlist restored at launch; save/open `.m3u8` playlists | — | ✓ |
| Subtitles (embedded text and bitmap tracks, `.srt`/`.ass`/`.vtt` files) | — (engine ready) | ✓ |
| Visualiser | ✓ | ✓ |

**Artwork:** when the visualiser is off and the file has no video, the stage shows the
file's embedded cover art. A file without embedded art falls back to a .jpg/.jpeg/.png in
the same folder: one named like the file, then `cover`, `folder`, `front`, `album`, then
any name containing those, then the first image.

**Cue sheets:** adding a `.cue` (or a folder containing one) lists its tracks
instead of the audio file it describes. Consecutive tracks of the same file play gaplessly.
Sheets in UTF-8, UTF-16 or the system code page are read. A `FILE` that no longer exists
falls back to the same name with another audio extension (e.g. a `.wav` later converted to
`.flac`).

### Visualiser

When enabled (off by default: V, the toolbar ✨ button or Settings), the stage shows a
MilkDrop-2-style visualiser instead of the artwork. It also replaces the picture of files
that have a video track. That video keeps decoding in sync but isn't drawn, so switching
back is instant.
- **Feedback warp:** each frame re-draws the previous one through a per-pixel
  zoom/rotate/warp/stretch field and fades it (`decay`).
- **Drawn elements:** waveforms (circle, XY scope, lines, spectrum, dot spiral), custom
  shapes and borders are drawn into that feedback.
- **Composite:** a half-res blur adds glow, and a final pass adds kaleidoscope, video
  echo, hue rotation, chromatic aberration and brighten/darken/solarize/invert.

The 13 built-in presets react to relative bass/mid/treble levels and beats. Like MilkDrop,
the player auto-advances on a strong beat (or after 40 s) and blends presets into each other.
It's an original implementation, so it does not load `.milk` preset files. Metal shaders
live in `Sources/SurroundPlayer/Visualizer/`, HLSL ones in `Windows/SurroundPlayer/Visualizer/`.

The audio comes from a tap in the engine, timed to what is leaving the speakers, so it
also works while bitstreaming. Native Dolby Digital tracks are decoded a second time
just for the visualiser. The visuals follow the Audio latency setting, like video.
`SurroundPlayer --vis-snapshot <dir>` renders every preset to PNG with synthetic music
(handy when writing presets), on both platforms.

## Building on macOS

Requirements: macOS 14+, the **Command Line Tools** (not Xcode; Swift 6.1), and
optionally `brew install nasm` (needed for the Intel FFmpeg build).

```sh
./scripts/build_app.sh         # first run also builds FFmpeg from source (~5 min)
open "build/arm64/Surround Player.app"
```

`build_app.sh` produces:

| Output | Runs on |
|---|---|
| `build/arm64/Surround Player.app` + `build/SurroundPlayer-<ver>-apple-silicon.zip` | Apple Silicon |
| `build/x86_64/Surround Player.app` + `build/SurroundPlayer-<ver>-intel.zip` | Intel |
| `build/universal/Surround Player.app` + `build/SurroundPlayer-<ver>-universal.zip` | both |

- `ARCHS=arm64 ./scripts/build_app.sh` builds a single architecture.
- FFmpeg is compiled statically by `scripts/build_ffmpeg.sh` into `vendor/ffmpeg/<arch>`.
  It's a slim LGPL build: every demuxer and decoder, plus only the AC3 and DTS encoders
  and the spdif muxer, with the fixes in `patches/ffmpeg/`. The apps depend on nothing
  outside macOS (the build script checks this with `otool`).
- Plain `swift build` uses the vendored FFmpeg if it has been built, otherwise Homebrew's.
  SwiftPM doesn't notice a rebuilt FFmpeg: delete the product binaries (or use
  `build_app.sh`, which does).
- The icon comes from `icon.png`. `scripts/make_icns.sh` clips its corners transparent,
  fits it to the macOS icon grid and writes `Resources/AppIcon.icns`. The build reruns it
  whenever `icon.png` changes.

The apps are ad-hoc signed, not notarized. On another Mac, right-click → Open the first
time, or run `xattr -dr com.apple.quarantine "Surround Player.app"`.

## Building on Windows 11

### One-time setup

1. **Visual Studio 2022 Build Tools** with the C++ workload, its **Clang** (clang-cl) and
   **CMake** components, and the Windows 11 SDK:

   ```powershell
   winget install Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Llvm.Clang --add Microsoft.VisualStudio.Component.VC.CMake.Project --add Microsoft.VisualStudio.Component.Windows11SDK.22621"
   ```

2. **.NET 8 SDK** (for the app):

   ```powershell
   winget install Microsoft.DotNet.SDK.8
   ```

3. **MSYS2** in `C:\msys64` (set `MSYS2_ROOT` if it lives elsewhere), with the tools the
   FFmpeg build and the tests need. In an MSYS2 shell:

   ```sh
   pacman -S --needed make nasm patch diffutils pkgconf mingw-w64-ucrt-x86_64-python mingw-w64-ucrt-x86_64-ffmpeg
   ```

   The UCRT64 `python` and `ffmpeg` are only used by the tests to make and check media;
   nothing links against them.

None of these need to be on PATH: the scripts find Visual Studio with `vswhere` and import
its x64 environment themselves (`scripts/win_env.ps1`).

### Build

Run from the repository root in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_ffmpeg_win.ps1
```

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_win.ps1 -App
```

- `build_ffmpeg_win.ps1` builds the same slim static FFmpeg with MSVC (static CRT,
  D3D11VA/DXVA2) into `vendor/ffmpeg/windows-x64`. Only needed once, or after the
  configure options or `patches/ffmpeg/` change (it checks a stamp and skips otherwise).
- `build_win.ps1` builds the engine with clang-cl + Ninja into `.build\win-release\`:
  `playercore.dll`, `pctest.exe` (real WASAPI/D3D11 back-ends) and `pcdump.exe`
  (stub back-ends, no device). Add `-App` to also build the app:
  `Windows\SurroundPlayer\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\SurroundPlayer.exe`.
  `-Config Debug` builds `.build\win-debug\` instead.

### Distributable build

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_win.ps1 -Package
```

This writes **`build\SurroundPlayer-<ver>-windows-x64.exe`**: a single self-contained
file (about 70 MB, with .NET, the Windows App SDK and the engine inside; nothing to
install). It unpacks itself to `%TEMP%\.net` on first start (about 10 s; later starts are
quick). It's unsigned, so SmartScreen asks once: More info → Run anyway. Bump `<Version>`
in `Windows/SurroundPlayer/SurroundPlayer.csproj` for each shared build (it's shown in
Settings). Builds are kept on the `releases` branch.

## Testing

> **Never play audible test signals through a real device.** The TV is often the
> default output. Anything that reaches a device should use silent media, e.g.
> `ffmpeg -f lavfi -i anullsrc=r=48000:cl=5.1 -t 60 -c:a ac3 silent.ac3`.
> Tones are fine in dumps, which write to a file and play nothing.

### Engine regression test (both platforms)

`scripts/regress.sh` generates its own test media, runs every output mode through a dump
tool, and checks the IEC 61937 stream: codec, layout and rate, burst spacing, that each
channel carries its source channel's tone, and that passthrough is bit-identical to the
source. It must print **`ALL PASSED`**.

macOS (also proves the core uses no OS media APIs, and builds the dump tool):

```sh
scripts/check_portable.sh && scripts/regress.sh
```

Windows, after `build_win.ps1` (from PowerShell, in MSYS2's UCRT64 environment):

```powershell
$env:MSYSTEM='UCRT64'; $env:CHERE_INVOKING='1'; C:\msys64\usr\bin\bash.exe -l scripts/regress.sh .build/win-release/pcdump.exe
```

### Command-line harness

`pctest` drives the engine without the GUI.

macOS (a Swift product):

```sh
swift build --product pctest
.build/debug/pctest devices                       # outputs; ac3fmt=1 means a digital AC3 format is offered
.build/debug/pctest dump movie.mkv out.spdif [--dts] [--pcm] [--stereo-ac3]   # exact output stream to a file
.build/debug/pctest play silent.mkv --device <UID> --debug --seconds 20       # silent media only!
ffprobe -f spdif out.spdif                        # -> ac3, 48000 Hz, 5.1, 640 kb/s
```

Windows (`tools/pcdump.c` built on the real back-ends; `pcdump.exe` takes the same
arguments without a device):

```powershell
.build\win-release\pctest.exe devices
.build\win-release\pctest.exe movie.mkv out.spdif --dts
.build\win-release\pctest.exe play silent.mkv --seconds 20
.build\win-release\pctest.exe watch --seconds 60
```

Options: `--pcm`, `--dts`, `--stereo` (encode stereo sources too), `--mc-pcm`
(multichannel PCM), `--track N`, `--device <id>`, `--debug`. `watch` lists audio device
changes (for hot-plug tests), and a dump prints a `cover art:` line when the file has
embedded art.

### App debugging

- `SURROUND_DEBUG=1` (macOS, to stderr) or `SURROUND_DEBUG=<file>` (Windows) logs the
  output, position, video decode and visualiser state every second. On Windows the log
  also has `[trace …]` start-up breadcrumbs.
- `SurroundPlayer --vis-snapshot <dir>` renders every visualiser preset to PNG.
- macOS: `SURROUND_TEST_FULLSCREEN=1` toggles full screen after 2 s. Intel builds can be
  run on Apple Silicon with `arch -x86_64`.
- Windows: device and screenshot tests need a session signed in at the PC itself. In a
  disconnected Remote Desktop session there are no audio devices and nothing is drawn;
  over a live Remote Desktop connection audio goes to "Remote Audio" instead of the TV.

## How the audio path works

| Kodi on Windows | macOS (CoreAudio HAL) | Windows (WASAPI) |
|---|---|---|
| Exclusive-mode stream | `kAudioDevicePropertyHogMode` | WASAPI exclusive mode |
| No mixer / APO processing | `kAudioDevicePropertySupportsMixing = 0` | exclusive mode bypasses the mixer |
| IEC 61937 AC3 subtype, 2ch 48 kHz | physical format `cac3` if offered, else 2ch 16-bit LPCM | `KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL` / `_DTS` |
| ActiveAE transcoding | FFmpeg `ac3` encoder (5.1, up to 640 kb/s), or `dca` for DTS (5.1, 1509 kb/s) | same |
| `CAEPackIEC61937` | FFmpeg `spdif` muxer → IEC 61937 bursts (AC3 6144 bytes, DTS 2048 bytes) | same |
| Keep-alive silence while paused | silence encoded in the stream's codec, sent while paused, seeking or between tracks | same |
| Sink kept open across tracks | output reused when the next file needs the same device, mode and codec | same |

Everything above the device (bursts, keep-alive, clock) is in the portable
`pc_output.c`; the back-ends only open the device and pull from its render callback.

Per file, with **Settings → Transcode surround to** (macOS) / **Surround output** (Windows)
set to Dolby Digital (default) or DTS:
- **Multichannel sources** (E-AC3, DTS-HD, TrueHD, AAC, FLAC, PCM…): decode → resample to 48 kHz →
  downmix to 5.1 when needed → encode → IEC 61937 → exclusive output.
  - Dolby Digital: AC3 at 384/448/640 kb/s, in 6144-byte bursts.
  - DTS: DTS core at 1509 (full rate) or 754 kb/s, in 2048-byte type I bursts. FFmpeg's DTS
    encoder is marked experimental and is less refined than its AC3 encoder. The bundled
    FFmpeg carries a fix for an LFE bug in it (`patches/ffmpeg/`). LFE still comes out
    about 2.8 dB lower than its input.
- **Native tracks pass through bit-for-bit untouched** (if the passthrough option is on):
  - 48 kHz AC3, in either mode.
  - In DTS mode, also 48 kHz DTS, plus the core of DTS-HD MA/HRA. The packer strips the
    HD extension, since optical can only carry the core. DTS Express has no core, so it
    is transcoded.
  - In Dolby Digital mode, DTS tracks are transcoded to AC3, in case the amp can't decode DTS.
- **Stereo sources**: shared-mode PCM by default. Turn on "Also encode stereo sources" if
  your amp drops audio when switching between PCM and a bitstream.
- **Multichannel PCM:** nothing is transcoded; multichannel sources go out as PCM in their
  own layout and rate with exclusive access (WASAPI exclusive, or CoreAudio hog mode with the
  device's channel layout). For HDMI connected directly
  to an AV receiver; over optical only stereo would survive.
- **Non-digital outputs** (speakers, headphones, Bluetooth): the engine refuses to bitstream
  and plays PCM, because IEC 61937 data played as PCM is loud noise.

Some TVs don't forward DTS over optical, even in pass-through mode, and some refuse the
DTS format over HDMI altogether (the engine then falls back to PCM). If the amp goes
silent in DTS mode, switch back to Dolby Digital.

Video is decoded in hardware where possible (VideoToolbox on macOS, D3D11VA on Windows;
HEVC falls back to software on GPUs without it) and timed against the audio device clock.
If your amp adds decode delay, **Settings → Audio latency** delays the picture to match.

## Layout

```
Sources/PlayerCore/        C engine: portable core + per-OS back-ends
  include/playercore.h     public API (PC_API exports for the Windows DLL)
  engine.c                 demux / decode / AC3+DTS encode / IEC 61937 / video / A-V sync (portable)
  pc_output.c              ring buffer, burst + keep-alive logic, render callback, clock (portable)
  pc_subtitle.c            subtitle events (text / bitmap) and the timed lookup (portable)
  pc_platform.h            threads, locks, clock, sleep (POSIX/Apple and Win32)
  pc_audio_device.h        audio back-end interface
  pc_video_backend.h       video back-end interface
  platform/apple/          CoreAudio (hog mode, formats) and VideoToolbox back-ends
  platform/windows/        WASAPI (exclusive IEC 61937, hot-plug) and D3D11VA back-ends
  platform/stub/           no-device / software back-ends (portability check, pcdump)
Sources/SurroundPlayer/    macOS SwiftUI app (Visualizer/: Metal visualiser, presets, analysis)
Sources/pctest/            macOS CLI harness
Windows/SurroundPlayer/    Windows WinUI 3 app (C#; Visualizer/: HLSL/D3D11 port; CueSheet.cs)
tools/pcdump.c             plain-C dump tool (pcdump, and pctest.exe on Windows)
patches/ffmpeg/            fixes applied to FFmpeg at build time
docs/PORTING_WINDOWS.md    how the Windows port was planned and done
CMakeLists.txt             Windows engine build (playercore.dll, pctest.exe, pcdump.exe)
Package.swift              macOS build (SwiftPM)
scripts/build_app.sh       macOS: arm64 / x86_64 / universal apps + zips
scripts/build_ffmpeg.sh    macOS: static FFmpeg per architecture -> vendor/ffmpeg/<arch>
scripts/make_icns.sh       macOS: icon.png -> Resources/AppIcon.icns
scripts/check_portable.sh  macOS: proves the core uses no OS media APIs; builds pcdump
scripts/build_ffmpeg_win.ps1  Windows: static MSVC FFmpeg -> vendor/ffmpeg/windows-x64
scripts/build_win.ps1      Windows: engine (+ -App, -Package)
scripts/win_env.ps1        Windows: imports the Visual Studio x64 environment
scripts/regress.sh         both: output tests for every mode (channel mapping, bursts, passthrough)
```
