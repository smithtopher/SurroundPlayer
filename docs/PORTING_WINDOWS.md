# Porting Surround Player to Windows 11

The engine is split into a portable core plus small per-OS back-ends. A Windows
port means writing two back-ends and a new UI. The core (demux, decode, AC3/DTS
encode, IEC 61937 packing, queues, seeking, A/V sync, keep-alive silence, audio
clock, visualiser tap) is shared as-is.

```
Sources/PlayerCore/
  include/playercore.h      public C API (PC_API = dllexport/dllimport on Windows)
  engine.c                  portable
  pc_output.c / .h          portable: ring buffer, burst logic, render callback, clock
  pc_platform.h             threads / locks / clock / sleep — POSIX and Win32 already done
  pc_audio_device.h         audio back-end interface   ← implement with WASAPI
  pc_video_backend.h        video back-end interface   ← implement with D3D11VA
  platform/apple/           macOS back-ends (reference implementations)
  platform/stub/            no-device / software-video back-ends (bring-up)
```

`scripts/check_portable.sh` proves on every run that `engine.c` and `pc_output.c` use no
OS media APIs. They build against the stub back-ends with no framework headers or
symbols, and that build produces byte-identical output to the macOS build.

## Step 0 — before starting

- **Ask the user how this PC is connected** (HDMI to the Samsung TV? amp on optical?).
  Agree that device tests use silent media only.
- The repo's `.gitattributes` keeps `*.sh` / `*.patch` LF. If scripts fail with `$'\r'`
  errors, the checkout predates it: run `git add --renormalize .` or re-clone.
- `pc_platform.h`'s Win32 branch has **never been compiled** (it was written on macOS).
  Expect small fixes. Check thread creation, the QPC clock and SRWLOCK/condition-variable
  pairing first.
- There is no Windows build system yet. Suggested: a `CMakeLists.txt` (clang-cl) with
  three targets: `playercore` (static, stub back-ends, for step 1), `pcdump`, and later
  `playercore.dll` with the real back-ends.

## Step 1 — engine builds and dumps on Windows (stub back-ends)

1. **Toolchain.** The C engine uses C11 `<stdatomic.h>`, including `_Atomic double`.
   Use **clang-cl** (Visual Studio's LLVM component) or MSYS2 clang. MSVC's own
   C11 atomics (`/std:c11 /experimental:c11atomics`) are experimental.
2. **FFmpeg 9.0.2 with the patch in `patches/ffmpeg/`.** Apply it: stock FFmpeg's
   DTS encoder reads out of bounds and can crash (see the patch header). Build it in
   MSYS2 with `--toolchain=msvc` (or clang), using the configure flags from
   `scripts/build_ffmpeg.sh`. Replace `--enable-videotoolbox` with
   `--enable-d3d11va --enable-dxva2`. The encoders needed are `ac3` and `dca`, the muxer
   `spdif`. Shared (DLL) builds are fine on Windows (LGPL).
3. **Build `engine.c`, `pc_output.c`, `platform/stub/*.c` and `tools/pcdump.c`** with
   `-DPC_STUB_BACKENDS`. Link FFmpeg and `winmm` (for `timeBeginPeriod`).
4. **Run `bash scripts/regress.sh <path-to-pcdump.exe>`** from MSYS2/Git Bash (it needs
   ffmpeg, ffprobe and python3 on PATH). It must print `ALL PASSED`. That proves
   decode, AC3/DTS encode, IEC 61937 packing, channel mapping and passthrough all
   work on Windows before any device code exists.

## Step 2 — WASAPI audio back-end (`platform/windows/audio_wasapi.c`)

Implement `pc_audio_device.h`. The macOS version (`platform/apple/audio_coreaudio.c`)
is the reference for behaviour.

**`pcad_list`:**
- Enumerate with `IMMDeviceEnumerator::EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)`.
- `uid` is `IMMDevice::GetId`, `name` is `PKEY_Device_FriendlyName`.
- `is_hdmi`: `PKEY_AudioEndpoint_FormFactor == DigitalAudioDisplayDevice` (HDMI/DP).
  SPDIF endpoints count as digital too.
- `has_compressed_formats`: the endpoint accepts an exclusive-mode IEC 61937 AC3
  format (`IAudioClient::IsFormatSupported`).

**`pcad_open(uid, type, fmt, …)`:**
- **`PC_STREAM_PCM`:** shared mode. Use `GetMixFormat` (normally 32-bit float) and
  report it in `fmt`. The core requires float in PCM mode; otherwise pass
  `AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM` with a float format.
- **`PC_STREAM_IEC61937_AC3` / `_DTS`:** this is what Kodi does.
  - Exclusive mode, event-driven: `AUDCLNT_SHAREMODE_EXCLUSIVE` +
    `AUDCLNT_STREAMFLAGS_EVENTCALLBACK`.
  - The format is a `WAVEFORMATEXTENSIBLE`: 2 channels, 48000 Hz, 16 bits, with
    `SubFormat = KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL` or `…_IEC61937_DTS`.
    That's why the interface passes the stream type: Windows must be told the payload.
  - Report `fmt` as 2 ch / 16-bit int / 48 kHz. The core then copies bursts straight
    into the buffer.
  - **Refuse non-digital endpoints** (speakers, headphones, Bluetooth). IEC 61937
    played as PCM is loud noise. Never relax this.
  - On `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED`, re-initialise with the aligned period
    (the documented WASAPI dance).
- **`fmt->latency`** = `IAudioClient::GetStreamLatency` plus one device period.

**`pcad_start(cb, ctx)`:**
- Start a render thread with `AvSetMmThreadCharacteristics(L"Pro Audio")`.
- Loop on the event: `GetBuffer`, call `cb(ctx, buf, frames, present_ns)`, `ReleaseBuffer`.
- `present_ns` must be in **`pc_now_ns()`'s timebase (QPC)**, computed from
  `IAudioClock::GetPosition(&pos, &qpc)`. `qpc` is in 100 ns units, at the moment
  `pos` (in device frequency units) was being played. So:
  `present_ns = qpc*100 + (frames_written_so_far - pos_in_frames) / rate * 1e9`.
  The core turns that into the clock that video and the visualiser follow.
- The callback must not block. It doesn't lock anything, and the core keeps it that way.

**`pcad_close`:** stop the thread, `IAudioClient::Stop`, release everything. WASAPI
needs no device state restored, unlike CoreAudio.

**Device changes:** register an `IMMNotificationClient`. HDMI endpoints come and go
when the TV changes input. Surface that to the app so it can reopen at the same
position, like `PlayerModel.handleDeviceChange` on macOS.

**Milestone:** `pctest`-style headless playback to the TV (`pcdump`, extended with
a play mode), testing with silent files only. Check the TV/amp shows Dolby Digital/DTS.

## Step 3 — video back-end (`platform/windows/video_d3d11.c`)

Implement `pc_video_backend.h`:
- **`pcvb_create`:**
  `av_hwdevice_ctx_create(AV_HWDEVICE_TYPE_D3D11VA, …)`. Better: wrap the app's own
  `ID3D11Device` (`AVD3D11VADeviceContext`) so decoded textures can be sampled
  directly by the renderer without copies.
- **`pcvb_prepare_decoder`:** set `hw_device_ctx`, and a `get_format` that picks
  `AV_PIX_FMT_D3D11`.
- **`pcvb_make_frame`:** like `platform/stub/video_stub.c`, return a refcounted
  `AVFrame` clone. For hardware frames, `data[0]` is the `ID3D11Texture2D*` and
  `data[1]` the array index. For software frames, either upload them or let the UI
  handle CPU frames.
- `PCNativeFrame` is then that refcounted frame. The UI reads texture and index from
  it in the video callback (add a tiny accessor to the public API).

## Step 4 — app (WinUI 3, C#)

- **Engine:** build it as `playercore.dll` with `-DPC_BUILD_DLL`, and P/Invoke the
  functions in `playercore.h`. The callbacks are `[UnmanagedFunctionPointer]`
  delegates; keep them alive. Call `timeBeginPeriod(1)` at start-up.
- **Video and visualiser:** a `SwapChainPanel` with a D3D11 swap chain.
  - Video frames arrive already timed to the audio clock, so just present the latest one.
  - The visualiser shaders port almost line-for-line to HLSL (Metal source is in
    `Visualizer/VisualizerShaders.swift`).
  - Presets and audio analysis are plain logic (`VisualizerPresets.swift`,
    `AudioAnalyzer.swift`). The FFT is Accelerate there; use any FFT on Windows.
- **Features to match:** playlist with folder drop, search, settings (same keys as
  `PlayerModel`), full screen, the single-key shortcuts, and the output badge from
  `PCInfo.output` / `bitstream_codec`.

## Testing rules (same as on macOS)

- **Never play audible test signals** on the TV/amp; use silent media. Tones are fine
  in dump mode (files only).
- `scripts/regress.sh` must stay green on both OSes.
