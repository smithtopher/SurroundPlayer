// macOS video back-end: VideoToolbox hardware decode (frames arrive as
// CVPixelBuffers), software frames converted to BGRA CVPixelBuffers from a pool.
// The native frame type handed to the UI is CVPixelBufferRef.
#if defined(__APPLE__) && !defined(PC_STUB_BACKENDS)

#include "../../pc_video_backend.h"

#include <CoreVideo/CoreVideo.h>
#include <libavutil/hwcontext.h>
#include <libavutil/pixdesc.h>
#include <libswscale/swscale.h>

struct PCVideoBackend {
    AVBufferRef *hwdev;
    struct SwsContext *sws;
    CVPixelBufferPoolRef pool;
    int pool_w, pool_h;
};

PCVideoBackend *pcvb_create(void) {
    PCVideoBackend *b = av_mallocz(sizeof(*b));
    av_hwdevice_ctx_create(&b->hwdev, AV_HWDEVICE_TYPE_VIDEOTOOLBOX, NULL, NULL, 0);
    return b;
}

void pcvb_destroy(PCVideoBackend *b) {
    if (!b) return;
    av_buffer_unref(&b->hwdev);
    sws_freeContext(b->sws);
    if (b->pool) CVPixelBufferPoolRelease(b->pool);
    av_free(b);
}

const char *pcvb_hw_name(void) { return "VideoToolbox"; }

static enum AVPixelFormat get_hw_format(AVCodecContext *c, const enum AVPixelFormat *fmts) {
    (void)c;
    for (const enum AVPixelFormat *f = fmts; *f != AV_PIX_FMT_NONE; f++)
        if (*f == AV_PIX_FMT_VIDEOTOOLBOX) return *f;
    for (const enum AVPixelFormat *f = fmts; *f != AV_PIX_FMT_NONE; f++) {
        const AVPixFmtDescriptor *d = av_pix_fmt_desc_get(*f);
        if (d && !(d->flags & AV_PIX_FMT_FLAG_HWACCEL)) return *f;
    }
    return fmts[0];
}

int pcvb_prepare_decoder(PCVideoBackend *b, AVCodecContext *dec) {
    if (!b->hwdev) return 0;
    dec->hw_device_ctx = av_buffer_ref(b->hwdev);
    dec->get_format = get_hw_format;
    return 1;
}

static void set_sar(CVPixelBufferRef pb, AVRational sar) {
    if (sar.num <= 0 || sar.den <= 0 || sar.num == sar.den) return;
    CFNumberRef h = CFNumberCreate(NULL, kCFNumberIntType, &sar.num);
    CFNumberRef v = CFNumberCreate(NULL, kCFNumberIntType, &sar.den);
    const void *keys[] = { kCVImageBufferPixelAspectRatioHorizontalSpacingKey, kCVImageBufferPixelAspectRatioVerticalSpacingKey };
    const void *vals[] = { h, v };
    CFDictionaryRef d = CFDictionaryCreate(NULL, keys, vals, 2, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CVBufferSetAttachment(pb, kCVImageBufferPixelAspectRatioKey, d, kCVAttachmentMode_ShouldPropagate);
    CFRelease(d); CFRelease(h); CFRelease(v);
}

static CVPixelBufferRef sw_frame_to_pb(PCVideoBackend *b, const AVFrame *f) {
    if (!b->pool || b->pool_w != f->width || b->pool_h != f->height) {
        if (b->pool) CVPixelBufferPoolRelease(b->pool);
        b->pool = NULL;
        int fmt = kCVPixelFormatType_32BGRA;
        CFNumberRef w = CFNumberCreate(NULL, kCFNumberIntType, &f->width);
        CFNumberRef h = CFNumberCreate(NULL, kCFNumberIntType, &f->height);
        CFNumberRef pf = CFNumberCreate(NULL, kCFNumberIntType, &fmt);
        CFDictionaryRef io = CFDictionaryCreate(NULL, NULL, NULL, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        const void *keys[] = { kCVPixelBufferWidthKey, kCVPixelBufferHeightKey, kCVPixelBufferPixelFormatTypeKey, kCVPixelBufferIOSurfacePropertiesKey };
        const void *vals[] = { w, h, pf, io };
        CFDictionaryRef attrs = CFDictionaryCreate(NULL, keys, vals, 4, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        CVPixelBufferPoolCreate(NULL, NULL, attrs, &b->pool);
        CFRelease(attrs); CFRelease(io); CFRelease(pf); CFRelease(h); CFRelease(w);
        b->pool_w = f->width;
        b->pool_h = f->height;
    }
    CVPixelBufferRef pb = NULL;
    if (!b->pool || CVPixelBufferPoolCreatePixelBuffer(NULL, b->pool, &pb) != kCVReturnSuccess) return NULL;
    b->sws = sws_getCachedContext(b->sws, f->width, f->height, f->format, f->width, f->height,
                                  AV_PIX_FMT_BGRA, SWS_BILINEAR, NULL, NULL, NULL);
    if (!b->sws) { CVPixelBufferRelease(pb); return NULL; }
    int cs = f->colorspace != AVCOL_SPC_UNSPECIFIED ? f->colorspace : (f->height >= 720 ? AVCOL_SPC_BT709 : AVCOL_SPC_SMPTE170M);
    int src_full = f->color_range == AVCOL_RANGE_JPEG;
    sws_setColorspaceDetails(b->sws, sws_getCoefficients(cs), src_full, sws_getCoefficients(AVCOL_SPC_BT709), 1, 0, 1 << 16, 1 << 16);
    CVPixelBufferLockBaseAddress(pb, 0);
    uint8_t *dst[4] = { CVPixelBufferGetBaseAddress(pb), NULL, NULL, NULL };
    int stride[4] = { (int)CVPixelBufferGetBytesPerRow(pb), 0, 0, 0 };
    sws_scale(b->sws, (const uint8_t *const *)f->data, f->linesize, 0, f->height, dst, stride);
    CVPixelBufferUnlockBaseAddress(pb, 0);
    CVBufferSetAttachment(pb, kCVImageBufferColorPrimariesKey, kCVImageBufferColorPrimaries_ITU_R_709_2, kCVAttachmentMode_ShouldPropagate);
    CVBufferSetAttachment(pb, kCVImageBufferTransferFunctionKey, kCVImageBufferTransferFunction_ITU_R_709_2, kCVAttachmentMode_ShouldPropagate);
    return pb;
}

void *pcvb_make_frame(PCVideoBackend *b, const AVFrame *f, int *hw) {
    CVPixelBufferRef pb;
    *hw = 0;
    if (f->format == AV_PIX_FMT_VIDEOTOOLBOX) {
        pb = (CVPixelBufferRef)f->data[3];
        CVPixelBufferRetain(pb);
        *hw = 1;
    } else {
        pb = sw_frame_to_pb(b, f);
    }
    if (pb) set_sar(pb, f->sample_aspect_ratio);
    return pb;
}

void pcvb_retain(void *frame) { CVPixelBufferRetain((CVPixelBufferRef)frame); }
void pcvb_release(void *frame) { CVPixelBufferRelease((CVPixelBufferRef)frame); }

#endif
