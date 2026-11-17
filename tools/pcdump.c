// Plain-C engine driver (no Swift, no OS media APIs when built with the stub
// back-ends). Built twice on Windows: pcdump.exe (stub back-ends, what regress.sh uses)
// and pctest.exe (real audio back-end).
//   pcdump <media> <out> [--pcm] [--dts] [--stereo]       write the output stream to a file
//   pcdump devices                                         list output devices
//   pcdump play <media> [--device UID] [--pcm] [--dts] [--stereo] [--seconds N] [--debug]
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
    (*(int *)ctx)++;
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

int main(int argc, char **argv) {
    if (argc >= 2 && !strcmp(argv[1], "devices")) return list_devices();
    int play = argc >= 2 && !strcmp(argv[1], "play");
    if (argc < 3) {
        fprintf(stderr, "usage: pcdump <media> <out> [--pcm] [--dts] [--stereo]\n"
                        "       pcdump devices\n"
                        "       pcdump play <media> [--device UID] [--pcm] [--dts] [--stereo] [--seconds N] [--debug]\n");
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
    int debug = 0;
    for (int i = 3; i < argc; i++) {
        if (!strcmp(argv[i], "--pcm")) cfg.bitstream_enabled = 0;
        else if (!strcmp(argv[i], "--dts")) cfg.bitstream_codec = PC_CODEC_DTS;
        else if (!strcmp(argv[i], "--stereo")) cfg.stereo_bitstream = 1;
        else if (!strcmp(argv[i], "--debug")) debug = 1;
        else if (!strcmp(argv[i], "--device") && i + 1 < argc) cfg.device_uid = argv[++i];
        else if (!strcmp(argv[i], "--seconds") && i + 1 < argc) seconds = atof(argv[++i]);
    }
    int frames = 0;
    PCPlayer *p = pc_player_create();
    pc_player_set_callbacks(p, on_frame, on_event, &frames);
    char err[512];
    if (pc_player_open(p, media, &cfg, 0, err, sizeof(err)) != 0) { fprintf(stderr, "open failed: %s\n", err); return 2; }
    PCInfo info;
    pc_player_get_info(p, &info);
    printf("source: %s\noutput: %s\n", info.source_audio, info.output);
    int64_t start = pc_now_ns(), next_report = start + 1000000000LL;
    while (!atomic_load(&done)) {
        pc_sleep_us(20000);
        int64_t now = pc_now_ns();
        if (play && now >= next_report) {
            printf("  %.2f s\n", pc_player_position(p));
            if (debug) pc_player_debug(p);
            next_report += 1000000000LL;
        }
        if (seconds > 0 && (double)(now - start) * 1e-9 >= seconds) break;
    }
    pc_player_get_info(p, &info);
    printf("video: %s, frames: %d\n", info.video, frames);
    pc_player_close(p);
    pc_player_destroy(p);
    return 0;
}
