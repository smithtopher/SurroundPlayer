// PlayerCore: FFmpeg-based media engine with a CoreAudio HAL output that can
// bitstream Dolby Digital (AC3) or DTS as IEC 61937 in exclusive (hog) mode, the
// macOS equivalent of Kodi's WASAPI exclusive passthrough on Windows.
#ifndef PLAYERCORE_H
#define PLAYERCORE_H

#include <stdint.h>
#include <CoreVideo/CoreVideo.h>

#ifdef __cplusplus
extern "C" {
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

// pb is only valid for the duration of the call; retain it to keep it.
typedef void (*PCVideoCallback)(void *ctx, CVPixelBufferRef pb, double pts);
typedef void (*PCEventCallback)(void *ctx, int event, const char *message);

PCPlayer *pc_player_create(void);
void pc_player_destroy(PCPlayer *p);
void pc_player_set_callbacks(PCPlayer *p, PCVideoCallback vcb, PCEventCallback ecb, void *ctx);

// Opens and starts playback (paused if `paused`). Returns 0 on success.
int pc_player_open(PCPlayer *p, const char *path, const PCConfig *cfg, int paused, char *err, int errlen);
void pc_player_close(PCPlayer *p);

void pc_player_set_paused(PCPlayer *p, int paused);
int pc_player_is_paused(PCPlayer *p);
void pc_player_seek(PCPlayer *p, double seconds);
double pc_player_position(PCPlayer *p);
void pc_player_set_av_offset(PCPlayer *p, double seconds);
void pc_player_get_info(PCPlayer *p, PCInfo *info);

int pc_player_audio_track_count(PCPlayer *p);
void pc_player_audio_track_name(PCPlayer *p, int index, char *buf, int len);
int pc_player_current_audio_track(PCPlayer *p);

// Embedded cover art (JPEG/PNG bytes), valid until close. NULL if none.
const uint8_t *pc_player_cover_art(PCPlayer *p, int *size);

int pc_list_output_devices(PCDevice *out, int max);

// Visualiser tap. When enabled, decoded audio is also kept as stereo float @ 48 kHz.
// pc_player_vis_samples copies the `frames` L/R frames that are leaving the speakers
// right now (interleaved, so lr must hold frames*2 floats). Returns frames, or 0.
void pc_player_set_vis_enabled(PCPlayer *p, int enabled);
int pc_player_vis_samples(PCPlayer *p, float *lr, int frames);
void pc_player_debug(PCPlayer *p); // dumps internal state to stderr

#ifdef __cplusplus
}
#endif
#endif
