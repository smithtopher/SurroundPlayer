// Video back-end interface: hardware decode setup and turning decoded AVFrames into
// the native frame type the UI layer displays. One implementation per OS:
//   platform/apple/video_apple.c   VideoToolbox decode; frames are CVPixelBufferRef
//   platform/windows/video_d3d11.c D3D11VA decode; frames are D3D11 texture slices (pc_frame_info)
//   platform/stub/video_stub.c     software only; frames are refcounted AVFrames
//
// Native frames are opaque to the engine; it only retains/releases them and hands
// them to PCVideoCallback.
#ifndef PC_VIDEO_BACKEND_H
#define PC_VIDEO_BACKEND_H

#include <libavcodec/avcodec.h>

typedef struct PCVideoBackend PCVideoBackend;

PCVideoBackend *pcvb_create(void);
void pcvb_destroy(PCVideoBackend *b);

// Before avcodec_open2: enable hardware decoding on `dec` if available.
// Returns 1 if hardware decoding was set up.
int pcvb_prepare_decoder(PCVideoBackend *b, AVCodecContext *dec);

// Converts a decoded frame to a native frame with one reference owned by the caller
// (NULL on failure). *hw is set when the frame came straight from hardware decode.
void *pcvb_make_frame(PCVideoBackend *b, const AVFrame *f, int *hw);
void pcvb_retain(void *frame);
void pcvb_release(void *frame);

// Name of the hardware decoder, for display ("VideoToolbox", "D3D11VA").
const char *pcvb_hw_name(void);

#endif
