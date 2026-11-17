# Surround Player — working notes

Native macOS music/video player whose point is **surround sound over HDMI as a compressed
bitstream** (Dolby Digital AC3 or DTS, IEC 61937). It's modelled on Kodi's WASAPI-exclusive
passthrough, done here with CoreAudio. README.md has the user-facing docs; this file is
the context for continuing development.

## The user's setup (why this exists)
- Mac → **HDMI → Samsung TV → optical (S/PDIF) → AC3-capable amp**. Optical can't carry
  multichannel PCM, so surround has to arrive as AC3/DTS.
- Over HDMI, the Samsung advertises a true digital format **`cac3` (kAudioFormat60958AC3)**.
  The engine picks it and IOProc buffers are int16, so bursts are copied straight through.
- **The Samsung's CoreAudio UID changes** when the TV re-sends EDID (input/mode change).
  The last one seen was `…B571…`, previously `…B471…`, and its channel count changed 8 → 2.
  The app remembers the device by name as a fallback, and reopens if the device disappears.
- Its TV menu should have digital audio output set to Pass-through. Whether this TV
  forwards **DTS** over optical is unverified.

## Layout
```
Package.swift              SwiftPM; FFmpeg from $FFMPEG_PREFIX, else vendor/ffmpeg/<host arch>, else Homebrew
Sources/PlayerCore/        C engine (public API: include/playercore.h)
  engine.c                 demux/decode threads, AC3/DTS encode, IEC 61937 via FFmpeg "spdif" muxer,
                           video (VideoToolbox) + presenter synced to audio clock, visualiser audio tap
  pc_output.c/.h           CoreAudio HAL output: hog mode, mixing off, physical-format switch,
                           lock-free ring, IOProc, clock (seqlock record + pts markers), burst logic
Sources/SurroundPlayer/    SwiftUI app
  SurroundPlayerApp.swift  App (Settings scene + menus); AppDelegate CREATES THE MAIN WINDOW itself
  PlayerModel.swift        singleton model: playlist, settings (UserDefaults), engine bridge,
                           device hot-plug, full screen, single-key handler
  ContentView.swift        stage (visualiser > video > artwork), controls, playlist + search
  VideoSink.swift          AVSampleBufferDisplayLayer, frames shown "display immediately"
  SettingsView.swift
  Visualizer/              Metal MilkDrop-2-style visualiser (shaders compiled at runtime)
Sources/pctest/            CLI harness for the engine (no GUI)
scripts/build_app.sh       arm64 + x86_64 + universal .app and zips into build/
scripts/build_ffmpeg.sh    static slim LGPL FFmpeg 9.0.2 per arch -> vendor/ffmpeg/<arch> (gitignored)
scripts/make_icns.sh       icon.png -> Resources/AppIcon.icns (masks the opaque corners)
```

## Build
- Toolchain: **Command Line Tools only (no Xcode)**, Swift 6.1 in Swift 5 language mode.
  The project targets macOS 14+ and was developed on an Apple Silicon Mac.
  - There's no `xcodebuild`, and no offline `metal` compiler (shaders go through
    `makeLibrary(source:)`).
  - `swift build --arch a --arch b` needs Xcode, so builds are per-`--triple`, joined with `lipo`.
- `./scripts/build_app.sh` (`ARCHS=arm64` for one arch).
  - On its first run it builds FFmpeg (~5 min for both archs). The x86_64 FFmpeg build
    needs `nasm` (installed via Homebrew).
  - It fails if anything outside /System or /usr/lib gets linked (it checks with `otool`).
- `scripts/build_ffmpeg.sh` holds an `OPTIONS` stamp. Change it whenever the configure flags
  change, so existing vendor builds get rebuilt. The encoders enabled are just **ac3 and dca**.
- `swift build` in development uses `vendor/ffmpeg/arm64` if it exists.
- Harmless noise: `ld: warning: no platform load command found` from the nasm objects,
  which `build_app.sh` filters out.
- Apps are ad-hoc signed, not notarized.

## Engine behaviour (key decisions)
- **Modes** (`PCOutputMode`):
  - PCM: shared mode.
  - TRANSCODE: decode → swr to 48 kHz 5.1 (or stereo) → AC3/DTS encode → spdif muxer → ring.
  - PASSTHROUGH: the source's own packets → spdif muxer.
- **Codec choice** (`PCConfig.bitstream_codec`):
  - AC3 mode: passes AC3 through; everything else is transcoded to AC3.
  - DTS mode: passes AC3 and DTS core through (DTS-HD → core, but DTS Express is
    transcoded); everything else is transcoded to DTS.
- **Stereo** goes out as PCM unless `stereo_bitstream` is set.
- **Bursts:** AC3 is 6144 bytes, DTS (512-sample frames) is 2048. Writes to the ring are
  always whole bursts. The IOProc only switches between real data and **encoded silence
  bursts** on burst boundaries (keep-alive while paused/seeking/between tracks).
- **Bitrates:** DTS uses 1509750 (full) or 754500 (half) bit/s; both fit a 2048-byte burst
  with its preamble.
- **Bitstream safety:** it only happens on HDMI/DisplayPort, or on devices that advertise a
  compressed format. Never on speakers or headphones, where it would play as noise.
- **Output reuse:** between files the output is kept open (`kept_out`) when device, mode
  and burst size match, so the receiver stays locked. A codec change forces a reopen
  (~0.75 s).
- **Clock:** audio is the master. The clock is the IOProc output host-time plus device and
  stream latency, mapped through pts markers. Video is shown against `clock - av_offset`;
  `av_offset` is the user's "Audio latency" setting, compensating for the amp's decode delay.
- **Visualiser tap:** decoded audio → stereo float at 48 kHz with pts markers; it can be read
  for the current clock. In passthrough it decodes a copy only while the visualiser is active.
- **Closing:** restores the original physical format, mixing and hog state. SIGINT/SIGTERM
  handlers in the app also restore. A `kill -9` leaves the device changed.

## App behaviour
- **Main window:** the main window is an `NSWindow` + `NSHostingView` (with
  `sceneBridgingOptions = [.toolbars, .title]`) created in `applicationWillFinishLaunching`.
  Don't go back to a SwiftUI `Window` scene: it **doesn't create its window when the app is
  launched to open files** (Finder "Open With" or command-line args).
- **Single-key shortcuts** live in an NSEvent local monitor (`PlayerModel.handleKey`), not
  in menus, so they don't hijack typing in the search field. That covers Space, the arrows,
  ⌘←/⌘→, F, V, [ ], L and Esc. Menu items only show the key in their title.
- **Visualiser:** off by default (`visualizerEnabled`). When on it **overrides the video
  track too**: video keeps decoding in sync, but `VideoSink.isEnabled = false` drops the frames.
- **Full screen:** hides the toolbar (`window.toolbar.isVisible`) and the playlist; the
  controls overlay and cursor auto-hide after 3 s.
- **Visualiser presets:** these are original (13 in `VisualizerPresets.swift`). It does
  not load `.milk` files. projectM (OpenGL) was considered and rejected; it's still an option
  if the user wants real MilkDrop presets.
- **Settings keys in UserDefaults (domain `local.surroundplayer`):**
  - Device: `deviceUID`, `deviceName`.
  - Bitstream: `ac3Enabled` (means bitstream on/off), `bitstreamCodec` (0 AC3, 1 DTS),
    `ac3Bitrate`, `dtsBitrate`, `ac3Passthrough`, `stereoAsAC3`.
  - Other: `avOffsetMs`, `visualizerEnabled`.

## Testing — IMPORTANT
- **Never play audible test signals.** The TV is often the default output, and a test once
  played a 440 Hz tone through it. Use **silent media** (`anullsrc`) for anything that hits a
  real device. Tones are fine with `pctest dump` (it writes to a file, nothing plays).
- With no screen-recording or accessibility permission there are no screenshots and no
  synthetic key presses. Verify through:
  - **`pctest`**:
    - `pctest devices`: lists outputs; `ac3fmt=1` means the device offers a compressed format.
    - `pctest dump <media> <out.spdif> [--dts] [--dts-bitrate 754] [--pcm] [--no-passthrough] [--stereo-ac3] [--vis]`:
      writes the exact output stream to a file.
    - `pctest play <media> [--device UID] [--pcm] [--dts] [--seek S] [--pause-at T] [--start S] [--then <media2>] [--debug] [--seconds N]`:
      headless playback.
  - **Checking a dump:**
    - `ffprobe -f spdif out.spdif`
    - `ffmpeg -f spdif -i out.spdif x.wav`, then check per-channel frequency with a
      zero-crossing script (test files put a different tone on each channel).
    - Passthrough is verified with `cmp` against the source's elementary stream.
  - **App debug:** `SURROUND_DEBUG=1` logs status to stderr every second (output, video
    decode, visualiser frames/signal). `SURROUND_TEST_FULLSCREEN=1` toggles full screen after
    2 s. `SurroundPlayer --vis-snapshot <dir>` renders every preset to PNG with synthetic
    music, which is how the visuals were checked.
  - **Intel:** run the x86_64 / universal binaries with `arch -x86_64` (Rosetta).
  - **Device state** (hog / mixing / physical format) can be checked with a tiny CoreAudio
    Swift script. The built-in speakers report SupportsMixing=0, which isn't settable, so
    that's normal.

## Known limitations / ideas not done
- **DTS quality:** FFmpeg's DTS encoder is experimental, and its LFE comes out about 3 dB low
  (same with plain ffmpeg).
- **Untested:** DTS-HD sources (no encoder to make test files), and audible playback through
  the amp.
- **Not implemented:** subtitles, gapless playback, persisting the playlist across launches,
  E-AC3/TrueHD/DTS-HD bitstreaming over HDMI direct to an AVR (the user's chain is optical,
  so AC3/DTS core is the ceiling), and notarization.
- A clock stall after a seek happened once on the very first bitstream run (when the TV
  first switched to DD) and never reproduced.
- PCM multichannel channel order follows FFmpeg's default layouts, not the device's
  preferred channel layout.
