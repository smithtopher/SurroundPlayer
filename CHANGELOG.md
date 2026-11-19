# Changelog

## [1.2.7] — 2026-11-19 (macOS)

The Mac app catches up with Windows 1.2.3–1.2.7.

### macOS
- **Subtitles** (embedded text and PGS/DVD/DVB bitmaps, and subtitle files), drawn over the video.
  - **CC menu** in the controls bar (Off, tracks, Load Subtitle File…). **S** cycles tracks.
  - `.srt`/`.ass`/`.ssa`/`.vtt` files named like the video are added automatically, and
    non-UTF-8 files are converted.
  - A track the file marks as default is shown until you choose otherwise. The language you
    pick is used for the next videos, and forced tracks are shown when nothing else is.
  - Bitmap subtitles are placed by the picture's displayed size, so they also land correctly
    on anamorphic video.
- **The playlist is restored at launch** (exact cue tracks, the current track and its position).
  Play resumes where it was.
- **Playlist files:**
  - File ▸ Save Playlist… (**⌘S**) writes an `.m3u8` with relative paths.
  - File ▸ Load Playlist… (**⌘L**) replaces the playlist.
  - Load…/Save… are also under the playlist.
  - Opening or dropping `.m3u`/`.m3u8` adds their entries, including lists written on Windows.
- The controls bar shows the **source format** ("24-bit / 96 kHz") next to the output badge. When
  space is short it drops labels rather than buttons.

## [1.2.7] — 2026-11-19 (Windows)

### Windows
- **Load…** next to Save… under the playlist (or **Ctrl+L**) replaces the playlist with an
  `.m3u`/`.m3u8` file and plays its first item.

## [1.2.6] — 2026-11-19 (Windows)

### Fixed
- **Embedded subtitles never came on by themselves**, and with a long audio track name the CC
  button was pushed off the controls bar, so they couldn't be chosen either (only "Load
  Subtitle File…" worked). Now:
  - A subtitle track the file marks as default is shown, like mpv and VLC, until you choose
    otherwise; choosing Off is remembered.
  - The controls bar drops labels (audio track name, subtitle language, source format) instead
    of clipping buttons when space is short. The CC button shows a short code ("EN", "Off");
    full names are in the tooltips and menus.

### Engine
- `pc_player_subtitle_track_flags` (default / forced / external). Embedded PGS verified with a
  generated Blu-ray subtitle stream (timing and placement), in `pctest` and in the app.

## [1.2.5] — 2026-11-19 (Windows)

### Engine
- **Subtitles** (`pc_subtitle.c`, portable): every embedded subtitle stream is decoded as the file
  is read, so switching tracks is instant. Text formats (SubRip, ASS/SSA, WebVTT, mov_text...)
  come out as plain text (styling removed); bitmap formats (PGS, DVD, DVB) as BGRA images with
  their placement. Subtitle files can be added as tracks. Timed like the video (audio latency
  setting included). New API: `pc_player_subtitle_*`, `pc_player_add_subtitle_file`.
- `pcdump`/`pctest`: `--sub N` / `--sub-file F` print subtitle changes during playback.

### Windows
- Subtitles drawn over the video. **CC** button in the controls bar (Off, tracks, Load Subtitle
  File…) and the **S** key cycles tracks. `.srt`, `.ass`, `.ssa` and `.vtt` files named like the
  video (`Movie.srt`, `Movie.en.srt`) are added automatically; non-UTF-8 files are converted.
- Subtitles stay off until you pick a track; the language picked is then used for the next
  videos. Forced tracks are shown even when subtitles are off.

## [1.2.4] — 2026-11-19 (Windows)

### Windows
- **The playlist is restored at launch:** saved to `%LOCALAPPDATA%\SurroundPlayer\playlist.json`
  (shortly after each change and at exit), including exact cue sheet tracks, the current track
  and its position. The current track is selected but not playing; Play resumes where it was.
  Entries whose files are gone are dropped.
- **Playlist files:** **Save…** under the playlist (or **Ctrl+S**) writes an `.m3u8` with paths
  relative to its folder; cue sheet tracks are written as their sheet. Opening or dropping an
  `.m3u`/`.m3u8` adds its entries, and it shows in Open Recent.

## [1.2.3] — 2026-11-19 (Windows)

### Engine
- `PCInfo.source_rate` / `source_bits`: the playing track's sample rate and bit depth (0 bits for
  lossy formats). The source description and track names end in ", 24-bit" etc.

### Windows
- The controls bar shows the source format next to the output badge, e.g. "24-bit / 96 kHz", or
  just "48 kHz" for lossy formats.

## [1.2.2] — 2026-11-19 (Windows)

### Windows
- **Open Recent:** a history button next to Open/Add folder (or **Ctrl+R**) lists the last 10
  opened files, folders or cue sheets, with Clear Menu. Same rules as the Mac menu.

## [1.2.1] — 2026-11-19

### macOS
The Mac app catches up with the Windows app's features [1460afd].
- **Multichannel PCM output** in the CoreAudio back-end. Surround goes out as decoded PCM in the
  source's own channel layout and sample rate, with exclusive access and nothing re-encoded.
  It's for HDMI straight into an AV receiver.
  - Speaker routing follows the device's reported channel layout.
  - Falls back to stereo PCM, giving the reason, when the device can't take it.
  - `pctest --mc-pcm` for headless testing.
  - Not yet tried on a real HDMI device.
- **"Surround output" setting:** Dolby Digital / DTS / Multichannel PCM / Off, the same as the
  Windows app.
- **.cue sheets:** a sheet becomes separate playlist tracks, and the audio file it covers is
  hidden. Tracks of the same file play gaplessly (no reopen). Sheets written on Windows
  (backslash paths, Windows-1252 text) work.
- **Folder artwork** (cover.jpg, folder.jpg, …) when a file has no embedded art.
- **Audio tracks:** the track menu shows the current track's name, **A** cycles tracks, and the
  badge shows "PCM 5.1" for untranscoded multichannel.
- **File ▸ Open Recent:** the last 10 opened files, folders or cue sheets.
- Version shown in Settings. `.cue` files can be opened from Finder.
- **Full screen:** the top bar (title bar and toolbar) fades out with the controls after 3 s
  without mouse movement, and the picture now runs under it [cb9773a].

### Windows
- Folder artwork when a file has no embedded art. `pcdump` prints a `cover art:` line [bdbe110].

### Docs
- README covers both macOS and Windows: builds, testing and features [fa53ef0].

## [1.2.0] — 2026-11-18 (Windows)

### Added
- .cue sheet support. Sheets can be UTF-8, UTF-16 or the ANSI code page. A missing `FILE` falls
  back to the same name with another media extension. Consecutive tracks of one file switch
  without reopening (gapless) [efa13d6].

## [1.1.0] — 2026-11-17 (Windows)

### Added
- **Multichannel PCM output** (engine + WASAPI) [797be84].
  - Multichannel sources go out untranscoded in their own layout and rate. Native AC3/DTS
    still pass through.
  - WASAPI tries the source rate and 48 kHz, side/back channel variants, and
    s24-in-32 / s32 / s16 / float.
  - Verified: the Samsung TV accepts 6 ch 24-bit/96 kHz over HDMI.
  - `pcdump --mc-pcm` and `--track N`. `regress.sh` checks multichannel channel mapping and
    track selection.
- "Surround output" setting: Dolby Digital / DTS / Multichannel PCM / Off [797be84].
- Audio track menu labelled with the current track, **A** cycles tracks, "PCM 5.1" badge
  [797be84].
- The app is shared as a single self-contained `.exe`, and the version is shown in Settings
  [be8591f].
- `SURROUND_DEBUG` logs start-up breadcrumbs [be8591f].

## [1.0.0] — 2026-11-17 (Windows)

First Windows 11 build: the same engine, with new back-ends and a WinUI 3 app.
- **Step 1:** FFmpeg 9.0.2 (with the local patches) built with MSVC, plus a CMake + clang-cl
  build of the engine and `pcdump`. `regress.sh` passes on Windows [ccfe799].
- **Step 2:** WASAPI audio back-end [d11c144].
  - Exclusive, event-driven IEC 61937 bitstream with the Dolby Digital / DTS subtypes.
  - Shared float PCM.
  - Bitstreaming only to digital outputs.
  - `pctest devices` / `play`.
  - Verified on the Samsung: the amp shows Dolby Digital. Over HDMI the TV refuses DTS, so DTS
    mode falls back to PCM.
- **Device hot-plug:** device change callback and lost-output detection. Playback reopens at
  the same position when the output goes away (e.g. the TV changes input) [32d4a46].
- **Step 3:** D3D11VA hardware video decoding (NV12/P010 textures shared with the renderer),
  with software frames as BGRA [b91c4b4].
- **Step 4:** WinUI 3 (C#, .NET 8) app on `playercore.dll` [7a17ceb].
  - Playlist with search and drag/drop, device menu, output badge, single-key shortcuts.
  - Full screen with auto-hiding controls.
  - D3D11 video renderer.
  - Settings stored as JSON with the macOS keys.
- DPI-aware window size, remembered between runs (it was tiny at 300% scaling on a 4K TV)
  [17afad4].
- MilkDrop-style visualiser ported: HLSL shaders, the 13 presets, the audio analyser and a D3D11
  renderer [9cae81a].
- `build_win.ps1 -Package`: self-contained app folder + zip [453895a].

## [1.0] — 2026-11-17 (macOS)

First release: a native macOS player (SwiftUI + C engine on FFmpeg) built to get surround
sound to an optical-only amp. The chain is Mac → HDMI → TV → optical → amp. The approach
is modelled on Kodi's WASAPI-exclusive passthrough.

### Added
- **Surround as a bitstream** [037877f].
  - Multichannel audio is decoded, downmixed to 5.1 when needed, encoded to Dolby Digital
    (AC3), and sent as IEC 61937 through CoreAudio.
  - CoreAudio setup: hog mode, mixing off, physical format switched to the TV's digital
    format.
  - Native AC3 passes through untouched.
  - Encoded-silence keep-alive while paused or seeking, so the amp stays locked.
  - The output is kept open between files.
  - Never bitstreams to speakers or headphones.
- **Playback** [037877f].
  - Music and video (mp4, mkv, …), with VideoToolbox hardware video decoding.
  - A/V sync to the audio clock, with an adjustable audio latency.
  - Playlist, audio track selection, device picker with hot-plug handling, output badge.
- **Playlist and builds** [b9a823d].
  - App icon.
  - Folder drag and drop (scanned recursively).
  - Playlist search.
  - Apple Silicon, Intel and universal builds.
- **Visualiser and full screen** [6a002cb].
  - MilkDrop 2-style visualiser (Metal, 13 original presets with blending), off by default.
    When on it also replaces a file's video.
  - Full-screen player with auto-hiding controls.
- **DTS output:** transcode to DTS 5.1 as an alternative to Dolby Digital; in DTS mode native
  DTS passes through too [fb50c99]. Includes a fix for an out-of-bounds read in FFmpeg's DTS
  encoder (`patches/ffmpeg/`).

### Changed
- Engine restructured for porting: a portable core plus per-OS audio/video back-ends, stub
  back-ends, the `pcdump` dump tool and the `regress.sh` behaviour tests. Output was verified
  byte-identical before and after [47b22ca].
- Project context for continuing development in CLAUDE.md and `docs/PORTING_WINDOWS.md`
  [16a5b12, 2ed39d4].
