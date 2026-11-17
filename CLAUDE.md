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
Sources/PlayerCore/        C engine = portable core + per-OS back-ends (public API: include/playercore.h)
  engine.c                 PORTABLE: demux/decode threads, AC3/DTS encode, IEC 61937 via FFmpeg
                           "spdif" muxer, video decode + presenter synced to audio clock, visualiser tap
  pc_output.c/.h           PORTABLE: lock-free ring, render callback with burst/keep-alive logic,
                           pause/flush, clock (seqlock record + pts markers), file sink
  pc_platform.h            threads/locks/monotonic ns clock/sleep (POSIX+Apple, Win32)
  pc_audio_device.h        audio back-end interface (pcad_*), takes PCStreamType (PCM / IEC AC3 / IEC DTS)
  pc_video_backend.h       video back-end interface (pcvb_*): hw decode setup, native frames
  platform/apple/          audio_coreaudio.c (hog, mixing off, physical format), video_apple.c (VT, CVPixelBuffer)
  platform/stub/           no-device audio + software video (-DPC_STUB_BACKENDS): portability proof, bring-up
Sources/SurroundPlayer/    SwiftUI app
  SurroundPlayerApp.swift  App (Settings scene + menus); AppDelegate CREATES THE MAIN WINDOW itself
  PlayerModel.swift        singleton model: playlist, settings (UserDefaults), engine bridge,
                           device hot-plug, full screen, single-key handler
  ContentView.swift        stage (visualiser > video > artwork), controls, playlist + search
  VideoSink.swift          AVSampleBufferDisplayLayer, frames shown "display immediately"
  SettingsView.swift
  Visualizer/              Metal MilkDrop-2-style visualiser (shaders compiled at runtime)
Sources/pctest/            Swift CLI harness for the engine (no GUI)
tools/pcdump.c             plain-C dump driver (built by check_portable.sh; first thing to run on a new OS)
patches/ffmpeg/            local FFmpeg fixes, applied by build_ffmpeg.sh (stamp-hashed -> auto rebuild)
docs/PORTING_WINDOWS.md    the Windows 11 port plan, step by step
scripts/build_app.sh       arm64 + x86_64 + universal .app and zips into build/
scripts/build_ffmpeg.sh    static slim LGPL FFmpeg 9.0.2 per arch -> vendor/ffmpeg/<arch> (gitignored)
scripts/make_icns.sh       icon.png -> Resources/AppIcon.icns (masks the opaque corners)
scripts/check_portable.sh  builds core + stub back-ends; fails on any OS media header/symbol; builds pcdump
scripts/regress.sh         behavioural output tests (codec/layout, burst spacing, per-channel tones,
                           bit-exact passthrough) for every mode; must print ALL PASSED
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
- `scripts/build_ffmpeg.sh` holds an `OPTIONS` stamp. It includes a hash of
  `patches/ffmpeg/*.patch`; change it whenever the configure flags change, so existing vendor
  builds get rebuilt. The encoders enabled are just **ac3 and dca**.
- **SwiftPM doesn't track the static FFmpeg libs as link inputs.** After rebuilding FFmpeg,
  delete the product binaries (or use `build_app.sh`, which does) or you keep the old FFmpeg.
- `swift build` in development uses `vendor/ffmpeg/arm64` if it exists.
- Harmless noise: `ld: warning: no platform load command found` from the nasm objects,
  which `build_app.sh` filters out.
- Apps are ad-hoc signed, not notarized.

## Portability (done 2026-11-17 in preparation for a Windows 11 port)
- **Core vs back-ends:** `engine.c` and `pc_output.c` contain no OS media code. OS specifics
  are behind `pcad_*` (audio device) and `pcvb_*` (video).
- **The rule:** back-ends own only the device. All IEC 61937 / keep-alive / clock logic is
  in the portable render callback `render()` in `pc_output.c`.
- **Present time:** the back-end calls `render(ctx, dst, frames, present_ns)` with
  `present_ns` in `pc_now_ns()`'s timebase (mach host time on macOS, QPC on Windows).
- **Native frames:** `PCNativeFrame` is opaque. On macOS it's a `CVPixelBufferRef`; Swift
  converts it with `Unmanaged<CVPixelBuffer>.fromOpaque`.
- **DLL exports:** `PC_API` marks the exports, for a Windows DLL / P/Invoke.
- The refactor was verified byte-identical against pre-refactor dumps of every mode, on the
  real HDMI device, and with the stub build.
- Windows plan: `docs/PORTING_WINDOWS.md` (WASAPI exclusive with
  KSDATAFORMAT_SUBTYPE_IEC61937_*, D3D11VA, WinUI 3 + P/Invoke).

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
  - **`scripts/check_portable.sh && scripts/regress.sh`**: the main regression check after
    any engine change. It's behavioural, so it also works on Windows.
  - **Byte-level check:** dumps are deterministic for the same build and FFmpeg. Before a
    refactor, dump every mode and `cmp` afterwards.
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
- **DTS quality:** FFmpeg's DTS encoder is experimental.
  - **Patched bug:** stock 9.0.2 `shift_history` read `input[-1]` for the LFE, building LFE
    history from the wrong channel and occasionally crashing (Homebrew's `ffmpeg -c:a dca`
    crashes about 1 in 12 runs). Fixed by `patches/ffmpeg/0001-…`.
  - **Remaining:** LFE still comes out about 2.8 dB under the source in the DTS path (AC3
    is exact). Cause not found yet.
  - **Finding bugs like this:** Guard Malloc
    (`DYLD_INSERT_LIBRARIES=/usr/lib/libgmalloc.dylib MALLOC_PROTECT_BEFORE=1`) on
    `pcdump` found it; symbolise with the crash report + `atos` against the unstripped
    `pcdump`.
- **Untested:** DTS-HD sources (no encoder to make test files), and audible playback through
  the amp.
- **Not implemented:** subtitles, gapless playback, persisting the playlist across launches,
  E-AC3/TrueHD/DTS-HD bitstreaming over HDMI direct to an AVR (the user's chain is optical,
  so AC3/DTS core is the ceiling), and notarization.
- A clock stall after a seek happened once on the very first bitstream run (when the TV
  first switched to DD) and never reproduced.
- PCM multichannel channel order follows FFmpeg's default layouts, not the device's
  preferred channel layout.
