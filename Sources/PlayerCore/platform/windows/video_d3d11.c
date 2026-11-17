// Windows video back-end: D3D11VA hardware decode.
//
// Hardware frames stay on the GPU: the native frame is a refcounted AVFrame whose
// data[0] is the decoder's ID3D11Texture2D (a texture array, NV12 or P010) and data[1]
// the array slice. The decoder pool is created with D3D11_BIND_SHADER_RESOURCE where the
// driver allows it, so the UI can sample the slice directly (pc_frame_info reports it).
// For that the UI must render with the same ID3D11Device: hand it over with
// pc_set_d3d11_device before opening files (otherwise each player creates its own).
// Software-decoded frames are converted to BGRA in CPU memory (like the macOS back-end).
#if defined(_WIN32) && !defined(PC_STUB_BACKENDS) && !defined(PC_STUB_VIDEO)

#define COBJMACROS
#include <windows.h>
#include <d3d11.h>

#include "../../pc_video_backend.h"
#include "playercore.h"

#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>
#include <stdatomic.h>

// Frames held outside the decoder: the engine's queue (FQ_SIZE 6), the one on screen,
// and a couple the UI may keep. Without them the fixed D3D11 texture pool runs dry.
#define PCVB_EXTRA_FRAMES 10

struct PCVideoBackend {
    AVBufferRef *hwdev;
    struct SwsContext *sws;
};

typedef struct { _Atomic int refs; AVFrame *frame; } WinFrame;

// ---------------------------------------------------------------------------
// shared device (set by the app)

static SRWLOCK g_dev_lock = SRWLOCK_INIT;
static ID3D11Device *g_app_device;

void pc_set_d3d11_device(void *device) {
    ID3D11Device *d = device;
    if (d) ID3D11Device_AddRef(d);
    AcquireSRWLockExclusive(&g_dev_lock);
    ID3D11Device *old = g_app_device;
    g_app_device = d;
    ReleaseSRWLockExclusive(&g_dev_lock);
    if (old) ID3D11Device_Release(old);
}

// ---------------------------------------------------------------------------

PCVideoBackend *pcvb_create(void) {
    PCVideoBackend *b = av_mallocz(sizeof(*b));
    if (!b) return NULL;
    AcquireSRWLockShared(&g_dev_lock);
    ID3D11Device *dev = g_app_device;
    if (dev) ID3D11Device_AddRef(dev);
    ReleaseSRWLockShared(&g_dev_lock);
    if (dev) {
        // wrap the app's device; FFmpeg releases it (our reference) on uninit and
        // turns on multithread protection, so decoder and renderer can share it
        b->hwdev = av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA);
        if (b->hwdev) {
            AVD3D11VADeviceContext *h = ((AVHWDeviceContext *)b->hwdev->data)->hwctx;
            h->device = dev;
            dev = NULL;
            if (av_hwdevice_ctx_init(b->hwdev) < 0) av_buffer_unref(&b->hwdev);
        }
        if (dev) ID3D11Device_Release(dev);
    } else {
        av_hwdevice_ctx_create(&b->hwdev, AV_HWDEVICE_TYPE_D3D11VA, NULL, NULL, 0);
    }
    return b;
}

void pcvb_destroy(PCVideoBackend *b) {
    if (!b) return;
    av_buffer_unref(&b->hwdev);
    sws_freeContext(b->sws);
    av_free(b);
}

const char *pcvb_hw_name(void) { return "D3D11VA"; }

// Decoder pool with room for the frames we hold, sampleable by the UI if possible.
static int setup_hw_frames(AVCodecContext *c, int srv) {
    AVBufferRef *frames = NULL;
    if (avcodec_get_hw_frames_parameters(c, c->hw_device_ctx, AV_PIX_FMT_D3D11, &frames) < 0) return -1;
    AVHWFramesContext *fc = (AVHWFramesContext *)frames->data;
    AVD3D11VAFramesContext *h = fc->hwctx;
    if (srv) h->BindFlags |= D3D11_BIND_SHADER_RESOURCE;
    if (fc->initial_pool_size > 0) fc->initial_pool_size += PCVB_EXTRA_FRAMES;
    if (av_hwframe_ctx_init(frames) < 0) { av_buffer_unref(&frames); return -1; }
    av_buffer_unref(&c->hw_frames_ctx);
    c->hw_frames_ctx = frames;
    return 0;
}

static enum AVPixelFormat get_hw_format(AVCodecContext *c, const enum AVPixelFormat *fmts) {
    for (const enum AVPixelFormat *f = fmts; *f != AV_PIX_FMT_NONE; f++)
        if (*f == AV_PIX_FMT_D3D11 && c->hw_device_ctx &&
            (setup_hw_frames(c, 1) == 0 || setup_hw_frames(c, 0) == 0))
            return *f;
    for (const enum AVPixelFormat *f = fmts; *f != AV_PIX_FMT_NONE; f++) {
        const AVPixFmtDescriptor *d = av_pix_fmt_desc_get(*f);
        if (d && !(d->flags & AV_PIX_FMT_FLAG_HWACCEL)) return *f;
    }
    return fmts[0];
}

int pcvb_prepare_decoder(PCVideoBackend *b, AVCodecContext *dec) {
    if (!b || !b->hwdev) return 0;
    dec->hw_device_ctx = av_buffer_ref(b->hwdev);
    dec->get_format = get_hw_format;
    return 1;
}

static AVFrame *sw_frame_to_bgra(PCVideoBackend *b, const AVFrame *f) {
    AVFrame *o = av_frame_alloc();
    if (!o) return NULL;
    o->format = AV_PIX_FMT_BGRA;
    o->width = f->width;
    o->height = f->height;
    if (av_frame_get_buffer(o, 32) < 0) { av_frame_free(&o); return NULL; }
    b->sws = sws_getCachedContext(b->sws, f->width, f->height, f->format, f->width, f->height,
                                  AV_PIX_FMT_BGRA, SWS_BILINEAR, NULL, NULL, NULL);
    if (!b->sws) { av_frame_free(&o); return NULL; }
    int cs = f->colorspace != AVCOL_SPC_UNSPECIFIED ? f->colorspace : (f->height >= 720 ? AVCOL_SPC_BT709 : AVCOL_SPC_SMPTE170M);
    int src_full = f->color_range == AVCOL_RANGE_JPEG;
    sws_setColorspaceDetails(b->sws, sws_getCoefficients(cs), src_full, sws_getCoefficients(AVCOL_SPC_BT709), 1, 0,
                             1 << 16, 1 << 16);
    sws_scale(b->sws, (const uint8_t *const *)f->data, f->linesize, 0, f->height, o->data, o->linesize);
    o->sample_aspect_ratio = f->sample_aspect_ratio;
    o->colorspace = AVCOL_SPC_BT709;
    o->color_primaries = AVCOL_PRI_BT709;
    o->color_trc = AVCOL_TRC_BT709;
    o->color_range = AVCOL_RANGE_JPEG;
    return o;
}

void *pcvb_make_frame(PCVideoBackend *b, const AVFrame *f, int *hw) {
    *hw = f->format == AV_PIX_FMT_D3D11;
    WinFrame *w = av_mallocz(sizeof(*w));
    if (!w) return NULL;
    w->frame = *hw ? av_frame_clone(f) : sw_frame_to_bgra(b, f);
    if (!w->frame) { av_free(w); return NULL; }
    atomic_store(&w->refs, 1);
    return w;
}

void pcvb_retain(void *frame) { atomic_fetch_add(&((WinFrame *)frame)->refs, 1); }

void pcvb_release(void *frame) {
    WinFrame *w = frame;
    if (w && atomic_fetch_sub(&w->refs, 1) == 1) {
        av_frame_free(&w->frame);
        av_free(w);
    }
}

void pc_frame_retain(PCNativeFrame frame) { if (frame) pcvb_retain(frame); }
void pc_frame_release(PCNativeFrame frame) { pcvb_release(frame); }

int pc_frame_info(PCNativeFrame frame, PCFrameInfo *out) {
    memset(out, 0, sizeof(*out));
    WinFrame *w = frame;
    if (!w || !w->frame) return -1;
    const AVFrame *f = w->frame;
    out->width = f->width;
    out->height = f->height;
    out->sar_num = f->sample_aspect_ratio.num > 0 ? f->sample_aspect_ratio.num : 1;
    out->sar_den = f->sample_aspect_ratio.den > 0 ? f->sample_aspect_ratio.den : 1;
    out->color_space = f->colorspace;
    out->color_primaries = f->color_primaries;
    out->color_trc = f->color_trc;
    out->color_range = f->color_range;
    if (f->format == AV_PIX_FMT_D3D11) {
        ID3D11Texture2D *tex = (ID3D11Texture2D *)f->data[0];
        D3D11_TEXTURE2D_DESC desc;
        ID3D11Texture2D_GetDesc(tex, &desc);
        out->kind = PC_FRAME_D3D11;
        out->texture = tex;
        out->array_index = (int)(intptr_t)f->data[1];
        out->dxgi_format = (int)desc.Format;
        out->shader_resource = (desc.BindFlags & D3D11_BIND_SHADER_RESOURCE) != 0;
    } else {
        out->kind = PC_FRAME_BGRA;
        out->bgra = f->data[0];
        out->stride = f->linesize[0];
    }
    return 0;
}

#endif
