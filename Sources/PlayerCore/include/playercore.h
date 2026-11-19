// PlayerCore: FFmpeg-based media engine with a CoreAudio HAL output that can
// bitstream Dolby Digital (AC3) or DTS as IEC 61937 in exclusive (hog) mode, the
// macOS equivalent of Kodi's WASAPI exclusive passthrough on Windows.
#ifndef PLAYERCORE_H
#define PLAYERCORE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Windows: build the engine as playercore.dll with -DPC_BUILD_DLL (exports), consumers
// get imports. Elsewhere it's a plain static library.
#if defined(_WIN32) && defined(PC_BUILD_DLL)
#define PC_API __declspec(dllexport)
#elif defined(_WIN32) && defined(PC_USE_DLL)
#define PC_API __declspec(dllimport)
#else
#define PC_API
#endif

typedef struct PCPlayer PCPlayer;

typedef enum {
    PC_MODE_PCM = 0,             // decoded PCM, shared (mixable) device access
    PC_MODE_TRANSCODE = 1,       // decode -> AC3/DTS encode -> IEC 61937, exclusive
    PC_MODE_PASSTHROUGH = 2,     // source AC3/DTS packets -> IEC 61937, exclusive
} PCOutputMode;

typedef enum {
    PC_CODEC_AC3 = 0,
    PC_CODEC_DTS = 1,
} PCBitstreamCodec;

typedef enum {
    PC_EVENT_EOF = 1,
    PC_EVENT_ERROR = 2,
} PCEvent;

typedef struct {
    const char *device_uid;     // CoreAudio device UID, NULL/"" = system default output
    int bitstream_enabled;      // compressed (IEC 61937) output allowed on digital devices
    int bitstream_codec;        // PC_CODEC_AC3 or PC_CODEC_DTS: what surround is transcoded to
    int ac3_bitrate_kbps;       // 384 / 448 / 640
    int dts_bitrate_kbps;       // 1509 (full rate) / 754 (half rate)
    int stereo_bitstream;       // also encode stereo sources (keeps the receiver locked)
    int passthrough;            // pass native AC3 (and, in DTS mode, DTS core) tracks through untouched
    int audio_track;            // index into the file's audio tracks, -1 = best
    double start_position;      // seconds
    double av_offset;           // seconds; >0 delays video (compensates receiver decode latency)
    const char *dump_path;      // testing: write output stream to this file instead of a device
    int multichannel_pcm;       // don't transcode: multichannel sources go out as decoded PCM in
                                // their own layout and rate, exclusive and bit-exact (HDMI to an AVR).
                                // Native AC3/DTS still pass through if `passthrough`; stereo stays shared PCM.
} PCConfig;

typedef struct {
    char title[256];
    char artist[256];
    char album[256];
    char source_audio[256];     // e.g. "E-AC3 5.1(side) 48000 Hz"
    char output[512];           // e.g. "Dolby Digital 5.1 640 kb/s, IEC 61937 bitstream -> LG TV (HDMI), exclusive"
    char video[256];
    double duration;
    int has_video;
    int mode;                   // PCOutputMode
    char bitstream_codec[32];   // "Dolby Digital" / "DTS" when bitstreaming
    int video_width, video_height;
    int source_rate;            // Hz of the playing audio track; 0 if none
    int source_bits;            // bits per sample of the source (PCM, lossless, or what a DTS/
                                // MLP header declares); 0 for lossy formats that have none
} PCInfo;

typedef struct {
    char uid[256];
    char name[256];
    int output_channels;
    int is_hdmi;
    int is_digital;             // HDMI / DisplayPort / optical
    int has_compressed_formats; // device advertises an AC3 physical format
    int is_default;
} PCDevice;

// A decoded video frame, ready to display. Its type depends on the platform's video
// back-end: macOS: CVPixelBufferRef; Windows: opaque, read it with pc_frame_info;
// stub back-end: refcounted AVFrame. Only valid for the duration of the callback;
// retain it with the platform's own mechanism (macOS: CVPixelBufferRetain; Windows:
// pc_frame_retain / pc_frame_release) to keep it.
typedef void *PCNativeFrame;

#ifdef _WIN32
typedef enum {
    PC_FRAME_D3D11 = 1,         // GPU: slice `array_index` of `texture` (NV12 / P010)
    PC_FRAME_BGRA = 2,          // CPU: `bgra` rows of `stride` bytes, full-range BT.709
} PCFrameKind;

typedef struct {
    int kind;                   // PCFrameKind
    int width, height;          // visible size (the texture can be larger: decoder alignment)
    int sar_num, sar_den;       // sample aspect ratio (1:1 if unknown)
    int color_space, color_primaries, color_trc, color_range;   // FFmpeg AVColor* values
    void *texture;              // ID3D11Texture2D* (texture array), PC_FRAME_D3D11 only
    int array_index;
    int dxgi_format;            // DXGI_FORMAT of the texture (103 NV12, 104 P010)
    int shader_resource;        // 1: the slice can be bound as an SRV directly; 0: copy it first
    const uint8_t *bgra;        // PC_FRAME_BGRA only
    int stride;
} PCFrameInfo;

// The device the UI renders with. Decoded textures live on it, so the UI can sample
// them without copies. Call before pc_player_open (affects players opened later); NULL
// = each player creates its own device. The engine takes a reference.
PC_API void pc_set_d3d11_device(void *id3d11device);
PC_API int pc_frame_info(PCNativeFrame frame, PCFrameInfo *out);
PC_API void pc_frame_retain(PCNativeFrame frame);
PC_API void pc_frame_release(PCNativeFrame frame);
#endif
typedef void (*PCVideoCallback)(void *ctx, PCNativeFrame frame, double pts);
typedef void (*PCEventCallback)(void *ctx, int event, const char *message);

PC_API PCPlayer *pc_player_create(void);
PC_API void pc_player_destroy(PCPlayer *p);
PC_API void pc_player_set_callbacks(PCPlayer *p, PCVideoCallback vcb, PCEventCallback ecb, void *ctx);

// Opens and starts playback (paused if `paused`). Returns 0 on success.
PC_API int pc_player_open(PCPlayer *p, const char *path, const PCConfig *cfg, int paused, char *err, int errlen);
PC_API void pc_player_close(PCPlayer *p);

PC_API void pc_player_set_paused(PCPlayer *p, int paused);
PC_API int pc_player_is_paused(PCPlayer *p);
PC_API void pc_player_seek(PCPlayer *p, double seconds);
PC_API double pc_player_position(PCPlayer *p);
PC_API void pc_player_set_av_offset(PCPlayer *p, double seconds);
PC_API void pc_player_get_info(PCPlayer *p, PCInfo *info);

PC_API int pc_player_audio_track_count(PCPlayer *p);
PC_API void pc_player_audio_track_name(PCPlayer *p, int index, char *buf, int len);
PC_API int pc_player_current_audio_track(PCPlayer *p);

// Embedded cover art (JPEG/PNG bytes), valid until close. NULL if none.
PC_API const uint8_t *pc_player_cover_art(PCPlayer *p, int *size);

PC_API int pc_list_output_devices(PCDevice *out, int max);

// Device hot-plug. `cb` runs on an OS thread whenever output devices appear, disappear,
// change state or the default changes, and when an open output stream is invalidated
// (e.g. the TV changed input). Don't block or call the engine from it: post to your own
// thread, then re-list devices and check pc_player_output_lost. NULL unregisters.
// Windows (IMMNotificationClient). macOS: not implemented (the app listens to CoreAudio itself).
typedef void (*PCDeviceChangeCallback)(void *ctx);
PC_API void pc_set_device_change_callback(PCDeviceChangeCallback cb, void *ctx);
// 1 when the open audio output was invalidated and must be reopened (same file, same position).
PC_API int pc_player_output_lost(PCPlayer *p);

// Visualiser tap. When enabled, decoded audio is also kept as stereo float @ 48 kHz.
// pc_player_vis_samples copies the `frames` L/R frames that are leaving the speakers
// right now (interleaved, so lr must hold frames*2 floats). Returns frames, or 0.
PC_API void pc_player_set_vis_enabled(PCPlayer *p, int enabled);
PC_API int pc_player_vis_samples(PCPlayer *p, float *lr, int frames);
PC_API void pc_player_debug(PCPlayer *p); // dumps internal state to stderr

#ifdef __cplusplus
}
#endif
#endif
