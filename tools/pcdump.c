// Plain-C engine driver (no Swift, no OS media APIs when built with the stub
// back-ends): writes a file's output stream to disk, like `pctest dump`.
//   pcdump <media> <out> [--pcm] [--dts] [--stereo]
// First thing to get running on a new OS; see scripts/check_portable.sh.
#include "playercore.h"
#include "../Sources/PlayerCore/pc_platform.h"

#include <stdatomic.h>
#include <stdio.h>
#include <string.h>

static _Atomic int done;

static void on_event(void *ctx, int event, const char *msg) {
    (void)ctx; (void)msg;
    if (event == PC_EVENT_EOF) atomic_store(&done, 1);
}

static void on_frame(void *ctx, PCNativeFrame frame, double pts) {
    (void)frame; (void)pts;
    (*(int *)ctx)++;
}

int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: pcdump <media> <out> [--pcm] [--dts] [--stereo]\n"); return 1; }
    PCConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.bitstream_enabled = 1;
    cfg.bitstream_codec = PC_CODEC_AC3;
    cfg.ac3_bitrate_kbps = 640;
    cfg.dts_bitrate_kbps = 1509;
    cfg.passthrough = 1;
    cfg.audio_track = -1;
    cfg.dump_path = argv[2];
    for (int i = 3; i < argc; i++) {
        if (!strcmp(argv[i], "--pcm")) cfg.bitstream_enabled = 0;
        if (!strcmp(argv[i], "--dts")) cfg.bitstream_codec = PC_CODEC_DTS;
        if (!strcmp(argv[i], "--stereo")) cfg.stereo_bitstream = 1;
    }
    int frames = 0;
    PCPlayer *p = pc_player_create();
    pc_player_set_callbacks(p, on_frame, on_event, &frames);
    char err[512];
    if (pc_player_open(p, argv[1], &cfg, 0, err, sizeof(err)) != 0) { fprintf(stderr, "open failed: %s\n", err); return 2; }
    PCInfo info;
    pc_player_get_info(p, &info);
    printf("output: %s\n", info.output);
    while (!atomic_load(&done)) pc_sleep_us(20000);
    pc_player_get_info(p, &info);
    printf("video: %s, frames: %d\n", info.video, frames);
    pc_player_close(p);
    pc_player_destroy(p);
    return 0;
}
