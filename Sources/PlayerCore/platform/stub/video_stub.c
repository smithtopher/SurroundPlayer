// Stub video back-end: software decoding only; native frames are refcounted
// AVFrame clones. Build with -DPC_STUB_BACKENDS.
#ifdef PC_STUB_BACKENDS

#include "../../pc_video_backend.h"
#include <stdatomic.h>

typedef struct { _Atomic int refs; AVFrame *frame; } StubFrame;

PCVideoBackend *pcvb_create(void) { return (PCVideoBackend *)av_mallocz(1); }
void pcvb_destroy(PCVideoBackend *b) { av_free(b); }
int pcvb_prepare_decoder(PCVideoBackend *b, AVCodecContext *dec) { (void)b; (void)dec; return 0; }
const char *pcvb_hw_name(void) { return "none"; }

void *pcvb_make_frame(PCVideoBackend *b, const AVFrame *f, int *hw) {
    (void)b;
    *hw = 0;
    StubFrame *s = av_mallocz(sizeof(*s));
    if (!s) return NULL;
    s->frame = av_frame_clone(f);
    if (!s->frame) { av_free(s); return NULL; }
    atomic_store(&s->refs, 1);
    return s;
}

void pcvb_retain(void *frame) { atomic_fetch_add(&((StubFrame *)frame)->refs, 1); }

void pcvb_release(void *frame) {
    StubFrame *s = frame;
    if (s && atomic_fetch_sub(&s->refs, 1) == 1) {
        av_frame_free(&s->frame);
        av_free(s);
    }
}

#endif
