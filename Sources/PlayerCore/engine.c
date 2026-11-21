// Playback engine.
//
// Threads:
//   demux    av_read_frame -> audio / video packet queues, handles seeks
//   audio    decode -> swresample -> (PCM | AC3 encode) -> IEC 61937 pack -> output ring
//            or, for native AC3 tracks, packets -> IEC 61937 pack directly (passthrough)
//   video    decode (hardware via the video back-end) -> native frame queue
//   present  shows frames against the audio clock, detects end of stream
// CoreAudio's IOProc drains the ring (pc_output.c).
#include "playercore.h"
#include "pc_output.h"
#include "pc_platform.h"
#include "pc_video_backend.h"
#include "pc_subtitle.h"
#include "pc_upmix.h"

#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/audio_fifo.h>
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libavutil/pixdesc.h>
#include <libswresample/swresample.h>

#include <math.h>
#include <stdatomic.h>
#include <stdio.h>
#include <string.h>

// ---------------------------------------------------------------------------
// packet queue

typedef struct PktNode { AVPacket *pkt; int serial; struct PktNode *next; } PktNode; // pkt NULL = end of stream

typedef struct {
    PktNode *first, *last;
    int count;
    int64_t bytes, duration;
    _Atomic int serial;
    int abort;
    pc_mutex m;
    pc_cond c;
} PacketQueue;

static void pq_init(PacketQueue *q) {
    memset(q, 0, sizeof(*q));
    pc_mutex_init(&q->m);
    pc_cond_init(&q->c);
}

static void pq_put(PacketQueue *q, AVPacket *pkt) {
    PktNode *n = av_mallocz(sizeof(*n));
    if (pkt) { n->pkt = av_packet_alloc(); av_packet_move_ref(n->pkt, pkt); }
    pc_mutex_lock(&q->m);
    n->serial = q->serial;
    if (q->last) q->last->next = n; else q->first = n;
    q->last = n;
    q->count++;
    if (n->pkt) { q->bytes += n->pkt->size; q->duration += n->pkt->duration; }
    pc_cond_signal(&q->c);
    pc_mutex_unlock(&q->m);
}

static void pq_clear_locked(PacketQueue *q) {
    PktNode *n = q->first;
    while (n) { PktNode *nx = n->next; av_packet_free(&n->pkt); av_free(n); n = nx; }
    q->first = q->last = NULL;
    q->count = 0; q->bytes = 0; q->duration = 0;
}

static void pq_flush(PacketQueue *q) {
    pc_mutex_lock(&q->m);
    pq_clear_locked(q);
    atomic_fetch_add(&q->serial, 1);
    pc_cond_broadcast(&q->c);
    pc_mutex_unlock(&q->m);
}

static void pq_abort(PacketQueue *q) {
    pc_mutex_lock(&q->m);
    q->abort = 1;
    atomic_fetch_add(&q->serial, 1);   // also unblocks pc_out_write
    pc_cond_broadcast(&q->c);
    pc_mutex_unlock(&q->m);
}

static void pq_destroy(PacketQueue *q) {
    pc_mutex_lock(&q->m);
    pq_clear_locked(q);
    pc_mutex_unlock(&q->m);
    pc_mutex_destroy(&q->m);
    pc_cond_destroy(&q->c);
}

// 1 = packet, 0 = end-of-stream marker, -1 = aborted
static int pq_get(PacketQueue *q, AVPacket **pkt, int *serial) {
    pc_mutex_lock(&q->m);
    while (!q->first && !q->abort) pc_cond_wait(&q->c, &q->m);
    if (q->abort) { pc_mutex_unlock(&q->m); return -1; }
    PktNode *n = q->first;
    q->first = n->next;
    if (!q->first) q->last = NULL;
    q->count--;
    if (n->pkt) { q->bytes -= n->pkt->size; q->duration -= n->pkt->duration; }
    pc_mutex_unlock(&q->m);
    *pkt = n->pkt;
    *serial = n->serial;
    int r = n->pkt ? 1 : 0;
    av_free(n);
    return r;
}

// ---------------------------------------------------------------------------
// video frame queue

#define FQ_SIZE 6

typedef struct { void *pb; double pts, dur; int serial; uint64_t id; } VFrame;   // pb: native frame (video back-end)

typedef struct {
    VFrame q[FQ_SIZE];
    int rindex, count, serial, abort;
    uint64_t next_id;
    pc_mutex m;
    pc_cond c;
} FrameQueue;

static void fq_init(FrameQueue *f) {
    memset(f, 0, sizeof(*f));
    pc_mutex_init(&f->m);
    pc_cond_init(&f->c);
}

static void fq_clear_locked(FrameQueue *f) {
    for (int i = 0; i < f->count; i++) pcvb_release(f->q[(f->rindex + i) % FQ_SIZE].pb);
    f->count = 0;
    f->rindex = 0;
}

static void fq_flush(FrameQueue *f, int serial) {
    pc_mutex_lock(&f->m);
    fq_clear_locked(f);
    f->serial = serial;
    pc_cond_broadcast(&f->c);
    pc_mutex_unlock(&f->m);
}

static void fq_abort(FrameQueue *f) {
    pc_mutex_lock(&f->m);
    f->abort = 1;
    pc_cond_broadcast(&f->c);
    pc_mutex_unlock(&f->m);
}

static void fq_destroy(FrameQueue *f) {
    fq_clear_locked(f);
    pc_mutex_destroy(&f->m);
    pc_cond_destroy(&f->c);
}

// Takes ownership of v.pb.
static void fq_push(FrameQueue *f, VFrame v) {
    pc_mutex_lock(&f->m);
    while (f->count == FQ_SIZE && !f->abort && v.serial == f->serial) pc_cond_wait(&f->c, &f->m);
    if (f->abort || v.serial != f->serial) {
        pc_mutex_unlock(&f->m);
        pcvb_release(v.pb);
        return;
    }
    v.id = ++f->next_id;
    f->q[(f->rindex + f->count) % FQ_SIZE] = v;
    f->count++;
    pc_mutex_unlock(&f->m);
}

// Copies entry idx with an extra retain on pb (caller releases). Drops stale entries.
static int fq_peek(FrameQueue *f, int idx, VFrame *out) {
    pc_mutex_lock(&f->m);
    while (f->count > 0 && f->q[f->rindex].serial != f->serial) {
        pcvb_release(f->q[f->rindex].pb);
        f->rindex = (f->rindex + 1) % FQ_SIZE;
        f->count--;
        pc_cond_broadcast(&f->c);
    }
    int ok = idx < f->count;
    if (ok) { *out = f->q[(f->rindex + idx) % FQ_SIZE]; pcvb_retain(out->pb); }
    pc_mutex_unlock(&f->m);
    return ok;
}

static void fq_pop_id(FrameQueue *f, uint64_t id) {
    pc_mutex_lock(&f->m);
    if (f->count > 0 && f->q[f->rindex].id == id) {
        pcvb_release(f->q[f->rindex].pb);
        f->rindex = (f->rindex + 1) % FQ_SIZE;
        f->count--;
        pc_cond_broadcast(&f->c);
    }
    pc_mutex_unlock(&f->m);
}

static int fq_count(FrameQueue *f) {
    pc_mutex_lock(&f->m);
    int n = f->count;
    pc_mutex_unlock(&f->m);
    return n;
}

// ---------------------------------------------------------------------------
// player

#define MAX_AUDIO_TRACKS 64
#define MAX_SUB_TRACKS 64
#define TAP_FRAMES 131072            // ~2.7 s at 48 kHz, power of two
#define TAP_MARKERS 128
#define TAP_RATE 48000

struct PCPlayer {
    PCVideoCallback vcb;
    PCEventCallback ecb;
    void *cbctx;

    int is_open;
    AVFormatContext *fmt;
    double start_time;
    int astream, vstream;
    int audio_tracks[MAX_AUDIO_TRACKS], n_audio, cur_track;
    // subtitles: tracks are streams (stream >= 0, decoded by the demux thread as they are read,
    // whether selected or not, so switching is instant) or added files (path, loaded whole)
    struct { int stream, flags; char *path; char name[200]; char lang[16]; AVCodecContext *dec; PCSubs *store; }
        subt[MAX_SUB_TRACKS];
    _Atomic int n_sub, cur_sub;      // cur_sub: selected track, -1 off
    int sub_last_track, sub_last_serial, sub_serial;   // pc_player_subtitle_state's view (caller's thread)
    AVCodecContext *adec, *vdec;
    PCVideoBackend *vb;
    PCOut *out;
    PCOut *kept_out;                 // output kept open between files (receiver stays locked)
    char kept_uid[256];
    PCStreamType kept_type, out_type;
    int kept_burst;
    enum AVCodecID bs_codec;         // codec being bitstreamed (AC3 / DTS)
    int burst_bytes;                 // its IEC 61937 burst size
    int enc_frame;                   // samples per encoded frame
    int mode;
    PCConfig cfg;

    // audio processing (audio thread only after open)
    SwrContext *swr;
    AVChannelLayout swr_in_layout;
    int swr_in_rate, swr_in_fmt;
    AVChannelLayout out_layout;
    int out_rate;
    enum AVSampleFormat out_fmt;
    uint8_t **cvt;
    int cvt_samples;
    AVCodecContext *aenc;
    AVAudioFifo *fifo;
    AVFrame *eframe;
    AVPacket *epkt;
    int64_t enc_counter;
    AVFormatContext *spdif;
    int64_t spdif_ts;
    uint8_t *burst;
    int burst_len, burst_cap;
    double next_pts;

    // stereo upmix. Settings: any thread, under upmix_lock (+ upmix_gen). Graph: audio thread.
    pc_mutex upmix_lock;
    PCUpmix upmix;
    _Atomic int upmix_gen;
    int upmixing;                    // decided at open: this file's stereo goes through the graph
    PCUpmixer *um;
    int um_gen;
    double um_t0;                    // pts of the first sample fed to the current graph
    int64_t um_in, um_out;           // samples fed / taken out since um_t0
    AVFrame *um_frame;

    // gapless (pc_player_set_next). The timeline runs on across files: a file's pts are its own
    // (pts - start) plus the offset of the point where it joined.
    char *next_path;                 // under lock: the file to continue into (NULL none)
    double dmx_offset;               // under lock: timeline offset of the file being demuxed
    double item_offset;              // under lock: timeline offset of the file being heard
    AVRational a_tb;                 // audio thread: the decoded file's time base, start, offset
    double a_start, a_offset;
    AVCodecContext *sw_dec;          // demux -> audio thread with the switch marker (under lock)
    AVRational sw_tb;
    double sw_start, sw_offset;
    _Atomic int pending;             // switch prepared (demuxing the next file), join not yet heard
    double pend_offset;              // under lock: timeline position of the join (NAN until known)
    PCInfo pend_info;                // the next file's info, swapped in when the join is heard
    uint8_t *pend_art;
    int pend_art_size;
    AVFormatContext *old_fmt;        // the previous file's context, closed at the next switch/close
    int mc_req_ch, mc_req_rate;      // exclusive PCM request this file made (gapless must match)
    int plan_mode, plan_up, plan_ch; // the route this file asked for (before any device fallback)
    uint32_t mc_req_mask;

    // threads
    pc_thread th_demux, th_audio, th_video, th_present;
    int have_audio_thread, have_video_thread, threads_running;
    _Atomic int quit, paused;
    PacketQueue aq, vq;
    FrameQueue fq;
    pc_mutex lock;            // seek request, ext clock, position
    int seek_req;
    double seek_pos;
    _Atomic double seek_target;      // drop decoded data before this pts after a seek
    _Atomic int demux_eof, audio_eof, video_eof, eof_sent, force_show;
    _Atomic double av_offset;

    double ext_base_pts, ext_base_time, ext_paused_pts;
    int ext_started;
    _Atomic double last_video_pts;
    double last_pos;

    int video_hw;

    PCInfo info;

    // visualiser tap: stereo float @ 48 kHz, timestamped, independent of the output path
    _Atomic int vis_enabled;
    pc_mutex tap_lock;
    float *tap;                      // TAP_FRAMES * 2, interleaved L/R
    int64_t tap_w;                   // total frames written
    int64_t tap_mpos[TAP_MARKERS];
    double tap_mpts[TAP_MARKERS];
    int tap_mhead, tap_mcount;
    SwrContext *tap_swr;             // audio thread only
    AVChannelLayout tap_in_layout;
    int tap_in_rate, tap_in_fmt;
    float *tap_tmp;
    int tap_tmp_frames;
    uint8_t *art;
    int art_size;
};

static double now_sec(void) { return (double)pc_now_ns() * 1e-9; }

static void copy_str(char *dst, int len, const char *src) {
    snprintf(dst, len, "%s", src ? src : "");
}

PCPlayer *pc_player_create(void) {
    av_log_set_level(AV_LOG_ERROR);
    PCPlayer *p = av_mallocz(sizeof(*p));
    pc_mutex_init(&p->lock);
    pc_mutex_init(&p->tap_lock);
    pc_mutex_init(&p->upmix_lock);
    p->upmix = (PCUpmix){ .enabled = 0, .include_video = 0, .surround_db = -3, .center = 0.5f, .width = 0.5f,
                          .lfe_db = -6, .crossover_hz = 100, .lfe_move = 0, .bass_db = 0,
                          .rear_delay_ms = 10, .rear_highcut_hz = 7000 };
    p->tap = av_calloc(TAP_FRAMES * 2, sizeof(float));
    p->astream = p->vstream = -1;
    p->cur_sub = p->sub_last_track = -1;
    return p;
}

void pc_player_set_callbacks(PCPlayer *p, PCVideoCallback vcb, PCEventCallback ecb, void *ctx) {
    p->vcb = vcb; p->ecb = ecb; p->cbctx = ctx;
}

void pc_player_destroy(PCPlayer *p) {
    if (!p) return;
    pc_player_close(p);
    pc_mutex_destroy(&p->lock);
    pc_mutex_destroy(&p->tap_lock);
    pc_mutex_destroy(&p->upmix_lock);
    av_free(p->tap);
    av_free(p);
}

// ---------------------------------------------------------------------------
// clocks

static double ext_clock_locked(PCPlayer *p) {
    if (!p->ext_started) return NAN;
    if (p->paused) return p->ext_paused_pts;
    return p->ext_base_pts + (now_sec() - p->ext_base_time);
}

static void ext_start(PCPlayer *p, double pts) {
    pc_mutex_lock(&p->lock);
    p->ext_base_pts = pts;
    p->ext_base_time = now_sec();
    p->ext_paused_pts = pts;
    p->ext_started = 1;
    pc_mutex_unlock(&p->lock);
}

static int audio_is_master(PCPlayer *p) {
    return p->astream >= 0 && p->out && !atomic_load(&p->audio_eof);
}

// Clock video frames are scheduled against.
static double video_clock(PCPlayer *p) {
    if (audio_is_master(p)) {
        double c = pc_out_clock(p->out);
        return isnan(c) ? NAN : c - atomic_load(&p->av_offset);
    }
    pc_mutex_lock(&p->lock);
    double c = ext_clock_locked(p);
    pc_mutex_unlock(&p->lock);
    return c;
}

// ---------------------------------------------------------------------------
// IEC 61937 packing via libavformat's spdif muxer

static int spdif_write_cb(void *opaque, const uint8_t *buf, int size) {
    PCPlayer *p = opaque;
    if (p->burst_len + size > p->burst_cap) {
        int cap = (p->burst_len + size) * 2;
        uint8_t *nb = av_realloc(p->burst, cap);
        if (!nb) return AVERROR(ENOMEM);
        p->burst = nb;
        p->burst_cap = cap;
    }
    memcpy(p->burst + p->burst_len, buf, size);
    p->burst_len += size;
    return size;
}

static int spdif_open(PCPlayer *p, enum AVCodecID codec, const AVChannelLayout *layout) {
    if (avformat_alloc_output_context2(&p->spdif, NULL, "spdif", NULL) < 0) return -1;
    AVStream *st = avformat_new_stream(p->spdif, NULL);
    st->codecpar->codec_type = AVMEDIA_TYPE_AUDIO;
    st->codecpar->codec_id = codec;
    st->codecpar->sample_rate = 48000;
    av_channel_layout_copy(&st->codecpar->ch_layout, layout);
    st->time_base = (AVRational){ 1, 48000 };
    int iosize = 32768;
    uint8_t *iobuf = av_malloc(iosize);
    p->spdif->pb = avio_alloc_context(iobuf, iosize, 1, p, NULL, spdif_write_cb, NULL);
    p->spdif->flags |= AVFMT_FLAG_CUSTOM_IO;
    if (avformat_write_header(p->spdif, NULL) < 0) return -1;
    avio_flush(p->spdif->pb);
    p->burst_len = 0;
    return 0;
}

static void spdif_close(PCPlayer *p) {
    if (!p->spdif) return;
    if (p->spdif->pb) {
        av_freep(&p->spdif->pb->buffer);
        avio_context_free(&p->spdif->pb);
    }
    avformat_free_context(p->spdif);
    p->spdif = NULL;
}

// Wraps one AC3/DTS frame into an IEC 61937 burst in p->burst. Returns its length.
static int spdif_pack(PCPlayer *p, AVPacket *pkt) {
    p->burst_len = 0;
    pkt->stream_index = 0;
    pkt->pts = pkt->dts = p->spdif_ts;
    pkt->duration = p->enc_frame;
    p->spdif_ts += p->enc_frame;
    if (av_write_frame(p->spdif, pkt) < 0) return 0;
    avio_flush(p->spdif->pb);
    // keep the ring burst-aligned no matter what the muxer produced
    int rem = p->burst_len % p->burst_bytes;
    if (rem) {
        int pad = p->burst_bytes - rem;
        uint8_t zeros[PC_MAX_BURST_BYTES] = { 0 };
        spdif_write_cb(p, zeros, pad);
    }
    return p->burst_len;
}

static int spdif_send(PCPlayer *p, AVPacket *pkt, double pts, int serial) {
    int n = spdif_pack(p, pkt);
    if (n <= 0) return 0;
    return pc_out_write(p->out, p->burst, n, pts, &p->aq.serial, serial);
}

// ---------------------------------------------------------------------------
// AC3 / DTS encoders

static AVCodecContext *open_bs_encoder(enum AVCodecID id, const AVChannelLayout *layout, int64_t bit_rate) {
    const AVCodec *c = id == AV_CODEC_ID_AC3 ? avcodec_find_encoder_by_name("ac3") : avcodec_find_encoder_by_name("dca");
    if (!c) c = avcodec_find_encoder(id);
    if (!c) return NULL;
    AVCodecContext *e = avcodec_alloc_context3(c);
    e->strict_std_compliance = FF_COMPLIANCE_EXPERIMENTAL;   // FFmpeg's DTS encoder is flagged experimental
    e->sample_fmt = AV_SAMPLE_FMT_FLTP;
    const enum AVSampleFormat *fmts = NULL;
    int nf = 0;
    if (avcodec_get_supported_config(NULL, c, AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, (const void **)&fmts, &nf) >= 0 && fmts && nf > 0) {
        int ok = 0;
        for (int i = 0; i < nf; i++) if (fmts[i] == AV_SAMPLE_FMT_FLTP) ok = 1;
        if (!ok) e->sample_fmt = fmts[0];
    }
    e->sample_rate = 48000;
    av_channel_layout_copy(&e->ch_layout, layout);
    e->bit_rate = bit_rate;
    e->time_base = (AVRational){ 1, 48000 };
    if (avcodec_open2(e, c, NULL) < 0) { avcodec_free_context(&e); return NULL; }
    return e;
}

static AVFrame *alloc_enc_frame(AVCodecContext *e) {
    AVFrame *f = av_frame_alloc();
    f->nb_samples = e->frame_size;
    f->format = e->sample_fmt;
    f->sample_rate = 48000;
    av_channel_layout_copy(&f->ch_layout, &e->ch_layout);
    av_frame_get_buffer(f, 0);
    return f;
}

// Encode one frame of digital silence and hand its IEC 61937 burst to the output,
// which plays it whenever there is no real data (pause, seek, underrun).
static void make_silence_burst(PCPlayer *p, const AVChannelLayout *layout, int64_t bit_rate) {
    AVCodecContext *e = open_bs_encoder(p->bs_codec, layout, bit_rate);
    if (!e) {   // e.g. a passthrough layout the encoder doesn't do
        AVChannelLayout l51 = AV_CHANNEL_LAYOUT_5POINT1;
        e = open_bs_encoder(p->bs_codec, &l51, bit_rate);
    }
    if (!e) return;
    AVFrame *f = alloc_enc_frame(e);
    av_samples_set_silence(f->extended_data, 0, f->nb_samples, f->ch_layout.nb_channels, e->sample_fmt);
    f->pts = 0;
    AVPacket *pkt = av_packet_alloc();
    if (avcodec_send_frame(e, f) >= 0 && avcodec_receive_packet(e, pkt) >= 0) {
        int n = spdif_pack(p, pkt);
        if (n == p->burst_bytes) pc_out_set_silence_burst(p->out, p->burst, n);
    }
    av_packet_free(&pkt);
    av_frame_free(&f);
    avcodec_free_context(&e);
}

// ---------------------------------------------------------------------------
// audio thread

// ---------------------------------------------------------------------------
// visualiser tap

static void tap_reset(PCPlayer *p) {
    swr_free(&p->tap_swr);
    pc_mutex_lock(&p->tap_lock);
    p->tap_mcount = 0;
    pc_mutex_unlock(&p->tap_lock);
}

static void tap_feed(PCPlayer *p, const AVFrame *f, double pts) {
    if (!atomic_load_explicit(&p->vis_enabled, memory_order_relaxed)) return;
    AVChannelLayout in = { 0 };
    if (f->ch_layout.order == AV_CHANNEL_ORDER_UNSPEC || f->ch_layout.nb_channels == 0)
        av_channel_layout_default(&in, f->ch_layout.nb_channels > 0 ? f->ch_layout.nb_channels : 2);
    else
        av_channel_layout_copy(&in, &f->ch_layout);
    if (!p->tap_swr || p->tap_in_rate != f->sample_rate || p->tap_in_fmt != f->format ||
        av_channel_layout_compare(&in, &p->tap_in_layout)) {
        swr_free(&p->tap_swr);
        AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO;
        if (swr_alloc_set_opts2(&p->tap_swr, &stereo, AV_SAMPLE_FMT_FLT, TAP_RATE, &in, f->format, f->sample_rate, 0, NULL) < 0 ||
            swr_init(p->tap_swr) < 0) {
            swr_free(&p->tap_swr);
            av_channel_layout_uninit(&in);
            return;
        }
        av_channel_layout_uninit(&p->tap_in_layout);
        av_channel_layout_copy(&p->tap_in_layout, &in);
        p->tap_in_rate = f->sample_rate;
        p->tap_in_fmt = f->format;
    }
    av_channel_layout_uninit(&in);
    int max_out = swr_get_out_samples(p->tap_swr, f->nb_samples);
    if (max_out <= 0) return;
    if (max_out > p->tap_tmp_frames) {
        av_free(p->tap_tmp);
        p->tap_tmp = av_malloc((size_t)max_out * 2 * sizeof(float));
        p->tap_tmp_frames = p->tap_tmp ? max_out : 0;
        if (!p->tap_tmp) return;
    }
    double first_pts = isnan(pts) ? NAN : pts - (double)swr_get_delay(p->tap_swr, TAP_RATE) / TAP_RATE;
    uint8_t *out[1] = { (uint8_t *)p->tap_tmp };
    int n = swr_convert(p->tap_swr, out, max_out, (const uint8_t **)f->extended_data, f->nb_samples);
    if (n <= 0) return;
    pc_mutex_lock(&p->tap_lock);
    if (!isnan(first_pts)) {
        int i = (p->tap_mhead + p->tap_mcount) % TAP_MARKERS;
        if (p->tap_mcount == TAP_MARKERS) { p->tap_mhead = (p->tap_mhead + 1) % TAP_MARKERS; p->tap_mcount--; }
        p->tap_mpos[i] = p->tap_w;
        p->tap_mpts[i] = first_pts;
        p->tap_mcount++;
    }
    for (int k = 0; k < n; k++) {
        int64_t idx = ((p->tap_w + k) & (TAP_FRAMES - 1)) * 2;
        p->tap[idx] = p->tap_tmp[k * 2];
        p->tap[idx + 1] = p->tap_tmp[k * 2 + 1];
    }
    p->tap_w += n;
    pc_mutex_unlock(&p->tap_lock);
}

void pc_player_set_vis_enabled(PCPlayer *p, int enabled) { atomic_store(&p->vis_enabled, enabled ? 1 : 0); }

int pc_player_vis_samples(PCPlayer *p, float *lr, int frames) {
    if (!p->is_open || p->astream < 0 || !p->out || frames <= 0 || frames > TAP_FRAMES / 2) return 0;
    double clock = pc_out_clock(p->out);
    if (isnan(clock)) return 0;
    clock -= atomic_load(&p->av_offset);   // match the picture: what the amp is playing now
    int got = 0;
    pc_mutex_lock(&p->tap_lock);
    int best = -1;
    for (int i = p->tap_mcount - 1; i >= 0; i--) {
        int k = (p->tap_mhead + i) % TAP_MARKERS;
        if (p->tap_mpts[k] <= clock) { best = k; break; }
    }
    if (best >= 0) {
        int64_t end = p->tap_mpos[best] + (int64_t)((clock - p->tap_mpts[best]) * TAP_RATE);
        if (end > p->tap_w) end = p->tap_w;
        int64_t start = end - frames;
        if (start >= 0 && start >= p->tap_w - TAP_FRAMES) {
            for (int k = 0; k < frames; k++) {
                int64_t idx = ((start + k) & (TAP_FRAMES - 1)) * 2;
                lr[k * 2] = p->tap[idx];
                lr[k * 2 + 1] = p->tap[idx + 1];
            }
            got = frames;
        }
    }
    pc_mutex_unlock(&p->tap_lock);
    return got;
}

static void audio_switch_now(PCPlayer *p, double offset);

static void audio_reset(PCPlayer *p) {
    // a seek flushed a switch marker before it was reached: take the next file's decoder now
    pc_mutex_lock(&p->lock);
    int sw = p->sw_dec != NULL;
    double swoff = p->sw_offset;
    pc_mutex_unlock(&p->lock);
    if (sw) audio_switch_now(p, swoff);
    tap_reset(p);
    pc_upmix_free(&p->um);      // rebuilt at the next frame (seek: a fresh timeline)
    if (p->adec) avcodec_flush_buffers(p->adec);
    swr_free(&p->swr);
    if (p->fifo) av_audio_fifo_reset(p->fifo);
    p->next_pts = NAN;
    pc_out_flush(p->out);
    atomic_store(&p->audio_eof, 0);
}

static int ensure_swr(PCPlayer *p, const AVFrame *f) {
    AVChannelLayout in = { 0 };
    if (f->ch_layout.order == AV_CHANNEL_ORDER_UNSPEC || f->ch_layout.nb_channels == 0)
        av_channel_layout_default(&in, f->ch_layout.nb_channels > 0 ? f->ch_layout.nb_channels : 2);
    else
        av_channel_layout_copy(&in, &f->ch_layout);
    if (p->swr && p->swr_in_rate == f->sample_rate && p->swr_in_fmt == f->format &&
        !av_channel_layout_compare(&in, &p->swr_in_layout)) {
        av_channel_layout_uninit(&in);
        return 0;
    }
    swr_free(&p->swr);
    // upmixing: swr makes stereo planar float for the upmix graph, which makes the output format
    AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO;
    if (swr_alloc_set_opts2(&p->swr, p->upmixing ? &stereo : &p->out_layout,
                            p->upmixing ? AV_SAMPLE_FMT_FLTP : p->out_fmt, p->out_rate,
                            &in, f->format, f->sample_rate, 0, NULL) < 0 || swr_init(p->swr) < 0) {
        swr_free(&p->swr);
        av_channel_layout_uninit(&in);
        return -1;
    }
    av_channel_layout_uninit(&p->swr_in_layout);
    p->swr_in_layout = in;
    p->swr_in_rate = f->sample_rate;
    p->swr_in_fmt = f->format;
    return 0;
}

static int ensure_cvt(PCPlayer *p, int samples) {
    if (samples <= p->cvt_samples) return 0;
    if (p->cvt) { av_freep(&p->cvt[0]); av_freep(&p->cvt); }
    p->cvt_samples = 0;
    if (av_samples_alloc_array_and_samples(&p->cvt, NULL, p->upmixing ? 2 : p->out_layout.nb_channels, samples,
                                           p->upmixing ? AV_SAMPLE_FMT_FLTP : p->out_fmt, 0) < 0)
        return -1;
    p->cvt_samples = samples;
    return 0;
}

static int encode_and_send(PCPlayer *p, AVFrame *ef, double pts, int serial) {
    if (avcodec_send_frame(p->aenc, ef) < 0) return 0;
    while (avcodec_receive_packet(p->aenc, p->epkt) >= 0) {
        int r = spdif_send(p, p->epkt, pts, serial);
        av_packet_unref(p->epkt);
        if (r < 0) return -1;
    }
    return 0;
}

// Feed converted samples to the output path. Returns -1 if interrupted by a seek/close.
static int deliver(PCPlayer *p, uint8_t **data, int n, int serial) {
    if (p->mode == PC_MODE_PCM) {
        int bpf = p->out_layout.nb_channels * 4;
        int off = 0;
        while (off < n) {
            int chunk = n - off > 4096 ? 4096 : n - off;
            double pts = p->next_pts;
            p->next_pts += (double)chunk / p->out_rate;
            if (pc_out_write(p->out, data[0] + (size_t)off * bpf, chunk * bpf, pts, &p->aq.serial, serial) < 0)
                return -1;
            off += chunk;
        }
        return 0;
    }
    double head = p->next_pts - (double)av_audio_fifo_size(p->fifo) / p->out_rate;
    av_audio_fifo_write(p->fifo, (void **)data, n);
    p->next_pts += (double)n / p->out_rate;
    while (av_audio_fifo_size(p->fifo) >= p->enc_frame) {
        av_frame_make_writable(p->eframe);
        av_audio_fifo_read(p->fifo, (void **)p->eframe->data, p->enc_frame);
        p->eframe->pts = p->enc_counter;
        p->enc_counter += p->enc_frame;
        if (encode_and_send(p, p->eframe, head, serial) < 0) return -1;
        head += (double)p->enc_frame / p->out_rate;
    }
    return 0;
}

// ---------------------------------------------------------------------------
// stereo upmix: stereo fltp from swr -> graph -> deliver(), restamped by the graph latency

static PCUpmix upmix_settings(PCPlayer *p) {
    pc_mutex_lock(&p->upmix_lock);
    PCUpmix u = p->upmix;
    pc_mutex_unlock(&p->upmix_lock);
    return u;
}

static int upmix_pull(PCPlayer *p, int serial) {
    while (p->um && pc_upmix_receive(p->um, p->um_frame) >= 0) {
        int n = p->um_frame->nb_samples;
        // output sample k is input sample k - latency
        p->next_pts = p->um_t0 + (double)(p->um_out - pc_upmix_latency(p->um)) / p->out_rate;
        p->um_out += n;
        int r = deliver(p, p->um_frame->extended_data, n, serial);
        av_frame_unref(p->um_frame);
        if (r < 0) return -1;
    }
    return 0;
}

// `first_pts`: pts of data's first sample (NAN = continues the previous chunk).
static int upmix_feed(PCPlayer *p, uint8_t **data, int n, double first_pts, int serial) {
    double expected = p->um ? p->um_t0 + (double)p->um_in / p->out_rate : NAN;
    int gen = atomic_load(&p->upmix_gen);
    if (p->um && gen != p->um_gen) {
        PCUpmix u = upmix_settings(p);
        if (pc_upmix_update(p->um, &u) != 0) pc_upmix_free(&p->um);   // crossover: rebuild, same timeline
        p->um_gen = gen;
    }
    if (p->um && !isnan(first_pts) && fabs(expected - first_pts) > 0.3) pc_upmix_free(&p->um);   // discontinuity
    if (!p->um) {
        PCUpmix u = upmix_settings(p);
        char e[256] = "";
        p->um = pc_upmix_create(&u, p->out_rate, &p->out_layout, p->out_fmt, e, sizeof(e));
        if (!p->um) return 0;   // checked at open, so only on allocation failure
        p->um_gen = gen;
        // a rebuilt graph's priming silence covers exactly what the old one still held
        p->um_t0 = !isnan(first_pts) ? first_pts : !isnan(expected) ? expected : isnan(p->next_pts) ? 0 : p->next_pts;
        p->um_in = p->um_out = 0;
    }
    if (pc_upmix_send(p->um, (const float *const *)data, n) < 0) return 0;
    p->um_in += n;
    return upmix_pull(p, serial);
}

// End of stream: push the graph's last window out with silence.
static int upmix_flush(PCPlayer *p, int serial) {
    if (!p->um) return 0;
    int n = pc_upmix_flush_samples(p->um);
    uint8_t **sil = NULL;
    if (av_samples_alloc_array_and_samples(&sil, NULL, 2, n, AV_SAMPLE_FMT_FLTP, 0) < 0) return 0;
    av_samples_set_silence(sil, 0, n, 2, AV_SAMPLE_FMT_FLTP);
    int r = upmix_feed(p, sil, n, NAN, serial);
    av_freep(&sil[0]);
    av_freep(&sil);
    return r;
}

static int audio_frame(PCPlayer *p, AVFrame *f, int serial) {
    double pts = f->best_effort_timestamp != AV_NOPTS_VALUE
                     ? f->best_effort_timestamp * av_q2d(p->a_tb) - p->a_start + p->a_offset : NAN;
    double dur = f->sample_rate > 0 ? (double)f->nb_samples / f->sample_rate : 0;
    double target = atomic_load(&p->seek_target);
    if (!isnan(target) && !isnan(pts) && pts + dur < target) return 0;
    tap_feed(p, f, pts);
    if (ensure_swr(p, f) < 0) return 0;
    int64_t delay = swr_get_delay(p->swr, p->out_rate);
    if (p->upmixing) {
        int max_up = swr_get_out_samples(p->swr, f->nb_samples);
        if (max_up <= 0 || ensure_cvt(p, max_up) < 0) return 0;
        int nu = swr_convert(p->swr, p->cvt, max_up, (const uint8_t **)f->extended_data, f->nb_samples);
        if (nu <= 0) return 0;
        return upmix_feed(p, p->cvt, nu, isnan(pts) ? NAN : pts - (double)delay / p->out_rate, serial);
    }
    if (!isnan(pts)) {
        double expected = p->next_pts + (double)delay / p->out_rate;
        if (isnan(p->next_pts) || fabs(expected - pts) > 0.3) p->next_pts = pts - (double)delay / p->out_rate;
    } else if (isnan(p->next_pts)) {
        p->next_pts = 0;
    }
    int max_out = swr_get_out_samples(p->swr, f->nb_samples);
    if (max_out <= 0 || ensure_cvt(p, max_out) < 0) return 0;
    int n = swr_convert(p->swr, p->cvt, max_out, (const uint8_t **)f->extended_data, f->nb_samples);
    if (n <= 0) return 0;
    return deliver(p, p->cvt, n, serial);
}

static void audio_drain(PCPlayer *p, AVFrame *frame, int serial) {
    if (p->mode != PC_MODE_PASSTHROUGH) {
        avcodec_send_packet(p->adec, NULL);
        while (avcodec_receive_frame(p->adec, frame) >= 0) {
            int r = audio_frame(p, frame, serial);
            av_frame_unref(frame);
            if (r < 0) return;
        }
        if (p->swr) {
            int max_out = swr_get_out_samples(p->swr, 0);
            if (max_out > 0 && ensure_cvt(p, max_out) == 0) {
                int n = swr_convert(p->swr, p->cvt, max_out, NULL, 0);
                if (n > 0 && (p->upmixing ? upmix_feed(p, p->cvt, n, NAN, serial) : deliver(p, p->cvt, n, serial)) < 0)
                    return;
            }
        }
        if (p->upmixing && upmix_flush(p, serial) < 0) return;
        if (p->mode == PC_MODE_TRANSCODE) {
            int left = av_audio_fifo_size(p->fifo);
            if (left > 0) {
                // pad the final partial AC3 frame with silence
                int pad = p->enc_frame - left;
                uint8_t **sil = NULL;
                if (av_samples_alloc_array_and_samples(&sil, NULL, p->out_layout.nb_channels, pad, p->out_fmt, 0) >= 0) {
                    av_samples_set_silence(sil, 0, pad, p->out_layout.nb_channels, p->out_fmt);
                    deliver(p, sil, pad, serial);
                    av_freep(&sil[0]);
                    av_freep(&sil);
                }
            }
        }
    }
    // wait for the ring to play out, then report audio end
    while (!atomic_load(&p->quit) && atomic_load(&p->aq.serial) == serial &&
           pc_out_buffered_seconds(p->out) > 0.005)
        pc_sleep_us(10000);
    pc_sleep_us((int64_t)(pc_out_latency(p->out) * 1e6));
    if (atomic_load(&p->aq.serial) == serial) atomic_store(&p->audio_eof, 1);
}

// Gapless: take the next file's decoder; its pts continue from `offset` on the timeline.
static void audio_switch_now(PCPlayer *p, double offset) {
    pc_mutex_lock(&p->lock);
    AVCodecContext *nd = p->sw_dec;
    p->sw_dec = NULL;
    AVRational tb = p->sw_tb;
    double start = p->sw_start;
    pc_mutex_unlock(&p->lock);
    if (!nd) return;
    avcodec_free_context(&p->adec);
    p->adec = nd;
    p->a_tb = tb;
    p->a_start = start;
    p->a_offset = offset;
}

// The switch marker: the previous file's last frames out of its decoder, then the next file's
// decoder in its place. swr, the upmix graph and the encoder keep running, so there is no gap.
static void audio_switch(PCPlayer *p, AVFrame *frame, int serial) {
    if (p->mode != PC_MODE_PASSTHROUGH && p->adec) {
        avcodec_send_packet(p->adec, NULL);
        while (avcodec_receive_frame(p->adec, frame) >= 0) {
            int r = audio_frame(p, frame, serial);
            av_frame_unref(frame);
            if (r < 0) return;
        }
    }
    // where the next file's first sample lands on the timeline
    double join;
    if (p->upmixing && p->um) join = p->um_t0 + (double)p->um_in / p->out_rate;
    else if (!isnan(p->next_pts)) join = p->next_pts + (p->swr ? (double)swr_get_delay(p->swr, p->out_rate) / p->out_rate : 0);
    else {
        pc_mutex_lock(&p->lock);
        join = p->sw_offset;
        pc_mutex_unlock(&p->lock);
    }
    audio_switch_now(p, join);
    pc_mutex_lock(&p->lock);
    p->dmx_offset = join;
    if (atomic_load(&p->pending) && isnan(p->pend_offset)) p->pend_offset = join;
    pc_mutex_unlock(&p->lock);
}

#define PC_SWITCH_MARK (-100)   // stream_index of the switch marker in the audio queue

static void *audio_thread(void *arg) {
    PCPlayer *p = arg;
    AVFrame *frame = av_frame_alloc();
    int serial = -1;
    while (!atomic_load(&p->quit)) {
        AVPacket *pkt = NULL;
        int pserial = 0;
        int r = pq_get(&p->aq, &pkt, &pserial);
        if (r < 0) break;
        if (pserial != serial) { audio_reset(p); serial = pserial; }
        if (r == 0) { audio_drain(p, frame, serial); continue; }
        if (pserial != atomic_load(&p->aq.serial)) { av_packet_free(&pkt); continue; }
        if (pkt->stream_index == PC_SWITCH_MARK) { audio_switch(p, frame, serial); av_packet_free(&pkt); continue; }

        if (p->mode == PC_MODE_PASSTHROUGH) {
            double pts = pkt->pts != AV_NOPTS_VALUE ? pkt->pts * av_q2d(p->a_tb) - p->a_start + p->a_offset : NAN;
            double target = atomic_load(&p->seek_target);
            if (isnan(target) || isnan(pts) || pts + 0.032 >= target) {
                // passthrough sends the packets untouched; decode a copy only to feed the visualiser
                if (p->adec && atomic_load(&p->vis_enabled) && avcodec_send_packet(p->adec, pkt) >= 0) {
                    while (avcodec_receive_frame(p->adec, frame) >= 0) {
                        double fpts = frame->best_effort_timestamp != AV_NOPTS_VALUE
                                          ? frame->best_effort_timestamp * av_q2d(p->a_tb) - p->a_start + p->a_offset : NAN;
                        tap_feed(p, frame, fpts);
                        av_frame_unref(frame);
                    }
                }
                spdif_send(p, pkt, pts, serial);
            }
        } else {
            int sr = avcodec_send_packet(p->adec, pkt);
            while (1) {
                int rr = avcodec_receive_frame(p->adec, frame);
                if (rr < 0) break;
                int ar = audio_frame(p, frame, serial);
                av_frame_unref(frame);
                if (ar < 0) break;
            }
            if (sr == AVERROR(EAGAIN)) avcodec_send_packet(p->adec, pkt);
        }
        av_packet_free(&pkt);
    }
    av_frame_free(&frame);
    return NULL;
}

// ---------------------------------------------------------------------------
// video thread

static void video_frame(PCPlayer *p, AVFrame *f, int serial) {
    AVStream *st = p->fmt->streams[p->vstream];
    double dur = f->duration > 0 ? f->duration * av_q2d(st->time_base)
                                 : (st->avg_frame_rate.num ? av_q2d(av_inv_q(st->avg_frame_rate)) : 0.04);
    double pts = f->best_effort_timestamp != AV_NOPTS_VALUE
                     ? f->best_effort_timestamp * av_q2d(st->time_base) - p->start_time
                     : atomic_load(&p->last_video_pts) + dur;
    double target = atomic_load(&p->seek_target);
    if (!isnan(target) && pts + dur * 0.5 < target) return;
    int hw = 0;
    void *pb = pcvb_make_frame(p->vb, f, &hw);
    if (!pb) return;
    if (hw) p->video_hw = 1;
    fq_push(&p->fq, (VFrame){ .pb = pb, .pts = pts, .dur = dur, .serial = serial });
}

static void *video_thread(void *arg) {
    PCPlayer *p = arg;
    AVFrame *frame = av_frame_alloc();
    int serial = -1;
    while (!atomic_load(&p->quit)) {
        AVPacket *pkt = NULL;
        int pserial = 0;
        int r = pq_get(&p->vq, &pkt, &pserial);
        if (r < 0) break;
        if (pserial != serial) { avcodec_flush_buffers(p->vdec); serial = pserial; atomic_store(&p->video_eof, 0); }
        if (r == 0) {
            avcodec_send_packet(p->vdec, NULL);
            while (avcodec_receive_frame(p->vdec, frame) >= 0) { video_frame(p, frame, serial); av_frame_unref(frame); }
            if (pserial == atomic_load(&p->vq.serial)) atomic_store(&p->video_eof, 1);
            continue;
        }
        int sr = avcodec_send_packet(p->vdec, pkt);
        while (avcodec_receive_frame(p->vdec, frame) >= 0) {
            video_frame(p, frame, serial);
            av_frame_unref(frame);
            if (pserial != atomic_load(&p->vq.serial)) break;
        }
        if (sr == AVERROR(EAGAIN)) avcodec_send_packet(p->vdec, pkt);
        av_packet_free(&pkt);
    }
    av_frame_free(&frame);
    return NULL;
}

// ---------------------------------------------------------------------------
// presenter

static void check_eof(PCPlayer *p) {
    if (atomic_load(&p->eof_sent) || !atomic_load(&p->demux_eof)) return;
    if (p->astream >= 0 && !atomic_load(&p->audio_eof)) return;
    if (p->vstream >= 0 && (!atomic_load(&p->video_eof) || fq_count(&p->fq) > 0)) return;
    pc_mutex_lock(&p->lock);
    int seeking = p->seek_req;
    pc_mutex_unlock(&p->lock);
    if (seeking) return;
    atomic_store(&p->eof_sent, 1);
    if (p->ecb) p->ecb(p->cbctx, PC_EVENT_EOF, NULL);
}

static void show(PCPlayer *p, VFrame *f) {
    if (p->vcb) p->vcb(p->cbctx, f->pb, f->pts);
    atomic_store(&p->last_video_pts, f->pts);
    fq_pop_id(&p->fq, f->id);
}

// Gapless: once the output clock reaches the join, the next file is the one being heard.
static void check_next(PCPlayer *p) {
    if (!atomic_load(&p->pending)) return;
    pc_mutex_lock(&p->lock);
    double at = p->pend_offset;
    pc_mutex_unlock(&p->lock);
    if (isnan(at)) return;
    if (!isinf(at)) {
        double c = p->out ? pc_out_clock(p->out) : NAN;
        if (isnan(c) || c < at) return;
    }
    pc_mutex_lock(&p->lock);
    p->item_offset = isinf(at) ? p->dmx_offset : at;
    char output[sizeof(p->info.output)];
    memcpy(output, p->info.output, sizeof(output));
    int mode = p->info.mode;
    char bsc[sizeof(p->info.bitstream_codec)];
    memcpy(bsc, p->info.bitstream_codec, sizeof(bsc));
    p->info = p->pend_info;
    memcpy(p->info.output, output, sizeof(output));     // the output itself is unchanged
    memcpy(p->info.bitstream_codec, bsc, sizeof(bsc));
    p->info.mode = mode;
    av_freep(&p->art);
    p->art = p->pend_art;
    p->art_size = p->pend_art_size;
    p->pend_art = NULL;
    p->pend_art_size = 0;
    p->last_pos = p->item_offset;
    atomic_store(&p->pending, 0);
    pc_mutex_unlock(&p->lock);
    if (p->ecb) p->ecb(p->cbctx, PC_EVENT_NEXT, NULL);
}

static void *present_thread(void *arg) {
    PCPlayer *p = arg;
    while (!atomic_load(&p->quit)) {
        check_next(p);
        check_eof(p);
        if (p->vstream < 0) { pc_sleep_us(20000); continue; }
        VFrame f;
        if (!fq_peek(&p->fq, 0, &f)) { pc_sleep_us(2000); continue; }
        if (atomic_exchange(&p->force_show, 0)) {
            // first frame after open/seek: show immediately, even when paused
            show(p, &f);
            if (!audio_is_master(p)) {
                ext_start(p, f.pts);
                if (atomic_load(&p->paused)) {
                    pc_mutex_lock(&p->lock); p->ext_paused_pts = f.pts; pc_mutex_unlock(&p->lock);
                }
            }
            pcvb_release(f.pb);
            continue;
        }
        if (atomic_load(&p->paused)) { pcvb_release(f.pb); pc_sleep_us(5000); continue; }
        double clock = video_clock(p);
        if (isnan(clock)) {
            if (!audio_is_master(p)) { ext_start(p, f.pts); clock = f.pts; }
            else { pcvb_release(f.pb); pc_sleep_us(2000); continue; }
        }
        double diff = f.pts - clock;
        if (diff > 0.002) {
            pcvb_release(f.pb);
            if (diff > 1.0 && audio_is_master(p)) diff = 0.01; // wait for the audio clock to settle
            pc_sleep_us((int64_t)(fmin(diff, 0.01) * 1e6));
            continue;
        }
        VFrame nx;
        if (fq_peek(&p->fq, 1, &nx)) {
            int late = nx.pts <= clock;
            pcvb_release(nx.pb);
            if (late) { fq_pop_id(&p->fq, f.id); pcvb_release(f.pb); continue; } // drop
        }
        show(p, &f);
        pcvb_release(f.pb);
    }
    return NULL;
}

// ---------------------------------------------------------------------------
// demux thread

// How far the demuxer reads ahead of playback (compressed packets in RAM). Slow media (a USB
// stick waking up or seeking, a network share) can stall reads for a second or more; this
// much queued data rides over that. Capped by the byte limit in demux_thread.
#define READ_AHEAD_SECONDS 10.0
#define READ_AHEAD_PACKETS 250     // for streams whose packets carry no duration

static double queued_seconds(PCPlayer *p, PacketQueue *q, int stream) {
    pc_mutex_lock(&q->m);
    double dur = q->duration * av_q2d(p->fmt->streams[stream]->time_base);
    pc_mutex_unlock(&q->m);
    return dur;
}

static int queue_enough(PCPlayer *p, PacketQueue *q, int stream) {
    if (stream < 0) return 1;
    pc_mutex_lock(&q->m);
    double dur = q->duration * av_q2d(p->fmt->streams[stream]->time_base);
    int enough = q->duration == 0 ? q->count > READ_AHEAD_PACKETS : (q->count > 25 && dur > READ_AHEAD_SECONDS);
    pc_mutex_unlock(&q->m);
    return enough;
}

static void do_seek(PCPlayer *p, double pos) {
    int64_t ts = (int64_t)((pos + p->start_time) * AV_TIME_BASE);
    if (avformat_seek_file(p->fmt, -1, INT64_MIN, ts, ts, 0) < 0)
        avformat_seek_file(p->fmt, -1, INT64_MIN, ts, INT64_MAX, 0);
    pc_mutex_lock(&p->lock);
    double off = p->dmx_offset;
    if (atomic_load(&p->pending)) p->pend_offset = -INFINITY;   // the next file is now the one heard
    pc_mutex_unlock(&p->lock);
    atomic_store(&p->seek_target, pos + off);
    pq_flush(&p->aq);
    pq_flush(&p->vq);
    fq_flush(&p->fq, atomic_load(&p->vq.serial));
    for (int i = 0; i < atomic_load(&p->n_sub); i++) {   // embedded subtitles are re-read from here
        if (!p->subt[i].dec) continue;
        avcodec_flush_buffers(p->subt[i].dec);
        subs_clear(p->subt[i].store);
    }
    atomic_store(&p->demux_eof, 0);
    atomic_store(&p->audio_eof, 0);
    atomic_store(&p->video_eof, 0);
    atomic_store(&p->eof_sent, 0);
    atomic_store(&p->force_show, 1);
    pc_mutex_lock(&p->lock);
    p->ext_started = 0;
    p->ext_paused_pts = pos + off;
    pc_mutex_unlock(&p->lock);
}

static int open_decoder(AVStream *st, AVCodecContext **out, PCVideoBackend *vb);

static void decode_subtitle(PCPlayer *p, int track, AVPacket *pkt) {
    AVStream *st = p->fmt->streams[pkt->stream_index];
    AVCodecContext *dec = p->subt[track].dec;
    AVSubtitle sub;
    int got = 0;
    if (avcodec_decode_subtitle2(dec, &sub, &got, pkt) < 0 || !got) return;
    double base = sub.pts != AV_NOPTS_VALUE ? sub.pts / (double)AV_TIME_BASE
                : pkt->pts != AV_NOPTS_VALUE ? pkt->pts * av_q2d(st->time_base) : NAN;
    base -= p->start_time;
    double start = base + sub.start_display_time / 1000.0;
    double end = sub.end_display_time > sub.start_display_time && sub.end_display_time != UINT32_MAX
                     ? base + sub.end_display_time / 1000.0
                 : pkt->duration > 0 ? start + pkt->duration * av_q2d(st->time_base) : INFINITY;
    subs_add(p->subt[track].store, &sub, start, end, dec->width, dec->height);
    avsubtitle_free(&sub);
    if (!isnan(start)) subs_prune(p->subt[track].store, start - 120);   // the demuxer runs a little ahead
}

static const char *meta(AVDictionary *d, const char *k);
static int audio_stream_bits(const AVCodecParameters *c);
static int64_t audio_stream_bitrate(AVFormatContext *fmt, AVStream *st);
static void describe_audio_stream(AVStream *st, char *buf, int len);

// Gapless: would the next file (audio stream `*as`) play exactly the way the current one does?
// Mirrors setup_audio's decisions without touching the device.
static int gapless_ok(PCPlayer *p, AVFormatContext *nf, int *as) {
    if (p->vstream >= 0 || p->plan_mode == PC_MODE_PASSTHROUGH || atomic_load(&p->n_sub) > 0 || !p->out) return 0;
    for (unsigned i = 0; i < nf->nb_streams; i++) {
        AVStream *st = nf->streams[i];
        enum AVMediaType t = st->codecpar->codec_type;
        if (t == AVMEDIA_TYPE_VIDEO && !(st->disposition & AV_DISPOSITION_ATTACHED_PIC)) return 0;
        if (t == AVMEDIA_TYPE_SUBTITLE) return 0;
    }
    int a = av_find_best_stream(nf, AVMEDIA_TYPE_AUDIO, -1, -1, NULL, 0);
    if (a < 0) return 0;
    AVCodecParameters *par = nf->streams[a]->codecpar;
    const PCConfig *cfg = &p->cfg;
    int src_ch = par->ch_layout.nb_channels;
    PCUpmix up = upmix_settings(p);
    int up_src = up.enabled && src_ch >= 1 && src_ch <= 2;
    int mode = PC_MODE_PCM;
    if (cfg->bitstream_enabled) {
        int dts = cfg->bitstream_codec == PC_CODEC_DTS;
        int native_ac3 = par->codec_id == AV_CODEC_ID_AC3 && par->sample_rate == 48000;
        int native_dts = dts && par->codec_id == AV_CODEC_ID_DTS && par->sample_rate == 48000 &&
                         par->profile != AV_PROFILE_DTS_EXPRESS;
        if (cfg->passthrough && (native_ac3 || native_dts) && !up_src) return 0;
        if (!cfg->multichannel_pcm && (src_ch > 2 || cfg->stereo_bitstream || up_src)) mode = PC_MODE_TRANSCODE;
    }
    // compare plans, not outcomes: a file that asks for the same route gets the same result from
    // the device (e.g. both fall back from Dolby Digital to PCM on a non-digital output)
    if (mode != p->plan_mode) return 0;
    if (mode == PC_MODE_TRANSCODE) {
        int ch = up_src || src_ch > 2 ? 6 : 2;
        if (ch != p->plan_ch || up_src != p->plan_up) return 0;
    } else {
        int want_excl = cfg->multichannel_pcm && (src_ch > 2 || up_src);
        if (want_excl != (p->mc_req_ch > 0)) return 0;
        if (want_excl) {
            AVChannelLayout lay = { 0 };
            if (up_src) av_channel_layout_copy(&lay, &(AVChannelLayout)AV_CHANNEL_LAYOUT_5POINT1);
            else if (par->ch_layout.order == AV_CHANNEL_ORDER_NATIVE && src_ch <= 8) av_channel_layout_copy(&lay, &par->ch_layout);
            else av_channel_layout_default(&lay, FFMIN(src_ch, 8));
            int rate = up_src ? 48000 : par->sample_rate > 0 ? par->sample_rate : 48000;
            int same = lay.nb_channels == p->mc_req_ch && (uint32_t)lay.u.mask == p->mc_req_mask && rate == p->mc_req_rate;
            av_channel_layout_uninit(&lay);
            if (!same) return 0;
        }
        if ((up_src && p->out_type == PC_STREAM_PCM_EXCLUSIVE) != p->upmixing) return 0;
    }
    *as = a;
    return 1;
}

// Gapless: at the end of the current file, continue into the next one if it is eligible.
// Returns 1 when the demuxer now reads the next file (a switch marker is queued for the audio
// thread), 0 to end playback as usual.
static int try_gapless(PCPlayer *p) {
    pc_mutex_lock(&p->lock);
    char *path = p->next_path;
    p->next_path = NULL;
    pc_mutex_unlock(&p->lock);
    if (!path) return 0;
    AVFormatContext *nf = NULL;
    AVCodecContext *nd = NULL;
    int as = -1;
    int ok = avformat_open_input(&nf, path, NULL, NULL) >= 0 && avformat_find_stream_info(nf, NULL) >= 0 &&
             gapless_ok(p, nf, &as) && open_decoder(nf->streams[as], &nd, NULL) >= 0;
    av_free(path);
    if (!ok) {
        avcodec_free_context(&nd);
        avformat_close_input(&nf);
        return 0;
    }
    double nstart = nf->start_time != AV_NOPTS_VALUE ? nf->start_time / (double)AV_TIME_BASE : 0;
    AVStream *ast = nf->streams[as];
    PCInfo ni;
    memset(&ni, 0, sizeof(ni));
    uint8_t *art = NULL;
    int art_size = 0, n_audio = 0, tracks[MAX_AUDIO_TRACKS], cur = -1;
    for (unsigned i = 0; i < nf->nb_streams; i++) {
        AVStream *st = nf->streams[i];
        st->discard = (int)i == as ? AVDISCARD_DEFAULT : AVDISCARD_ALL;
        if (st->codecpar->codec_type == AVMEDIA_TYPE_AUDIO && n_audio < MAX_AUDIO_TRACKS) {
            if ((int)i == as) cur = n_audio;
            tracks[n_audio++] = (int)i;
        }
        if ((st->disposition & AV_DISPOSITION_ATTACHED_PIC) && !art && st->attached_pic.size > 0) {
            art = av_memdup(st->attached_pic.data, st->attached_pic.size);
            art_size = art ? st->attached_pic.size : 0;
        }
    }
    const char *title = meta(nf->metadata, "title");
    if (!title) title = meta(ast->metadata, "title");
    copy_str(ni.title, sizeof(ni.title), title);
    const char *artist = meta(nf->metadata, "artist");
    if (!artist) artist = meta(nf->metadata, "album_artist");
    copy_str(ni.artist, sizeof(ni.artist), artist);
    copy_str(ni.album, sizeof(ni.album), meta(nf->metadata, "album"));
    ni.duration = nf->duration != AV_NOPTS_VALUE ? nf->duration / (double)AV_TIME_BASE : 0;
    describe_audio_stream(ast, ni.source_audio, sizeof(ni.source_audio));
    ni.source_rate = ast->codecpar->sample_rate;
    ni.source_bits = audio_stream_bits(ast->codecpar);
    int64_t br = audio_stream_bitrate(nf, ast);
    ni.source_bitrate = br > INT32_MAX ? INT32_MAX : (int)br;

    double old_dur = p->fmt->duration != AV_NOPTS_VALUE ? p->fmt->duration / (double)AV_TIME_BASE : 0;
    pc_mutex_lock(&p->lock);
    double prov = p->dmx_offset + old_dur;   // refined by the audio thread when it reaches the join
    avcodec_free_context(&p->sw_dec);
    p->sw_dec = nd;
    p->sw_tb = ast->time_base;
    p->sw_start = nstart;
    p->sw_offset = prov;
    p->pend_info = ni;
    av_freep(&p->pend_art);
    p->pend_art = art;
    p->pend_art_size = art_size;
    p->pend_offset = NAN;
    atomic_store(&p->pending, 1);
    p->dmx_offset = prov;
    // the demuxer now reads the next file; the previous context stays alive until the next switch
    if (p->old_fmt) avformat_close_input(&p->old_fmt);
    p->old_fmt = p->fmt;
    p->fmt = nf;
    p->astream = as;
    p->start_time = nstart;
    memcpy(p->audio_tracks, tracks, sizeof(int) * (size_t)n_audio);
    p->n_audio = n_audio;
    p->cur_track = cur;
    pc_mutex_unlock(&p->lock);
    AVPacket *m = av_packet_alloc();
    m->stream_index = PC_SWITCH_MARK;
    pq_put(&p->aq, m);
    av_packet_free(&m);
    return 1;
}

static void *demux_thread(void *arg) {
    PCPlayer *p = arg;
    AVPacket *pkt = av_packet_alloc();
    while (!atomic_load(&p->quit)) {
        pc_mutex_lock(&p->lock);
        if (p->seek_req) {
            double pos = p->seek_pos;
            pc_mutex_unlock(&p->lock);
            do_seek(p, pos);
            pc_mutex_lock(&p->lock);
            if (p->seek_pos == pos) p->seek_req = 0;
        }
        pc_mutex_unlock(&p->lock);

        int64_t total = p->aq.bytes + p->vq.bytes;
        if ((queue_enough(p, &p->aq, p->astream) && queue_enough(p, &p->vq, p->vstream)) || total > 256 * 1024 * 1024) {
            pc_sleep_us(10000);
            continue;
        }
        int ret = av_read_frame(p->fmt, pkt);
        if (ret < 0) {
            if (ret != AVERROR(EAGAIN) && !atomic_load(&p->demux_eof)) {
                if (p->astream >= 0 && try_gapless(p)) continue;   // gapless: on into the next file
                // An audio-only file can be read to the end within moments of opening (short tracks,
                // 10 s read-ahead), before the app has named the next file. End the stream only once
                // little audio is left, so a pc_player_set_next that comes a little late still joins.
                if (p->astream >= 0 && p->vstream < 0 && queued_seconds(p, &p->aq, p->astream) > 1.0) {
                    pc_sleep_us(10000);
                    continue;
                }
                if (p->astream >= 0) pq_put(&p->aq, NULL);
                if (p->vstream >= 0) pq_put(&p->vq, NULL);
                atomic_store(&p->demux_eof, 1);
            }
            pc_sleep_us(10000);
            continue;
        }
        if (pkt->stream_index == p->astream) pq_put(&p->aq, pkt);
        else if (pkt->stream_index == p->vstream) pq_put(&p->vq, pkt);
        else if (p->fmt->streams[pkt->stream_index]->codecpar->codec_type == AVMEDIA_TYPE_SUBTITLE) {
            for (int i = 0; i < atomic_load(&p->n_sub); i++)
                if (p->subt[i].stream == pkt->stream_index && p->subt[i].dec) { decode_subtitle(p, i, pkt); break; }
        }
        av_packet_unref(pkt);
    }
    av_packet_free(&pkt);
    return NULL;
}

// ---------------------------------------------------------------------------
// open / close

static const char *meta(AVDictionary *d, const char *k) {
    AVDictionaryEntry *e = av_dict_get(d, k, NULL, 0);
    return e ? e->value : NULL;
}

/// Source bit depth: what the stream declares (FLAC, ALAC, WavPack, DTS, TrueHD...), else the
/// sample size of a lossless/PCM codec. 0 for lossy codecs, where it has no meaning.
static int audio_stream_bits(const AVCodecParameters *c) {
    if (c->bits_per_raw_sample > 0) return c->bits_per_raw_sample;
    const AVCodecDescriptor *d = avcodec_descriptor_get(c->codec_id);
    if (!d || !(d->props & AV_CODEC_PROP_LOSSLESS)) return 0;
    int b = av_get_exact_bits_per_sample(c->codec_id);
    if (b <= 0) b = c->bits_per_coded_sample;
    return b >= 8 ? b : 0;
}

/// The audio track's bitrate: what the stream declares (MP3/AAC/AC3/DTS headers), else, for a
/// file with no other streams (attached cover art aside), the file's average.
static int64_t audio_stream_bitrate(AVFormatContext *fmt, AVStream *st) {
    if (st->codecpar->bit_rate > 0) return st->codecpar->bit_rate;
    int others = 0;
    for (unsigned i = 0; i < fmt->nb_streams; i++) {
        AVStream *o = fmt->streams[i];
        if (o != st && !(o->disposition & AV_DISPOSITION_ATTACHED_PIC)) others++;
    }
    return others == 0 && fmt->bit_rate > 0 ? fmt->bit_rate : 0;
}

static void describe_audio_stream(AVStream *st, char *buf, int len) {
    char layout[64] = "";
    av_channel_layout_describe(&st->codecpar->ch_layout, layout, sizeof(layout));
    const char *profile = avcodec_profile_name(st->codecpar->codec_id, st->codecpar->profile);
    int bits = audio_stream_bits(st->codecpar);
    char depth[16] = "";
    if (bits > 0) snprintf(depth, sizeof(depth), ", %d-bit", bits);
    snprintf(buf, len, "%s%s%s %s %d Hz%s", avcodec_get_name(st->codecpar->codec_id),
             profile ? " " : "", profile ? profile : "", layout, st->codecpar->sample_rate, depth);
}

static int open_decoder(AVStream *st, AVCodecContext **out, PCVideoBackend *vb) {
    const AVCodec *c = avcodec_find_decoder(st->codecpar->codec_id);
    if (!c) return -1;
    AVCodecContext *d = avcodec_alloc_context3(c);
    avcodec_parameters_to_context(d, st->codecpar);
    d->pkt_timebase = st->time_base;
    d->thread_count = 0;
    if (vb) pcvb_prepare_decoder(vb, d);
    if (avcodec_open2(d, c, NULL) < 0) { avcodec_free_context(&d); return -1; }
    *out = d;
    return 0;
}

static void free_cfg(PCConfig *c) {
    av_freep(&c->device_uid);
    av_freep(&c->dump_path);
}

static void drop_kept_output(PCPlayer *p) {
    pc_out_close(p->kept_out);
    p->kept_out = NULL;
}

// req: PC_STREAM_PCM_EXCLUSIVE only (channels / rate / channel_mask wanted)
static PCOut *take_output(PCPlayer *p, PCStreamType type, int burst, const PCDeviceFormat *req, char *oerr, int len) {
    int bitstream = type == PC_STREAM_IEC61937_AC3 || type == PC_STREAM_IEC61937_DTS;
    const PCConfig *cfg = &p->cfg;
    if (cfg->dump_path) {
        if (type == PC_STREAM_PCM_EXCLUSIVE)
            return pc_out_open_file(cfg->dump_path, 0, req->channels, req->rate, req->channel_mask, oerr, len);
        return pc_out_open_file(cfg->dump_path, bitstream, 2, 48000, 0, oerr, len);
    }
    const char *uid = cfg->device_uid ? cfg->device_uid : "";
    if (p->kept_out && pc_out_lost(p->kept_out)) drop_kept_output(p);   // never reuse a dead stream
    if (p->kept_out) {
        // reuse only for the same device, mode and burst size (AC3 <-> DTS needs a fresh stream);
        // exclusive PCM is opened per file (its channels and rate follow the source)
        if (p->kept_type == type && type != PC_STREAM_PCM_EXCLUSIVE && (!bitstream || p->kept_burst == burst) &&
            strcmp(p->kept_uid, uid) == 0) {
            PCOut *o = p->kept_out;
            p->kept_out = NULL;
            pc_out_flush(o);
            return o;
        }
        drop_kept_output(p);
    }
    PCOut *o = pc_out_open(uid, type, req, oerr, len);
    if (o && bitstream) pc_out_set_burst_size(o, burst);
    return o;
}

static int setup_audio(PCPlayer *p, char *err, int errlen) {
    AVStream *st = p->fmt->streams[p->astream];
    AVCodecParameters *par = st->codecpar;
    const PCConfig *cfg = &p->cfg;
    int src_ch = par->ch_layout.nb_channels;
    int dts = cfg->bitstream_codec == PC_CODEC_DTS;
    int64_t ac3_rate = (int64_t)(cfg->ac3_bitrate_kbps > 0 ? FFMIN(cfg->ac3_bitrate_kbps, 640) : 640) * 1000;
    // DTS nominal 1536/768 kb/s are really 1509.75/754.5 kb/s; both fit a 2048-byte burst
    int64_t dts_rate = cfg->dts_bitrate_kbps > 0 && cfg->dts_bitrate_kbps < 1000 ? 754500 : 1509750;
    // stereo upmix: mono/stereo sources become 5.1 when the output is surround
    PCUpmix up = upmix_settings(p);
    int up_src = up.enabled && src_ch >= 1 && src_ch <= 2 && (up.include_video || p->vstream < 0);
    p->upmixing = 0;

    // Decide: PCM, pass the source's own compressed frames through, or transcode.
    int mode = PC_MODE_PCM;
    if (cfg->bitstream_enabled) {
        int native_ac3 = par->codec_id == AV_CODEC_ID_AC3 && par->sample_rate == 48000;
        // DTS core (incl. the core of DTS-HD, the packer strips extensions); not DTS Express (no core)
        int native_dts = dts && par->codec_id == AV_CODEC_ID_DTS && par->sample_rate == 48000 &&
                         par->profile != AV_PROFILE_DTS_EXPRESS;
        if (cfg->passthrough && (native_ac3 || native_dts) && !up_src) {
            mode = PC_MODE_PASSTHROUGH;
            p->bs_codec = par->codec_id;
        } else if (!cfg->multichannel_pcm && (src_ch > 2 || cfg->stereo_bitstream || up_src)) {
            mode = PC_MODE_TRANSCODE;
            p->bs_codec = dts ? AV_CODEC_ID_DTS : AV_CODEC_ID_AC3;
        }
    }
    p->plan_mode = mode;   // gapless compares the next file's plan with this one
    p->plan_up = up_src;
    p->plan_ch = up_src || src_ch > 2 ? 6 : 2;
    if (mode != PC_MODE_PCM) {
        if (p->bs_codec == AV_CODEC_ID_AC3) p->enc_frame = 1536;
        else p->enc_frame = mode == PC_MODE_PASSTHROUGH && par->frame_size > 0 ? par->frame_size : 512;
        p->burst_bytes = p->enc_frame * 4;
        if (p->burst_bytes > PC_MAX_BURST_BYTES) {   // unusually long DTS frames: re-encode instead
            mode = PC_MODE_TRANSCODE;
            p->enc_frame = 512;
            p->burst_bytes = 2048;
        }
    }

    if (mode == PC_MODE_PASSTHROUGH) {
        open_decoder(st, &p->adec, NULL);   // optional: only feeds the visualiser
    } else if (open_decoder(st, &p->adec, NULL) < 0) {
        snprintf(err, errlen, "No decoder for audio codec %s", avcodec_get_name(par->codec_id));
        return -1;
    }

    char oerr[256] = "";
    char fallback[300] = "";
    if (mode != PC_MODE_PCM) {
        p->out = take_output(p, p->bs_codec == AV_CODEC_ID_DTS ? PC_STREAM_IEC61937_DTS : PC_STREAM_IEC61937_AC3,
                             p->burst_bytes, NULL, oerr, sizeof(oerr));
        if (!p->out) {
            if (mode == PC_MODE_PASSTHROUGH && !p->adec) {
                snprintf(err, errlen, "Bitstream output failed (%s) and no decoder available", oerr);
                return -1;
            }
            snprintf(fallback, sizeof(fallback), " [bitstream unavailable: %s]", oerr);
            mode = PC_MODE_PCM;
        }
    }
    PCStreamType pcm_type = PC_STREAM_PCM;
    if (mode == PC_MODE_PCM && cfg->multichannel_pcm && (src_ch > 2 || up_src)) {
        // multichannel PCM, untouched: the source's own layout and rate, exclusive
        // (an upmixed stereo source: 5.1 at its own rate)
        PCDeviceFormat req = { 0 };
        AVChannelLayout lay = { 0 };
        // An upmix goes out exactly like a native 5.1 track: 5.1 with *side* surrounds (the
        // layout Windows' "5.1" speaker setup and most HDMI sinks expect; the graph maps the
        // filter's back surrounds onto them) at 48 kHz. Asking for 5.1(back) at the source's
        // 44.1 kHz was accepted by drivers but left some HDMI receivers playing only L/R.
        if (up_src) av_channel_layout_copy(&lay, &(AVChannelLayout)AV_CHANNEL_LAYOUT_5POINT1);
        else if (par->ch_layout.order == AV_CHANNEL_ORDER_NATIVE && src_ch <= 8) av_channel_layout_copy(&lay, &par->ch_layout);
        else av_channel_layout_default(&lay, FFMIN(src_ch, 8));
        req.channels = lay.nb_channels;
        req.rate = up_src ? 48000 : par->sample_rate > 0 ? par->sample_rate : 48000;
        req.channel_mask = (uint32_t)lay.u.mask;   // FFmpeg AV_CH_* bits == WAVE SPEAKER_* bits
        av_channel_layout_uninit(&lay);
        p->mc_req_ch = req.channels;   // gapless continues only into files making the same request
        p->mc_req_mask = req.channel_mask;
        p->mc_req_rate = req.rate;
        p->out = take_output(p, PC_STREAM_PCM_EXCLUSIVE, 0, &req, oerr, sizeof(oerr));
        if (p->out) pcm_type = PC_STREAM_PCM_EXCLUSIVE;
        else snprintf(fallback, sizeof(fallback), " [multichannel PCM unavailable: %s]", oerr);
    }
    if (mode == PC_MODE_PCM && !p->out) {
        p->out = take_output(p, PC_STREAM_PCM, 0, NULL, oerr, sizeof(oerr));
        if (!p->out) { snprintf(err, errlen, "Audio output failed: %s", oerr); return -1; }
    }
    p->mode = mode;
    p->out_type = mode == PC_MODE_PCM ? pcm_type
                : p->bs_codec == AV_CODEC_ID_DTS ? PC_STREAM_IEC61937_DTS : PC_STREAM_IEC61937_AC3;
    p->next_pts = NAN;

    AVChannelLayout enc_layout = { 0 };
    if (mode == PC_MODE_PCM) {
        int ch = pc_out_channels(p->out);
        if (ch > 8) ch = 8;
        uint32_t mask = pc_out_channel_mask(p->out);
        if (!mask || av_channel_layout_from_mask(&p->out_layout, mask) < 0 || p->out_layout.nb_channels != ch)
            av_channel_layout_default(&p->out_layout, ch);
        p->out_rate = (int)pc_out_rate(p->out);
        p->out_fmt = AV_SAMPLE_FMT_FLT;
        char lay[64] = "";
        av_channel_layout_describe(&p->out_layout, lay, sizeof(lay));
        if (pcm_type == PC_STREAM_PCM_EXCLUSIVE)
            snprintf(p->info.output, sizeof(p->info.output), "PCM %s %d Hz (multichannel, untranscoded) -> %s%s", lay,
                     p->out_rate, pc_out_description(p->out), fallback);
        else
            snprintf(p->info.output, sizeof(p->info.output), "PCM %d ch %d Hz -> %s%s", ch, p->out_rate,
                     pc_out_description(p->out), fallback);
    }
    // provisional (the descriptions below use it); checked once the output format is final
    p->upmixing = up_src && (mode == PC_MODE_TRANSCODE || pcm_type == PC_STREAM_PCM_EXCLUSIVE);
    if (mode == PC_MODE_PCM) {
        if (pcm_type == PC_STREAM_PCM_EXCLUSIVE && p->upmixing) {
            char lay[64] = "";
            av_channel_layout_describe(&p->out_layout, lay, sizeof(lay));
            snprintf(p->info.output, sizeof(p->info.output), "PCM %s %d Hz (upmixed from %s) -> %s%s", lay, p->out_rate,
                     src_ch == 1 ? "mono" : "stereo", pc_out_description(p->out), fallback);
        }
    } else {
        const char *cname = p->bs_codec == AV_CODEC_ID_DTS ? "DTS" : "Dolby Digital";
        snprintf(p->info.bitstream_codec, sizeof(p->info.bitstream_codec), "%s", cname);
        int64_t rate = p->bs_codec == AV_CODEC_ID_DTS ? dts_rate : ac3_rate;
        if (mode == PC_MODE_PASSTHROUGH) {
            av_channel_layout_copy(&enc_layout, &par->ch_layout);
        } else {
            // upmixed stereo: 5.1(side). The upmix graph makes back surrounds and its aformat stage
            // moves them to the side pair; FFmpeg's DTS encoder has no 5.1(back) (1.3.0 failed to open it)
            enc_layout = src_ch > 2 || up_src ? (AVChannelLayout)AV_CHANNEL_LAYOUT_5POINT1 : (AVChannelLayout)AV_CHANNEL_LAYOUT_STEREO;
            if (src_ch <= 2 && !up_src && p->bs_codec == AV_CODEC_ID_AC3) rate = FFMIN(rate, 320000);
            p->aenc = open_bs_encoder(p->bs_codec, &enc_layout, rate);
            if (!p->aenc && p->bs_codec == AV_CODEC_ID_AC3) {
                enc_layout = (AVChannelLayout)AV_CHANNEL_LAYOUT_5POINT1_BACK;
                p->aenc = open_bs_encoder(p->bs_codec, &enc_layout, rate);
            }
            if (!p->aenc) { snprintf(err, errlen, "Could not open the %s encoder", cname); return -1; }
            p->enc_frame = p->aenc->frame_size;
            p->out_fmt = p->aenc->sample_fmt;
            p->out_rate = 48000;
            av_channel_layout_copy(&p->out_layout, &p->aenc->ch_layout);
            p->fifo = av_audio_fifo_alloc(p->out_fmt, p->out_layout.nb_channels, p->enc_frame * 4);
            p->eframe = alloc_enc_frame(p->aenc);
            p->epkt = av_packet_alloc();
        }
        if (spdif_open(p, p->bs_codec, &enc_layout) < 0) { snprintf(err, errlen, "Could not open the IEC 61937 packer"); return -1; }
        make_silence_burst(p, &enc_layout, p->bs_codec == AV_CODEC_ID_AC3 && mode == PC_MODE_PASSTHROUGH ? 448000 : rate);
        char lay[64] = "";
        av_channel_layout_describe(&enc_layout, lay, sizeof(lay));
        if (mode == PC_MODE_PASSTHROUGH)
            snprintf(p->info.output, sizeof(p->info.output), "%s %s %lld kb/s passthrough%s, IEC 61937 -> %s", cname,
                     lay, (long long)(par->bit_rate / 1000), p->bs_codec == AV_CODEC_ID_DTS && par->codec_id == AV_CODEC_ID_DTS &&
                     par->profile != AV_PROFILE_DTS ? " (core)" : "", pc_out_description(p->out));
        else
            snprintf(p->info.output, sizeof(p->info.output), "%s %s %lld kb/s (%s), IEC 61937 -> %s%s", cname,
                     lay, (long long)(p->aenc->bit_rate / 1000),
                     p->upmixing ? (src_ch == 1 ? "upmixed from mono, transcoded" : "upmixed from stereo, transcoded")
                                 : "transcoded", pc_out_description(p->out), fallback);
        av_channel_layout_uninit(&enc_layout);
    }
    if (p->upmixing) {
        // check now that the graph builds for this output, so playback never goes silent later;
        // without it the output still works (swr places stereo in the front pair)
        char e[256] = "";
        PCUpmixer *test = pc_upmix_create(&up, p->out_rate, &p->out_layout, p->out_fmt, e, sizeof(e));
        if (test) {
            pc_upmix_free(&test);
            p->um_frame = av_frame_alloc();
        } else {
            p->upmixing = 0;
            size_t l = strlen(p->info.output);
            snprintf(p->info.output + l, sizeof(p->info.output) - l, " [upmix unavailable: %s]", e);
        }
    }
    p->info.mode = mode;
    describe_audio_stream(st, p->info.source_audio, sizeof(p->info.source_audio));
    p->info.source_rate = st->codecpar->sample_rate;
    int64_t br = audio_stream_bitrate(p->fmt, st);
    p->info.source_bitrate = br > INT32_MAX ? INT32_MAX : (int)br;
    p->info.source_bits = audio_stream_bits(st->codecpar);
    return 0;
}

static void close_internal(PCPlayer *p, int keep_out);

int pc_player_open(PCPlayer *p, const char *path, const PCConfig *cfg, int paused, char *err, int errlen) {
    close_internal(p, 1);
    memset(&p->info, 0, sizeof(p->info));
    p->cfg = *cfg;
    p->cfg.device_uid = cfg->device_uid ? av_strdup(cfg->device_uid) : NULL;
    p->cfg.dump_path = cfg->dump_path ? av_strdup(cfg->dump_path) : NULL;
    p->astream = p->vstream = -1;
    p->n_audio = 0;
    atomic_store(&p->n_sub, 0);
    atomic_store(&p->cur_sub, -1);
    p->sub_last_track = -1;
    p->cur_track = -1;
    p->mc_req_ch = p->mc_req_rate = 0;
    p->mc_req_mask = 0;
    p->dmx_offset = p->item_offset = p->a_offset = 0;
    p->pend_offset = NAN;
    atomic_store(&p->pending, 0);

    if (avformat_open_input(&p->fmt, path, NULL, NULL) < 0) {
        snprintf(err, errlen, "Could not open file");
        free_cfg(&p->cfg);
        return -1;
    }
    p->is_open = 1;
    if (avformat_find_stream_info(p->fmt, NULL) < 0) {
        snprintf(err, errlen, "Could not read stream info");
        goto fail;
    }
    p->start_time = p->fmt->start_time != AV_NOPTS_VALUE ? p->fmt->start_time / (double)AV_TIME_BASE : 0;

    int vbest = av_find_best_stream(p->fmt, AVMEDIA_TYPE_VIDEO, -1, -1, NULL, 0);
    for (unsigned i = 0; i < p->fmt->nb_streams; i++) {
        AVStream *st = p->fmt->streams[i];
        st->discard = AVDISCARD_ALL;
        if (st->codecpar->codec_type == AVMEDIA_TYPE_AUDIO && p->n_audio < MAX_AUDIO_TRACKS)
            p->audio_tracks[p->n_audio++] = (int)i;
        if (st->codecpar->codec_type == AVMEDIA_TYPE_SUBTITLE && p->n_sub < MAX_SUB_TRACKS) {
            int n = p->n_sub;
            if (open_decoder(st, &p->subt[n].dec, NULL) < 0) continue;
            p->subt[n].store = subs_create();
            st->discard = AVDISCARD_DEFAULT;
            const char *lang = meta(st->metadata, "language");
            const char *title = meta(st->metadata, "title");
            p->subt[n].stream = (int)i;
            p->subt[n].flags = ((st->disposition & AV_DISPOSITION_DEFAULT) ? PC_SUBFLAG_DEFAULT : 0) |
                               ((st->disposition & AV_DISPOSITION_FORCED) ? PC_SUBFLAG_FORCED : 0);
            p->subt[n].path = NULL;
            snprintf(p->subt[n].lang, sizeof(p->subt[n].lang), "%s", lang && strcmp(lang, "und") ? lang : "");
            snprintf(p->subt[n].name, sizeof(p->subt[n].name), "%d: %s%s%s%s%s%s", n + 1,
                     p->subt[n].lang, p->subt[n].lang[0] ? " - " : "", avcodec_get_name(st->codecpar->codec_id),
                     title ? " - " : "", title ? title : "",
                     (st->disposition & AV_DISPOSITION_FORCED) ? " (forced)" : "");
            atomic_store(&p->n_sub, n + 1);
        }
        if (st->disposition & AV_DISPOSITION_ATTACHED_PIC) {
            if (!p->art && st->attached_pic.size > 0) {
                p->art = av_memdup(st->attached_pic.data, st->attached_pic.size);
                p->art_size = st->attached_pic.size;
            }
        } else if (st->codecpar->codec_type == AVMEDIA_TYPE_VIDEO && p->vstream < 0 &&
                   (vbest < 0 || (int)i == vbest || (p->fmt->streams[vbest]->disposition & AV_DISPOSITION_ATTACHED_PIC))) {
            p->vstream = (int)i;
        }
    }
    if (cfg->audio_track >= 0 && cfg->audio_track < p->n_audio) {
        p->astream = p->audio_tracks[cfg->audio_track];
    } else {
        p->astream = av_find_best_stream(p->fmt, AVMEDIA_TYPE_AUDIO, -1, p->vstream, NULL, 0);
        if (p->astream < 0) p->astream = -1;
    }
    for (int i = 0; i < p->n_audio; i++) if (p->audio_tracks[i] == p->astream) p->cur_track = i;
    if (p->astream < 0 && p->vstream < 0) { snprintf(err, errlen, "No playable audio or video streams"); goto fail; }

    if (p->astream >= 0) {
        p->fmt->streams[p->astream]->discard = AVDISCARD_DEFAULT;
        p->a_tb = p->fmt->streams[p->astream]->time_base;
        p->a_start = p->start_time;
        if (setup_audio(p, err, errlen) < 0) goto fail;
    }
    if (p->vstream >= 0) {
        AVStream *st = p->fmt->streams[p->vstream];
        st->discard = AVDISCARD_DEFAULT;
        p->vb = pcvb_create();
        if (open_decoder(st, &p->vdec, p->vb) < 0 && open_decoder(st, &p->vdec, NULL) < 0) {
            if (p->astream < 0) { snprintf(err, errlen, "No decoder for video codec"); goto fail; }
            st->discard = AVDISCARD_ALL;
            p->vstream = -1;
        }
    }

    // metadata
    AVDictionary *md = p->fmt->metadata;
    const char *title = meta(md, "title");
    if (!title && p->astream >= 0) title = meta(p->fmt->streams[p->astream]->metadata, "title");
    copy_str(p->info.title, sizeof(p->info.title), title);
    const char *artist = meta(md, "artist");
    if (!artist) artist = meta(md, "album_artist");
    copy_str(p->info.artist, sizeof(p->info.artist), artist);
    copy_str(p->info.album, sizeof(p->info.album), meta(md, "album"));
    p->info.duration = p->fmt->duration != AV_NOPTS_VALUE ? p->fmt->duration / (double)AV_TIME_BASE : 0;
    p->info.has_video = p->vstream >= 0;
    if (p->vstream >= 0) {
        AVCodecParameters *vp = p->fmt->streams[p->vstream]->codecpar;
        p->info.video_width = vp->width;
        p->info.video_height = vp->height;
        snprintf(p->info.video, sizeof(p->info.video), "%s %dx%d", avcodec_get_name(vp->codec_id), vp->width, vp->height);
    }
    if (p->astream < 0) p->info.mode = -1;

    // threads
    pq_init(&p->aq);
    pq_init(&p->vq);
    fq_init(&p->fq);
    atomic_store(&p->quit, 0);
    atomic_store(&p->paused, paused ? 1 : 0);
    atomic_store(&p->demux_eof, 0);
    atomic_store(&p->audio_eof, 0);
    atomic_store(&p->video_eof, 0);
    atomic_store(&p->eof_sent, 0);
    atomic_store(&p->force_show, 1);
    atomic_store(&p->av_offset, cfg->av_offset);
    atomic_store(&p->last_video_pts, 0);
    atomic_store(&p->seek_target, NAN);
    p->seek_req = 0;
    p->ext_started = 0;
    p->last_pos = 0;
    if (cfg->start_position > 0.5) {
        do_seek(p, cfg->start_position);
        p->last_pos = cfg->start_position;
    }
    drop_kept_output(p);   // not reused by this file
    if (p->out) {
        pc_out_set_paused(p->out, paused ? 1 : 0);
        if (pc_out_start(p->out) < 0) { snprintf(err, errlen, "Could not start the audio device"); goto fail_threads; }
    }
    p->threads_running = 1;
    pc_thread_create(&p->th_demux, demux_thread, p);
    if (p->astream >= 0) { pc_thread_create(&p->th_audio, audio_thread, p); p->have_audio_thread = 1; }
    if (p->vstream >= 0) { pc_thread_create(&p->th_video, video_thread, p); p->have_video_thread = 1; }
    pc_thread_create(&p->th_present, present_thread, p);
    return 0;

fail_threads:
    pq_destroy(&p->aq);
    pq_destroy(&p->vq);
    fq_destroy(&p->fq);
fail:
    p->threads_running = 0;
    close_internal(p, 0);
    return -1;
}

void pc_player_close(PCPlayer *p) {
    close_internal(p, 0);
    drop_kept_output(p);
}

static void close_internal(PCPlayer *p, int keep_out) {
    if (!p->is_open) return;
    if (p->threads_running) {
        atomic_store(&p->quit, 1);
        pq_abort(&p->aq);
        pq_abort(&p->vq);
        fq_abort(&p->fq);
        pc_thread_join(p->th_demux);
        if (p->have_audio_thread) pc_thread_join(p->th_audio);
        if (p->have_video_thread) pc_thread_join(p->th_video);
        pc_thread_join(p->th_present);
        pq_destroy(&p->aq);
        pq_destroy(&p->vq);
        fq_destroy(&p->fq);
        p->threads_running = p->have_audio_thread = p->have_video_thread = 0;
    }
    if (keep_out && p->out && !p->cfg.dump_path) {
        drop_kept_output(p);
        p->kept_out = p->out;
        p->kept_type = p->out_type;
        p->kept_burst = p->burst_bytes;
        snprintf(p->kept_uid, sizeof(p->kept_uid), "%s", p->cfg.device_uid ? p->cfg.device_uid : "");
    } else {
        pc_out_close(p->out);
    }
    p->out = NULL;
    avcodec_free_context(&p->adec);
    avcodec_free_context(&p->vdec);
    avcodec_free_context(&p->aenc);
    pc_upmix_free(&p->um);
    av_frame_free(&p->um_frame);
    p->upmixing = 0;
    swr_free(&p->swr);
    swr_free(&p->tap_swr);
    av_channel_layout_uninit(&p->tap_in_layout);
    av_freep(&p->tap_tmp);
    p->tap_tmp_frames = 0;
    pc_mutex_lock(&p->tap_lock);
    p->tap_mcount = 0;
    pc_mutex_unlock(&p->tap_lock);
    av_channel_layout_uninit(&p->swr_in_layout);
    av_channel_layout_uninit(&p->out_layout);
    if (p->cvt) { av_freep(&p->cvt[0]); av_freep(&p->cvt); }
    p->cvt_samples = 0;
    if (p->fifo) { av_audio_fifo_free(p->fifo); p->fifo = NULL; }
    av_frame_free(&p->eframe);
    av_packet_free(&p->epkt);
    spdif_close(p);
    av_freep(&p->burst);
    p->burst_cap = p->burst_len = 0;
    p->enc_counter = p->spdif_ts = 0;
    pcvb_destroy(p->vb);   // after the threads: queued frames are already released
    p->vb = NULL;
    avformat_close_input(&p->fmt);
    avformat_close_input(&p->old_fmt);
    av_freep(&p->art);
    p->art_size = 0;
    av_freep(&p->next_path);
    avcodec_free_context(&p->sw_dec);
    av_freep(&p->pend_art);
    p->pend_art_size = 0;
    atomic_store(&p->pending, 0);
    for (int i = 0; i < p->n_sub; i++) {
        av_freep(&p->subt[i].path);
        avcodec_free_context(&p->subt[i].dec);
        subs_destroy(p->subt[i].store);
        p->subt[i].store = NULL;
    }
    atomic_store(&p->n_sub, 0);
    atomic_store(&p->cur_sub, -1);
    free_cfg(&p->cfg);
    p->astream = p->vstream = -1;
    p->video_hw = 0;
    p->is_open = 0;
}

// ---------------------------------------------------------------------------
// control

void pc_player_set_paused(PCPlayer *p, int paused) {
    if (!p->is_open) return;
    pc_mutex_lock(&p->lock);
    if (paused && !p->paused) {
        p->ext_paused_pts = ext_clock_locked(p);
        if (isnan(p->ext_paused_pts)) p->ext_paused_pts = atomic_load(&p->last_video_pts);
    } else if (!paused && p->paused && p->ext_started) {
        p->ext_base_pts = p->ext_paused_pts;
        p->ext_base_time = now_sec();
    }
    atomic_store(&p->paused, paused ? 1 : 0);
    pc_mutex_unlock(&p->lock);
    if (p->out) pc_out_set_paused(p->out, paused);
}

int pc_player_is_paused(PCPlayer *p) { return atomic_load(&p->paused); }

void pc_player_seek(PCPlayer *p, double seconds) {
    if (!p->is_open) return;
    if (seconds < 0) seconds = 0;
    if (p->info.duration > 0 && seconds > p->info.duration - 0.5) seconds = fmax(0, p->info.duration - 0.5);
    pc_mutex_lock(&p->lock);
    p->seek_req = 1;
    p->seek_pos = seconds;
    p->last_pos = seconds + p->item_offset;
    pc_mutex_unlock(&p->lock);
}

void pc_player_set_next(PCPlayer *p, const char *path) {
    if (!p->is_open) return;
    pc_mutex_lock(&p->lock);
    av_freep(&p->next_path);
    if (path && *path) p->next_path = av_strdup(path);
    pc_mutex_unlock(&p->lock);
}

int pc_player_next_pending(PCPlayer *p) { return p->is_open && atomic_load(&p->pending); }

void pc_player_set_upmix(PCPlayer *p, const PCUpmix *u) {
    PCUpmix v = *u;
    pc_upmix_sanitize(&v);
    pc_mutex_lock(&p->upmix_lock);
    p->upmix = v;
    pc_mutex_unlock(&p->upmix_lock);
    atomic_fetch_add(&p->upmix_gen, 1);
}

int pc_player_upmix_active(PCPlayer *p) { return p->is_open && p->upmixing; }

int pc_player_output_lost(PCPlayer *p) {
    return p->is_open && p->out && pc_out_lost(p->out);
}

double pc_player_position(PCPlayer *p) {
    if (!p->is_open) return 0;
    pc_mutex_lock(&p->lock);
    if (p->seek_req) { double s = p->seek_pos; pc_mutex_unlock(&p->lock); return s; }
    pc_mutex_unlock(&p->lock);
    double c;
    if (audio_is_master(p)) {
        c = pc_out_clock(p->out);
        double t = atomic_load(&p->seek_target);
        if (!isnan(c) && !isnan(t) && c < t - 1.0) c = NAN; // stale clock right after a seek
    } else {
        pc_mutex_lock(&p->lock);
        c = ext_clock_locked(p);
        pc_mutex_unlock(&p->lock);
    }
    pc_mutex_lock(&p->lock);
    if (!isnan(c)) p->last_pos = c;
    double r = p->last_pos - p->item_offset;   // relative to the file being heard
    pc_mutex_unlock(&p->lock);
    if (r < 0) r = 0;
    if (p->info.duration > 0 && r > p->info.duration) r = p->info.duration;
    return r;
}

void pc_player_set_av_offset(PCPlayer *p, double seconds) { atomic_store(&p->av_offset, seconds); }

void pc_player_get_info(PCPlayer *p, PCInfo *info) {
    pc_mutex_lock(&p->lock);   // gapless swaps it from the presenter thread
    *info = p->info;
    pc_mutex_unlock(&p->lock);
    if (p->vstream >= 0 && p->vdec)
        snprintf(info->video + strlen(info->video), sizeof(info->video) - strlen(info->video),
                 " (%s)", p->video_hw ? pcvb_hw_name() : "software");
}

int pc_player_audio_track_count(PCPlayer *p) { return p->is_open ? p->n_audio : 0; }
int pc_player_current_audio_track(PCPlayer *p) { return p->cur_track; }

void pc_player_audio_track_name(PCPlayer *p, int index, char *buf, int len) {
    buf[0] = 0;
    if (!p->is_open || index < 0 || index >= p->n_audio) return;
    AVStream *st = p->fmt->streams[p->audio_tracks[index]];
    char desc[200];
    describe_audio_stream(st, desc, sizeof(desc));
    const char *lang = meta(st->metadata, "language");
    const char *title = meta(st->metadata, "title");
    snprintf(buf, len, "%d: %s%s%s%s%s", index + 1, lang ? lang : "", lang ? " - " : "", desc,
             title ? " - " : "", title ? title : "");
}

int pc_player_subtitle_track_count(PCPlayer *p) { return p->is_open ? atomic_load(&p->n_sub) : 0; }
int pc_player_current_subtitle_track(PCPlayer *p) { return p->is_open ? atomic_load(&p->cur_sub) : -1; }

void pc_player_subtitle_track_name(PCPlayer *p, int index, char *buf, int len) {
    if (len > 0) buf[0] = 0;
    if (p->is_open && index >= 0 && index < atomic_load(&p->n_sub)) snprintf(buf, len, "%s", p->subt[index].name);
}

int pc_player_subtitle_track_flags(PCPlayer *p, int index) {
    return p->is_open && index >= 0 && index < atomic_load(&p->n_sub) ? p->subt[index].flags : 0;
}

void pc_player_subtitle_track_language(PCPlayer *p, int index, char *buf, int len) {
    if (len > 0) buf[0] = 0;
    if (p->is_open && index >= 0 && index < atomic_load(&p->n_sub)) snprintf(buf, len, "%s", p->subt[index].lang);
}

void pc_player_set_subtitle_track(PCPlayer *p, int index) {
    if (!p->is_open) return;
    if (index < -1 || index >= atomic_load(&p->n_sub)) index = -1;
    atomic_store(&p->cur_sub, index);
}

int pc_player_add_subtitle_file(PCPlayer *p, const char *path, const char *name) {
    int n = atomic_load(&p->n_sub);
    if (!p->is_open || !path || n >= MAX_SUB_TRACKS) return -1;
    char err[256];
    PCSubs *store = subs_create();
    if (!store || subs_load_file(store, path, err, sizeof(err)) < 0) { subs_destroy(store); return -1; }
    p->subt[n].stream = -1;
    p->subt[n].flags = PC_SUBFLAG_EXTERNAL;
    p->subt[n].path = av_strdup(path);
    p->subt[n].dec = NULL;
    p->subt[n].store = store;
    p->subt[n].lang[0] = 0;
    if (!name) {
        const char *slash = strrchr(path, '/'), *bslash = strrchr(path, '\\');
        const char *sep = slash > bslash ? slash : bslash;
        name = sep ? sep + 1 : path;
    }
    snprintf(p->subt[n].name, sizeof(p->subt[n].name), "%d: %s", n + 1, name);
    atomic_store(&p->n_sub, n + 1);   // published after the entry is complete
    return n;
}

static PCSubs *current_store(PCPlayer *p, int *track) {
    *track = p->is_open ? atomic_load(&p->cur_sub) : -1;
    return *track >= 0 && *track < atomic_load(&p->n_sub) ? p->subt[*track].store : NULL;
}

void pc_player_subtitle_state(PCPlayer *p, PCSubtitleState *st) {
    memset(st, 0, sizeof(*st));
    int track;
    PCSubs *store = current_store(p, &track);
    if (store) {
        double t = video_clock(p);
        if (isnan(t)) {
            pc_mutex_lock(&p->lock);
            t = p->last_pos;
            pc_mutex_unlock(&p->lock);
        }
        subs_state(store, t, st);
    }
    // one serial across track switches (each store counts its own)
    if (track != p->sub_last_track || st->serial != p->sub_last_serial) {
        p->sub_last_track = track;
        p->sub_last_serial = st->serial;
        p->sub_serial++;
    }
    st->serial = p->sub_serial;
}

int pc_player_subtitle_text(PCPlayer *p, char *buf, int len) {
    int track;
    PCSubs *store = current_store(p, &track);
    if (len > 0) buf[0] = 0;
    if (!store) return p->sub_serial;
    int s = subs_text(store, buf, len);
    return track == p->sub_last_track && s == p->sub_last_serial ? p->sub_serial : -1;
}

int pc_player_subtitle_bitmap(PCPlayer *p, uint8_t *bgra, int len) {
    int track;
    PCSubs *store = current_store(p, &track);
    if (!store) return p->sub_serial;
    int s = subs_bitmap(store, bgra, len);
    return track == p->sub_last_track && s == p->sub_last_serial ? p->sub_serial : -1;
}

const uint8_t *pc_player_cover_art(PCPlayer *p, int *size) {
    *size = p->art_size;
    return p->art;
}

void pc_player_debug(PCPlayer *p) {
    if (!p->is_open) return;
    fprintf(stderr, "[player] aq=%d vq=%d fq=%d serial a=%d v=%d eof d=%d a=%d v=%d target=%.3f next_pts=%.3f\n",
            p->aq.count, p->vq.count, fq_count(&p->fq), atomic_load(&p->aq.serial), atomic_load(&p->vq.serial),
            atomic_load(&p->demux_eof), atomic_load(&p->audio_eof), atomic_load(&p->video_eof),
            atomic_load(&p->seek_target), p->next_pts);
    pc_out_debug(p->out);
}
