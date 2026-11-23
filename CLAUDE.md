# Surround Player — working notes

Music/video player whose point is **surround sound over HDMI as a compressed bitstream**
(Dolby Digital AC3 or DTS, IEC 61937), modelled on Kodi's WASAPI-exclusive passthrough.
The macOS app (SwiftUI + C engine, CoreAudio) is complete. README.md has the user-facing
docs; this file is the context for continuing development.

## Status and next step (updated 2026-11-17)
- **macOS: done.** Apple Silicon, Intel and universal builds; see the sections below.
- **Next: Windows 11 port.** The engine was restructured for it: portable core +
  per-OS back-ends.
  - **Step 1 done (2026-11-17):** engine core + stub back-ends + `pcdump.exe` build with
    clang-cl, and `regress.sh` prints `ALL PASSED` on Windows 11. The Win32 branch of
    `pc_platform.h` compiled and worked unchanged.
  - **Step 2 (WASAPI) written 2026-11-17:** `platform/windows/audio_wasapi.c`, driven by
    `pctest.exe devices|play` (tools/pcdump.c on the real back-end). Silent-media runs to the
    Samsung: AC3 transcode + AC3 passthrough open exclusive IEC 61937 Dolby Digital (10 ms
    buffer), PCM shared works, clock tracks real time. **The Samsung's HDMI endpoint refuses
    the IEC 61937 DTS subtype** (EDID has no DTS), so DTS mode falls back to decoded PCM on
    this PC. The user confirmed the amp showed Dolby Digital, with no noise.
  - **Hot-plug written, NOT yet tested with a real TV input change:**
    `pc_set_device_change_callback` + `pc_player_output_lost` (WASAPI: IMMNotificationClient,
    invalidated-stream detection). Test: `pctest play <3-min silent file> --seconds 90`
    while the user switches the TV input away and back; expect "output lost" then
    "reopened at …", and the amp back on Dolby Digital. `pctest watch` lists changes only.
  - **Step 3 (D3D11VA) done 2026-11-17:** `platform/windows/video_d3d11.c`. H.264 1080p
    decodes on the GPU into NV12 texture-array slices with SHADER_RESOURCE (UI samples
    them directly). Pool enlarged by 10 frames for the engine's queue. Software-decoded
    frames become BGRA in CPU memory. API (Windows only): `pc_set_d3d11_device` (UI's
    device, before open), `pc_frame_info`, `pc_frame_retain/release`.
    **This PC's GPU is a GTX 970: no hardware HEVC**, so HEVC is decoded in software
    (1080p 10-bit keeps up). The P010 texture path is untested for that reason.
  - **Step 4 (WinUI 3 app), first version 2026-11-17:** `Windows/SurroundPlayer/` (C#, .NET 8,
    Windows App SDK 1.6 unpackaged + self-contained, Vortice for D3D11) on `playercore.dll`.
    - Files: `Native.cs` (P/Invoke, struct layouts mirror playercore.h), `PlayerModel.cs`
      (port of PlayerModel.swift incl. hot-plug reopen), `MainWindow.xaml(.cs)` (stage,
      controls, playlist + search, device menu, badge, keys, full screen), `SettingsDialog.cs`,
      `VideoRenderer.cs` (SwapChainPanel; NV12/P010 slices → RGB shader, BGRA upload),
      `Settings.cs` (same keys as the Mac app, JSON in %LOCALAPPDATA%\SurroundPlayer).
    - Build: `scripts\build_win.ps1 -App`. Debug log: `SURROUND_DEBUG=<file>`.
    - **Share:** `scripts\build_win.ps1 -Package` → one file, `build\SurroundPlayer-<ver>-windows-x64.exe`
      (~70 MB, single-file self-contained: .NET + Windows App SDK + engine; no VC++ runtime
      imports; nothing to install). It unpacks to `%TEMP%\.net` on first start (~10 s; cached
      starts ~1 s). It's unsigned, so SmartScreen asks once (More info → Run anyway).
      **Bump `<Version>` in SurroundPlayer.csproj for each shared build** (shown in Settings).
      - **Releasing:** commit the exe to `Windows/` on the `releases` branch (macOS zips go in
        `macOS/`) **and publish a GitHub release with the gh CLI**: `gh release create vX.Y.Z
        <exe> [<mac zip>] --target <main sha> --notes-file <CHANGELOG section>`, newest `--latest`.
        Tags v1.1.0–v1.2.8 exist; v1.2.1 and v1.2.7 carry both platforms.
      - The single-file publish uses its own obj/bin (`build\windows\*-singlefile`). Sharing
        them with the normal build made WinAppSDK reuse the wrong reg-free WinRT manifest,
        causing "ClassFactory cannot supply requested class" at start.
    - **Unresolved:** three start-up hangs (window not responding, no status), all within
      minutes of a rebuild. The next 22 starts were fine. Suspected antivirus scanning
      fresh binaries, not proven. `SURROUND_DEBUG` now logs `[trace …]` breadcrumbs
      (app/XAML/window/model/renderer/open, UI vs presenter thread) to locate it if it recurs.
      No debugger is installed (cdb/dotnet-dump unavailable).
    - Verified headless with silent media: H.264 D3D11VA at 24 fps presented, MPEG-4
      software/BGRA, AC3 passthrough + transcode, track advance, clean exit.
    - **Visualiser ported** (`Windows/SurroundPlayer/Visualizer/`): HLSL shaders, the 13
      presets, analyser (own radix-2 FFT scaled like vDSP), D3D11 renderer on its own device
      and SwapChainPanel, driven by CompositionTarget.Rendering. `SurroundPlayer.exe
      --vis-snapshot <dir>` writes a PNG per preset (verified: all 13 render like the Mac's).
      Live: 60 fps from the engine's tap; video keeps decoding but isn't drawn.
    - **.cue sheets (2026-11-18; Mac too since 2026-11-19, `CueSheet.swift`, same rules):** `CueSheet.cs` parses them
      (UTF-8/UTF-16, else the ANSI code page; a missing `FILE` falls back to the same name with
      another media extension). `PlayerModel.Expand` turns a sheet into `Track`s with `Start`/`End`
      and hides the audio files it covers. App-level only, no engine change: open at
      `Start + pos`, position/seek/duration relative to the track. `Tick` (200 ms) handles the
      `End` boundary: if the next item is the next span of the same file it just switches
      `Current` (gapless, no reopen), otherwise it calls `Next()` (can overrun by up to ~200 ms).
      Embedded FLAC CUESHEET tags aren't read.
    - **Artwork (Windows app):** embedded art first (`pc_player_cover_art`; `pcdump` prints a
      `cover art:` line), else a .jpg/.jpeg/.png in the file's folder (`PlayerModel.FindFolderArt`:
      same name as the file > cover/folder/front/album/albumart > names containing
      cover/folder/front > first alphabetically). Shown when the visualiser is off and there's
      no video. The Mac app does the same (`FolderArt.swift`, since 2026-11-19).
    - **Not done on Windows:** cursor auto-hide only via ShowCursor, HDR (PQ) tone mapping,
      (playlist persistence: both apps now, see below).
    - Shortcuts: Mac ⌘ → Ctrl (Ctrl+←/→ prev/next, Ctrl+F search, Ctrl+O open, Ctrl+, settings).
      Ctrl+R opens the Open Recent menu (Windows only; no menu bar there).
    - **Keyboard focus (Windows):** buttons are `IsTabStop="False"`, so the search box is the only
      focus holder and single keys are ignored while it has focus. `FocusPlayer()` moves focus to
      the playlist after pickers/dialogs, Enter/Esc and any click outside the search box
      (`Root_PointerPressedAnywhere`, handledEventsToo); Space in an empty search box toggles play.
    - **Drag and drop (Windows):** `Root_Drop` reads CF_HDROP ("FileDrop": DROPFILES header +
      paths) under a drop deferral; `GetStorageItemsAsync` is only a fallback (unpackaged, it can
      return one item or throw 0x80040064 when "Shell IDList Array" is unreadable). Drops are
      logged with SURROUND_DEBUG. A WinForms DoDragDrop helper or UIAutomation-driven Explorer
      drag can test it (synthetic mouse; flaky, retry).
    - **Playlist persistence (Windows 1.2.4):** `PlaylistFile.cs`. Session in
      `%LOCALAPPDATA%\SurroundPlayer\playlist.json` (tracks with their full `CueTrack`, current
      index, track-relative position), written 2 s after a change (`ScheduleSave`) and in
      `Shutdown`; restored in the model constructor (missing files dropped; current selected,
      not opened; `TogglePlay` resumes at `_resumeAt`). `.m3u/.m3u8`: `Expand` replaces a playlist
      by its entries (nested playlists and URLs skipped); Save… / Ctrl+S writes UTF-8 `.m3u8` with
      relative paths, cue tracks as their sheet (`CueTrack.CuePath`). Load… / Ctrl+L
      (`PlayerModel.LoadPlaylist`) replaces the playlist and plays the first entry; Open adds.
      **Mac (1.2.7):** `PlaylistFile.swift`, same rules and JSON shape, session in
      `~/Library/Application Support/SurroundPlayer/playlist.json`, saved 2 s after a change and in
      `shutdown()` (app quit and SIGINT/SIGTERM), restored in `PlayerModel.init`. File menu: Load
      Playlist… ⌘L, Save Playlist… ⌘S; Load…/Save… also under the playlist. `.m3u/.m3u8` open from
      Finder. Tested: M3U write/read (relative paths, cue sheets once, Windows-written lists with
      backslashes/CRLF/BOM) and quit → relaunch restoring exact cue tracks + position.
    - **Open Recent (Windows, 2026-11-19):** a history button (DropDownButton) next to the
      folder button, same rules as the Mac menu (see `recentFiles` below), in
      `PlayerModel.NoteRecent/OpenRecent/RecentTitle`, menu built in `RecentMenu_Opening`.
      Stored in settings.json `recentFiles`. The command line adds to it too.
  - **The Windows PC is connected the same way:** HDMI → Samsung TV → optical → AC3 amp.
    The user confirmed the testing rule below (silent files only) for it.
  - **Windows build (no PATH setup needed):**
    - Toolchain: VS 2022 Build Tools (C++ + Clang: clang-cl 19, CMake, Ninja) and MSYS2
      (`C:\msys64`: make, nasm, patch, diffutils, pkgconf; UCRT64 python + ffmpeg for tests).
    - `scripts\build_ffmpeg_win.ps1`: MSVC static FFmpeg 9.0.2 + patches → `vendor/ffmpeg/windows-x64`
      (`.lib` names, static CRT /MT, w32threads, d3d11va/dxva2). Same `OPTIONS` stamp scheme.
    - `scripts\build_win.ps1`: `CMakeLists.txt` → `.build\win-release\pcdump.exe`.
    - `scripts\win_env.ps1`: imports the VS x64 environment (`Enter-PcDevShell`).
    - Test: `MSYSTEM=UCRT64` bash: `bash scripts/regress.sh .build/win-release/pcdump.exe`.
    - **zlib/bzlib are not in the Windows FFmpeg yet** (macOS has them). PNG cover art and
      zlib-compressed MKV tracks need zlib: add it before the app relies on artwork.
  - **What's macOS-only:** the macOS-only parts below are marked **(macOS)**.
    - Swift, `Package.swift`, `build_app.sh`, `build_ffmpeg.sh`, `check_portable.sh`
      (nm/clang/framework flags), `pctest` and all CoreAudio notes.
    - Portable to Windows: the C engine sources, `tools/pcdump.c`, `scripts/regress.sh`
      (needs bash, ffmpeg, ffprobe and python3, e.g. MSYS2 or Git Bash), and
      `patches/ffmpeg/` (FFmpeg on Windows needs it too).

## The user's setup (why this exists) (macOS side)
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

## Build (macOS)
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
- **Multichannel PCM** (`PCConfig.multichannel_pcm`, added 2026-11-17): nothing is transcoded.
  Multichannel sources go out as decoded PCM in their own layout and rate via
  `PC_STREAM_PCM_EXCLUSIVE`: WASAPI exclusive, first accepted of source rate / 48 kHz ×
  source mask / side↔back twin × s24-in-32 / s32 / s16 / float. Bit-exact for ≤24-bit,
  verified with a 24/96 dump. Native AC3/DTS still pass through; stereo stays shared PCM.
  On refusal it falls back to shared PCM. The Samsung accepts 6 ch 24/96 over HDMI (Windows),
  but optical to the amp is stereo, so it's for HDMI-direct AVR setups.
  - **CoreAudio (2026-11-19):** hog + mixing off (`take_exclusive`, shared with bitstream),
    then the best LPCM physical format with ≥ the source's channels (exact count first) at the
    source rate, else 48 kHz, and a float32 virtual format of the same shape (falls back to
    the physical int format; 24-in-32 must be high-aligned). Speaker routing comes from the
    device's `PreferredChannelLayout`: descriptions, bitmap, or a layout tag expanded with
    AudioToolbox (the expanded layout keeps its tag but carries descriptions). Labels map to
    WAVE bits (Ls/Rs → side, Rls/Rrs → back). The engine renders that mask in WAVE order,
    and the IOProc scatters into device order (`speaker_perm`); unlabelled channels get
    silence. With no layout reported, it assumes the source's order when the counts match.
  - **Tested on Mac** (no HDMI device attached at the time): the exclusive open, rate switch,
    silent render and restore on the built-in speakers (2 ch), refusal of 6 ch there (→ shared
    PCM fallback with the reason in `output`), and synthetic unit tests of the layout parsing
    and IOProc scatter (MPEG 5.1 A/C tags, HDMI 7.1 descriptions, bitmap, unlabelled channels).
    **Not yet tried on a real HDMI device:** `pctest play <silent 5.1 file> --mc-pcm --device <uid>`.
- **Audio tracks:** `PCConfig.audio_track`. Both apps: labelled track menu in the
  controls bar (shown when a file has >1 track) and the A key cycles. pcdump has `--track N`.
- **Subtitles (engine, 2026-11-19):** `pc_subtitle.c` (portable). Tracks = subtitle streams
  with a decoder (`subt[]`, each with its own decoder + `PCSubs` store) + files from
  `pc_player_add_subtitle_file` (loaded whole into their store). Every embedded subtitle stream
  is decoded by the demux thread as packets arrive (not just the selected one: the demuxer
  runs seconds ahead, so selecting later would miss cues); stores keep 120 s behind and are
  cleared on seek. Text: ASS dialogue from FFmpeg -> plain text (override tags stripped,
  \N -> newline). Bitmaps: PAL8 -> premultiplied BGRA; open-ended events end at the next one.
  `pc_player_subtitle_state` looks up at `video_clock` (clock - av_offset) and has one serial
  across track switches. Text files must be UTF-8 (no iconv in our FFmpeg): the Windows app
  converts others to %TEMP%\SurroundPlayer\subtitles. PGS was tested with a generated .sup
  (FFmpeg can't encode PGS; a small script writes PCS/WDS/PDS/ODS/END segments). Mux it with
  `ffmpeg -copyts -i test.sup` or ffmpeg shifts it to start at 0. DVD/DVB subs are untested.
  Controls bar: `FitRightControls` collapses labels so buttons are never clipped. `pctest play <video> --sub N | --sub-file F`.
  Windows app: `PlayerModel.Subtitles.cs` (sidecars `Movie[.lang...].srt/.ass/.ssa/.vtt`,
  `subtitleLanguage` setting: "" never chosen = the file's default-flagged track, "off", "*" any,
  else 2-letter; forced tracks shown when nothing else is; flags from
  `pc_player_subtitle_track_flags`), overlay
  `SubtitleLayer` in `MainWindow` polled every 40 ms, placed over the uniform-fit video rect.
  **Mac app (1.2.7):** `Subtitles.swift` (same rules; non-UTF-8 files are converted into
  `$TMPDIR/SurroundPlayer/subtitles`; languages from `Locale.LanguageCode`), `SubtitleOverlay`
  in `ContentView.swift` over the video, CC menu in the controls bar (`ViewThatFits` drops
  labels before buttons), S key, Playback > Next Subtitle Track / Load Subtitle File….
  Unlike Windows it places subtitles in the **displayed** rect (`VideoSink.displaySize`, the
  pixel aspect ratio applied), so anamorphic video (DVD 720x480 SAR 32:27 → 853x480) is right.
  Tested in the app (`SURROUND_DEBUG` prints the subtitle state): default-flagged SubRip timing,
  PGS bitmaps on an HD and an anamorphic canvas, a Windows-1252 `movie.de.srt` sidecar picked
  by the remembered language. Not checked by eye (no screenshots), DVD/DVB untested.
- **Stereo upmix (engine + Mac app 1.3.0, 2026-11-20):** `pc_upmix.c` (portable) builds a
  libavfilter graph: `bass` (boost) → `surround` (FFT ambience extraction, chl_out **"5.1" = back
  surrounds** in FFmpeg naming; with 5.1(side) out-of-phase ambience goes nowhere) → `adelay`
  (rears) → `lowpass` (rears) → `alimiter` (latency=1) → `aformat`. API `pc_player_set_upmix` /
  `pc_player_upmix_active` (not a PCConfig field, so the C# mirror of PCConfig is unchanged).
  Applies to 1–2 ch sources when the output is a bitstream transcode or exclusive multichannel PCM
  (stereo passthrough is skipped). Timing: output sample k = input k − 2048 (half the 4096 window,
  measured with an impulse); `upmix_pull` restamps `t0 + (out − 2048)/rate`, a rebuilt graph's
  priming covers exactly what the old one held, and `upmix_flush` pushes a window of silence at
  EOF. Runtime filter commands for everything except the crossover (rebuild, ~40 ms dropout).
  level_out +2 dB make-up (the filter alone is −1.3..−3 dB). Settings map (see the comment at the
  top of pc_upmix.c): surround → bl/br_out, centre → fc_out + flx/frx, width → angle 45..135.
  Tests: `regress.sh` "Stereo upmix" (channel placement, every slider's effect, mono, latency
  index 50048, AC3 5.1 bursts, off = stereo PCM); `pcdump --upmix` / `--upmix-set k=v,...`.
  **Live on the Samsung (silent media):** AC3 5.1 upmixed over HDMI, and multichannel PCM 6 ch
  24-bit 44.1 kHz exclusive with the TV's channel order L R LFE C Ls Rs (the CoreAudio remap path).
  Mac UI: Settings > Stereo Upmix (mode Off/Music/Movies & TV/Wide/Custom; sliders surround,
  centre focus, width, LFE; Advanced: crossover, copy/move bass, bass boost, rear delay, rear
  high-cut), U key, Playback menu, badge " · UPMIX". `Upmix.swift` holds presets and keys.
  **Windows (1.3.0, 2026-11-20):** FFmpeg rebuilt with the filters, CMake links avfilter +
  pc_upmix.c. `PlayerModel.Upmix.cs` (presets, same `upmix*` keys and defaults in settings.json,
  `PushUpmix` before every open and on every change; on/off and include-video reopen), Settings
  dialog "Stereo Upmix" (mode, blurb, include video, four sliders, Advanced expander), U key,
  badge " · UPMIX" from `pc_player_upmix_active`. Mode switching lives in `UpmixProfiles.cs`
  (pure, testable): Custom is a saved profile (`upmixCustom` in settings.json, saved when
  leaving Custom, restored when choosing it again; sliders moved on a preset become Custom).
  A user lost their Custom values comparing against Music before this. **The Mac app still
  has that bug** (`UpmixSettings.apply` only knows presets): port the profile there too.
- **Tone (engine + Windows 1.4.0, 2026-11-23):** `PCUpmix.bass_db` (100 Hz low shelf) and
  `treble_db` (6 kHz high shelf, appended to the struct; Native.cs mirrors it), 0..12 dB, Q 0.7.
  - In the upmix graph, before the surround filter (from Surround Bridge, SHARED.md there): our
    FFmpeg has no `treble` filter, so treble is a second `bass` of −T plus +T of gain through
    surround's `fl_in`/`fr_in` (exactly a high shelf of +T for FFmpeg's biquads).
  - **Everything else decoded** (native multichannel, stereo with the upmix off, any mode but
    passthrough) goes through `pc_tone_create`: `bass@ub → bass@ut (−T) → alimiter@ul
    (level_in = +T, limit 0.98, latency=1) → aformat`, in the output's own layout and sample
    format (`tone_deliver` in engine.c, between swr and `deliver`). No latency: output sample
    k = input sample k, so next_pts isn't restamped; `tone_held` (the limiter lookahead) is
    added to the gapless join and the discontinuity check. EOF via `pc_upmix_end` flushes it
    exactly. Flat tone = no graph (bit-exact, regress "tone flat").
  - Live changes come through `upmix_gen`. A graph started cold on a running signal clicked
    (a 2.5x spike), so mid-stream it's created flat and ramps `TONE_RAMP_DB` (1 dB) per chunk
    to the settings; ramping back to flat frees it. At a stream start (open/seek/jump,
    `tone_gen == -1`) it starts at the settings.
  - Tests: regress "Tone" (shelf levels for upmix / native 5.1 / stereo, flat bit-exact, no
    samples lost, channel mapping, no latency, AC3 + DTS transcode, passthrough untouched,
    toggled live with `pcdump --tone-toggle-ms`: no click). `pcdump --no-upmix` keeps the
    `--upmix-set` values with the upmix off.
  - Apps: tone is kept out of the upmix presets/profiles (`UpmixProfiles` no longer has bass;
    `PlayerModel.BassDb/TrebleDb` save and push without touching the mode). Settings ▸ Tone in
    both apps; keys `upmixBassDb`, `upmixTrebleDb`. **The Mac changes (Upmix.swift,
    SettingsView.swift) haven't been compiled**; the Windows dialog wasn't checked by eye.
- **Source format:** `PCInfo.source_rate` / `source_bits` (`audio_stream_bits`:
  `bits_per_raw_sample`, else the sample size of lossless/PCM codecs, else 0 = lossy). The
  source description ends ", 24-bit". Windows shows "24-bit / 96 kHz" beside the output badge
  (`PlayerModel.SourceFormat`; Mac: `sourceFormat`, same place). PCInfo grew at the end:
  `Native.cs` mirrors it. `source_bitrate` (after `source_bits`; `audio_stream_bitrate`: the
  stream's bit_rate, else the file average when there are no other streams): Windows shows
  "320 kb/s / 44.1 kHz" for lossy sources. The Mac UI doesn't use it yet.
- **Open dialogs (Windows):** `FileDialogs.cs` wraps Windows' IFileOpenDialog (files or folders,
  multi-select). WinUI's FolderPicker throws E_FAIL here; FileOpenPicker/FileSavePicker still
  work and are used for Load…/Save… and subtitle files.
- **Gapless (engine + Windows app, 2026-11-20):** `pc_player_set_next(path)`. At demux EOF
  `try_gapless` opens the next file and `gapless_ok` checks it would play the same way (mirrors
  `setup_audio`: audio only, no subtitles, same mode, not passthrough, same transcode channel
  class and upmix, same exclusive-PCM request `mc_req_*`). It compares *plans* (`plan_mode`,
  `plan_up`, `plan_ch`, recorded before the device is opened), not outcomes: when bitstream falls
  back to PCM (non-digital output), comparing with the actual mode refused every join. The demuxer then reads the new file
  (old context kept in `old_fmt` until the next switch) and queues a switch marker
  (`PC_SWITCH_MARK` packet). The audio thread drains the old decoder and swaps in `sw_dec`;
  swr / upmix graph / encoder FIFO keep running. One continuous timeline: a file's pts =
  local pts − start + `a_offset`, where the offset is the join (`next_pts` + swr delay, or the
  upmix graph's input end). The presenter thread's `check_next` fires `PC_EVENT_NEXT` when the
  output clock reaches the join and swaps in `pend_info`/`pend_art`; `item_offset` makes
  `pc_player_position` and `pc_player_seek` relative to the file being heard. A seek while
  `pending` lands in the next file (the app reopens instead). With no next file named yet, EOF
  is held back while > 1 s of audio is queued, so a slightly late `set_next` still joins (short
  files are read to the end at once). Tests: regress "Gapless" (bit-identical PCM join, upmixed
  AC3 without dropout, ineligible file not joined), `pctest play a --then b`. Windows app:
  `PlayerModel.Gapless.cs` (`UpdateGaplessNext` after open, playlist changes and each switch;
  next must start at 0 in another, non-video file; `NextStarted` loads the new track's info).
  Verified in the app with silent files: one open for three tracks, switches exactly 4.0 s apart,
  AC3 upmix and multichannel PCM upmix.
- **Upmixed AC3/DTS is encoded as 5.1(side) too (1.3.2):** FFmpeg's DTS encoder has no 5.1(back),
  so 1.3.0/1.3.1 failed to open it for upmixed stereo in DTS mode. `enc_layout` is 5.1(side)
  for upmixed and native 5.1 alike; the graph's aformat maps back → side. regress "upmix dts".
- **Upmixed multichannel PCM goes out as 5.1(side) at 48 kHz** (was 5.1(back) at the source
  rate): a user's HDMI receiver showed only L/R with the old request. Same request as a native
  5.1 track; the upmix graph's aformat maps the filter's back surrounds onto the sides.
- **Read errors (2026-11-22):** a user's Windows app sometimes stopped mid-track and went to the next
  one. The demux thread treated every `av_read_frame` failure as EOF (and, with gapless, joined
  the next file there). FFmpeg returns I/O errors as themselves (EIO, EACCES...), not
  AVERROR_EOF. Now it clears `pb->error`/`eof_reached` and retries for `READ_RETRY_NS` (6 s,
  covered by the read-ahead), then sends `PC_EVENT_ERROR` "read error: ..." (no EOF follows).
  Both apps (`ReadFailed` / `readFailed`) reopen at the heard position, up to 3 times per track,
  then stop with the error shown. A recovered error loses one parser frame for raw formats
  (FFmpeg flushes parsers on any read error). Test: `scripts/test_read_errors.py` (Windows
  byte-range locks). The app path was tested on Windows with silent WAV. The Swift change
  hasn't been compiled. The user's real cause wasn't confirmed (where their files live is unknown).
- **Read-ahead:** `queue_enough` in engine.c: the demuxer reads until each queue holds
  `READ_AHEAD_SECONDS` (10 s; was 2 s, which let USB-drive stalls pause playback) or
  `READ_AHEAD_PACKETS` for duration-less packets, capped at 256 MB in total.
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

## App behaviour (macOS app; the Windows app should match it)
- **Main window:** the main window is an `NSWindow` + `NSHostingView` (with
  `sceneBridgingOptions = [.toolbars, .title]`) created in `applicationWillFinishLaunching`.
  Don't go back to a SwiftUI `Window` scene: it **doesn't create its window when the app is
  launched to open files** (Finder "Open With" or command-line args).
- **Single-key shortcuts** live in an NSEvent local monitor (`PlayerModel.handleKey`), not
  in menus, so they don't hijack typing in the search field. That covers Space, the arrows,
  ⌘←/⌘→, F, V, [ ], L and Esc. Menu items only show the key in their title.
- **Visualiser:** off by default (`visualizerEnabled`). When on it **overrides the video
  track too**: video keeps decoding in sync, but `VideoSink.isEnabled = false` drops the frames.
- **Full screen:** the playlist is hidden and the stage is laid out outside the `HSplitView`
  (the split view applies the title-bar inset itself, so `.ignoresSafeArea` doesn't work
  inside it). The window gets `.fullSizeContentView` so the picture runs under the top bar.
  - The controls overlay, the top bar and the cursor fade out after 3 s without mouse movement.
  - AppKit keeps the full-screen title bar + toolbar in its own `NSToolbarFullScreenWindow`.
    It's always shown, and `toolbar.isVisible = false` gets undone. So
    `PlayerModel.setTopBarVisible` fades that window's alpha and sets `ignoresMouseEvents`
    while it's hidden. It doesn't hide while the mouse rests on it.
  - Testing: with the display asleep, full screen silently fails to enter. Use
    `caffeinate -u` first.
- **Visualiser presets:** these are original (13 in `VisualizerPresets.swift`). It does
  not load `.milk` files. projectM (OpenGL) was considered and rejected; it's still an option
  if the user wants real MilkDrop presets.
- **Settings keys in UserDefaults (domain `local.surroundplayer`):**
  - Device: `deviceUID`, `deviceName`.
  - Bitstream: `ac3Enabled` (means bitstream on/off), `bitstreamCodec` (0 AC3, 1 DTS),
    `ac3Bitrate`, `dtsBitrate`, `ac3Passthrough`, `stereoAsAC3`, `multichannelPcm`.
    Both apps show these as one "Surround output" choice: DD / DTS / Multichannel PCM / Off
    (Mac: `PlayerModel.surroundOutput`, which sets them together with a single reopen).
  - Upmix: `upmixMode` (0 off, 1 music, 2 movies, 3 wide, 4 custom), `upmixVideo`, `upmixSurroundDb`,
    `upmixCenter`, `upmixWidth`, `upmixLfeDb`, `upmixCrossoverHz`, `upmixLfeMove`,
    `upmixRearDelayMs`, `upmixRearHighcutHz`, `upmixLastMode` (what U turns back on).
  - Tone: `upmixBassDb`, `upmixTrebleDb` (not part of the presets).
  - Other: `avOffsetMs`, `visualizerEnabled`, `subtitleLanguage` ("" = the file's default
    track, "off", "*", or a 2-letter code; set when the user picks a track).
  - `recentFiles`: File > Open Recent, newest first, at most 10 paths. It records what the user
    opened (files, folders or cue sheets, via Open…, drops, Finder), in `PlayerModel.add`
    (Windows: `PlayerModel.Add`, the toolbar's history button). Auto-advance doesn't add to it.
- **Version:** `CFBundleShortVersionString` in `Resources/Info.plist`, shown in Settings. Kept
  in step with the Windows `<Version>` (1.3.2 = both; the Mac app lacks gapless playback).

## Testing — IMPORTANT
- **Windows: run test copies of the app with `SURROUND_DATA_DIR=<scratch folder>`.** The app then
  keeps settings.json and playlist.json there instead of `%LOCALAPPDATA%\SurroundPlayer` (the
  user's real settings, recents and saved playlist). Never delete or overwrite files in
  `%LOCALAPPDATA%\SurroundPlayer`.
- **Never touch the user's real app data.** `~/Library/Application Support/SurroundPlayer/playlist.json`
  (saved playlist; the SwiftPM debug binary uses it too) and the `local.surroundplayer` defaults
  domain hold the user's playlist, recents and settings. On 2026-11-20 a test cleanup deleted the
  user's 5,683-item playlist and their recents. Before any app run, back them up (`cp`,
  `defaults export local.surroundplayer`) and restore them afterwards; never `rm` or
  `defaults delete` them. A log line "restored N playlist items" you didn't create means stop.
- **zsh:** the Bash tool's shell is zsh, where `"$1:s..."` is a substitution modifier. Write
  `${1}`. (`aevalsrc=$1:s=48000:d=6` lost its duration and wrote a 24 GB file.) Cap generated
  media with `-t`, and use `ffmpeg -nostdin`.
- **Never play audible test signals.** The TV is often the default output, and a test once
  played a 440 Hz tone through it. Use **silent media** (`anullsrc`) for anything that hits a
  real device. Tones are fine with `pctest dump` (it writes to a file, nothing plays).
- With no screen-recording or accessibility permission there are no screenshots and no
  synthetic key presses. Verify through:
  - **`scripts/check_portable.sh && scripts/regress.sh`**: the main regression check after
    any engine change on macOS. `regress.sh` is behavioural and runs anywhere with
    bash/ffmpeg/ffprobe/python3; pass it the dump tool path (`pcdump.exe` on Windows).
    `check_portable.sh` is macOS-only.
  - **Byte-level check:** dumps are deterministic for the same build and FFmpeg. Before a
    refactor, dump every mode and `cmp` afterwards.
  - **`pctest`** (macOS):
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
  - **App debug (macOS):** `SURROUND_DEBUG=1` logs status to stderr every second (output, video
    decode, artwork, visualiser frames/signal), plus an `[app] open <file> at <s>` line per
    file open. That's how gapless cue-track switches were checked: one open per file. `SURROUND_TEST_FULLSCREEN=1` toggles full screen after
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
- **Not implemented:** gapless playback in the Mac app (the engine has it, see Gapless),
  E-AC3/TrueHD/DTS-HD bitstreaming over HDMI direct to an AVR (the user's chain is optical,
  so AC3/DTS core is the ceiling), and notarization.
- A clock stall after a seek happened once on the very first bitstream run (when the TV
  first switched to DD) and never reproduced.
- Shared-mode PCM multichannel channel order follows FFmpeg's default layouts, not the
  device's preferred channel layout. (Exclusive multichannel PCM on macOS does follow it.)
