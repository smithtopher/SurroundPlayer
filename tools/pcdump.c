// Plain-C engine driver (no Swift, no OS media APIs when built with the stub
// back-ends). Built twice on Windows: pcdump.exe (stub back-ends, what regress.sh uses)
// and pctest.exe (real audio back-end).
//   pcdump <media> <out> [--pcm] [--dts] [--stereo] [--mc-pcm] [--track N]
//                                                          write the output stream to a file
//   pcdump devices                                         list output devices
//   pcdump play <media> [--device UID] [--pcm] [--dts] [--stereo] [--mc-pcm] [--track N] [--seconds N] [--debug]
//   --mc-pcm: multichannel sources as untranscoded PCM (dump: f32le in the source's channels/rate)
//   --track N: audio track index (0-based)
//                                                          headless playback to a device
// First thing to get running on a new OS; see scripts/check_portable.sh.
// TESTING RULE: only play SILENT media to real devices (ffmpeg -f lavfi -i anullsrc ...).
#include "playercore.h"
#include "../Sources/PlayerCore/pc_platform.h"

#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static _Atomic int done;

static void on_event(void *ctx, int event, const char *msg) {
    (void)ctx;
    if (event == PC_EVENT_EOF) atomic_store(&done, 1);
    if (event == PC_EVENT_ERROR) { fprintf(stderr, "error: %s\n", msg ? msg : "?"); atomic_store(&done, 1); }
}

static void on_frame(void *ctx, PCNativeFrame frame, double pts) {
    (void)frame; (void)pts;
    int n = ++(*(int *)ctx);
#ifdef PCTEST_FRAME_INFO
    // pctest: describe the first frame the presenter hands to the UI
    PCFrameInfo fi;
    if (n == 1 && pc_frame_info(frame, &fi) == 0)
        printf("first frame @ %.3f s: %s %dx%d sar %d:%d, %s, csp %d trc %d range %d\n", pts,
               fi.kind == PC_FRAME_D3D11 ? "D3D11 texture" : "BGRA CPU", fi.width, fi.height, fi.sar_num, fi.sar_den,
               fi.kind == PC_FRAME_D3D11 ? (fi.dxgi_format == 103 ? "NV12" : fi.dxgi_format == 104 ? "P010" : "other format")
                                         : "-",
               fi.color_space, fi.color_trc, fi.color_range);
    if (n == 1 && fi.kind == PC_FRAME_D3D11)
        printf("  slice %d, shader resource %s\n", fi.array_index, fi.shader_resource ? "yes" : "NO (copy needed)");
#else
    (void)n;
#endif
}

static int list_devices(void) {
    PCDevice devs[32];
    int n = pc_list_output_devices(devs, 32);
    for (int i = 0; i < n; i++)
        printf("%s%s\n    uid=%s\n    channels=%d hdmi=%d digital=%d ac3fmt=%d\n", devs[i].is_default ? "* " : "  ",
               devs[i].name, devs[i].uid, devs[i].output_channels, devs[i].is_hdmi, devs[i].is_digital,
               devs[i].has_compressed_formats);
    if (n == 0) printf("no output devices\n");
    return 0;
}

// device hot-plug: the callback runs on an OS thread, so it only raises a flag
static _Atomic int devices_changed;
static void on_devices_changed(void *ctx) { (void)ctx; atomic_store(&devices_changed, 1); }

static double elapsed_s(int64_t since) { return (double)(pc_now_ns() - since) * 1e-9; }

// pcdump watch [--seconds N]: print the device list whenever it changes (plays nothing)
static int watch_devices(double seconds) {
    setvbuf(stdout, NULL, _IONBF, 0);
    pc_set_device_change_callback(on_devices_changed, NULL);
    int64_t start = pc_now_ns();
    printf("[%.1f s] devices:\n", 0.0);
    list_devices();
    while (seconds <= 0 || elapsed_s(start) < seconds) {
        pc_sleep_us(100000);
        if (atomic_exchange(&devices_changed, 0)) {
            pc_sleep_us(300000);                  // let a burst of notifications settle
            atomic_store(&devices_changed, 0);
            printf("[%.1f s] devices changed:\n", elapsed_s(start));
            list_devices();
        }
    }
    pc_set_device_change_callback(NULL, NULL);
    return 0;
}

int main(int argc, char **argv) {
    if (argc >= 2 && !strcmp(argv[1], "devices")) return list_devices();
    if (argc >= 2 && !strcmp(argv[1], "watch"))
        return watch_devices(argc >= 4 && !strcmp(argv[2], "--seconds") ? atof(argv[3]) : 0);
    int play = argc >= 2 && !strcmp(argv[1], "play");
    if (argc < 3) {
        fprintf(stderr, "usage: pcdump <media> <out> [--pcm] [--dts] [--stereo] [--mc-pcm] [--track N]\n"
                        "       pcdump devices\n"
                        "       pcdump watch [--seconds N]\n"
                        "       pcdump play <media> [--device UID] [--pcm] [--dts] [--stereo] [--mc-pcm] [--track N] [--seconds N] [--debug]\n"
                        "         [--sub N] [--sub-file F]  (select a subtitle track / add a file; prints changes)\n"
                        "         (play reopens at the same position when the output is lost)\n");
        return 1;
    }
    setvbuf(stdout, NULL, _IONBF, 0);
    PCConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.bitstream_enabled = 1;
    cfg.bitstream_codec = PC_CODEC_AC3;
    cfg.ac3_bitrate_kbps = 640;
    cfg.dts_bitrate_kbps = 1509;
    cfg.passthrough = 1;
    cfg.audio_track = -1;
    const char *media = argv[play ? 2 : 1];
    if (!play) cfg.dump_path = argv[2];
    double seconds = 0;
    int debug = 0, sub_track = -2;
    const char *sub_file = NULL;
    for (int i = 3; i < argc; i++) {
        if (!strcmp(argv[i], "--pcm")) cfg.bitstream_enabled = 0;
        else if (!strcmp(argv[i], "--dts")) cfg.bitstream_codec = PC_CODEC_DTS;
        else if (!strcmp(argv[i], "--stereo")) cfg.stereo_bitstream = 1;
        else if (!strcmp(argv[i], "--mc-pcm")) cfg.multichannel_pcm = 1;
        else if (!strcmp(argv[i], "--track") && i + 1 < argc) cfg.audio_track = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--debug")) debug = 1;
        else if (!strcmp(argv[i], "--device") && i + 1 < argc) cfg.device_uid = argv[++i];
        else if (!strcmp(argv[i], "--seconds") && i + 1 < argc) seconds = atof(argv[++i]);
        else if (!strcmp(argv[i], "--sub") && i + 1 < argc) sub_track = atoi(argv[++i]);
        else if (!strcmp(argv[i], "--sub-file") && i + 1 < argc) sub_file = argv[++i];
    }
    int frames = 0;
    PCPlayer *p = pc_player_create();
    pc_player_set_callbacks(p, on_frame, on_event, &frames);
    char err[512];
    if (pc_player_open(p, media, &cfg, 0, err, sizeof(err)) != 0) { fprintf(stderr, "open failed: %s\n", err); return 2; }
    PCInfo info;
    pc_player_get_info(p, &info);
    printf("source: %s\noutput: %s\n", info.source_audio, info.output);
    int art_size = 0;
    const uint8_t *art = pc_player_cover_art(p, &art_size);
    if (art && art_size > 0)
        printf("cover art: %d bytes (%s)\n", art_size,
               art_size > 3 && art[0] == 0xFF && art[1] == 0xD8 ? "JPEG" :
               art_size > 3 && art[0] == 0x89 && art[1] == 'P' ? "PNG" : "other");
    int ntracks = pc_player_audio_track_count(p);
    for (int i = 0; ntracks > 1 && i < ntracks; i++) {
        char name[256];
        pc_player_audio_track_name(p, i, name, sizeof(name));
        printf("track %s%s\n", name, i == pc_player_current_audio_track(p) ? "  (playing)" : "");
    }
    if (sub_file) {
        int idx = pc_player_add_subtitle_file(p, sub_file, NULL);
        if (sub_track == -2) sub_track = idx;
    }
    int nsubs = pc_player_subtitle_track_count(p);
    for (int i = 0; i < nsubs; i++) {
        char name[256];
        pc_player_subtitle_track_name(p, i, name, sizeof(name));
        printf("subtitle track %s\n", name);
    }
    if (sub_track >= 0) pc_player_set_subtitle_track(p, sub_track);
    int sub_serial = 0;
    if (play) pc_set_device_change_callback(on_devices_changed, NULL);
    int64_t start = pc_now_ns(), next_report = start + 1000000000LL;
    int is_open = 1;
    double resume_at = 0;
    while (!atomic_load(&done)) {
        pc_sleep_us(20000);
        int64_t now = pc_now_ns();
        if (sub_track >= 0 && is_open) {   // print what a subtitle overlay would show, when it changes
            PCSubtitleState ss;
            pc_player_subtitle_state(p, &ss);
            if (ss.serial != sub_serial) {
                sub_serial = ss.serial;
                double pos = pc_player_position(p);
                if (ss.kind == PC_SUB_TEXT) {
                    char text[1024];
                    pc_player_subtitle_text(p, text, sizeof(text));
                    for (char *c = text; *c; c++) if (*c == '\n') *c = '|';
                    printf("  sub @%.2f: %s\n", pos, text);
                } else if (ss.kind == PC_SUB_BITMAP) {
                    printf("  sub @%.2f: bitmap %dx%d at %d,%d on %dx%d\n", pos, ss.w, ss.h, ss.x, ss.y, ss.ref_w, ss.ref_h);
                } else {
                    printf("  sub @%.2f: -\n", pos);
                }
            }
        }
        if (play && atomic_exchange(&devices_changed, 0)) {
            printf("[%.1f s] devices changed%s\n", elapsed_s(start),
                   is_open && pc_player_output_lost(p) ? ": output lost" : "");
            if (is_open && pc_player_output_lost(p)) {
                // what the app does: close, then reopen the same file at the same position
                resume_at = pc_player_position(p);
                pc_player_close(p);
                is_open = 0;
            }
            if (!is_open) {
                cfg.start_position = resume_at;
                if (pc_player_open(p, media, &cfg, 0, err, sizeof(err)) == 0) {
                    is_open = 1;
                    pc_player_get_info(p, &info);
                    printf("[%.1f s] reopened at %.2f s: %s\n", elapsed_s(start), resume_at, info.output);
                } else {
                    printf("[%.1f s] reopen failed (%s), waiting for the device\n", elapsed_s(start), err);
                }
            }
        }
        if (play && now >= next_report) {
            if (is_open) printf("  %.2f s\n", pc_player_position(p));
            if (is_open && debug) pc_player_debug(p);
            next_report += 1000000000LL;
        }
        if (seconds > 0 && (double)(now - start) * 1e-9 >= seconds) break;
    }
    if (play) pc_set_device_change_callback(NULL, NULL);
    if (!is_open) { pc_player_destroy(p); return 3; }
    pc_player_get_info(p, &info);
    printf("video: %s, frames: %d\n", info.video, frames);
    pc_player_close(p);
    pc_player_destroy(p);
    return 0;
}
