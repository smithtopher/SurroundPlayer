// PORTABLE: subtitle event store and lookup (see pc_subtitle.h).
#include "pc_subtitle.h"
#include "pc_platform.h"

#include <libavformat/avformat.h>
#include <libavutil/mem.h>

#include <math.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    double start, end;
    uint64_t id;
    char *text;                 // UTF-8, plain (ASS markup removed); NULL for bitmaps
    uint8_t *bgra;              // premultiplied BGRA, w*h*4; NULL for text
    int x, y, w, h, ref_w, ref_h;
} SubEvent;

struct PCSubs {
    pc_mutex lock;
    SubEvent *ev;
    int n, cap;
    uint64_t next_id;
    // what is showing (rebuilt when the set of active events changes)
    uint64_t key;
    int active_count;
    PCSubtitleState cur;
    char *cur_text;
    uint8_t *cur_bgra;
    int cur_bgra_cap;
};

PCSubs *subs_create(void) {
    PCSubs *s = calloc(1, sizeof(*s));
    if (!s) return NULL;
    pc_mutex_init(&s->lock);
    s->next_id = 1;
    return s;
}

static void free_event(SubEvent *e) {
    free(e->text);
    free(e->bgra);
}

static void clear_locked(PCSubs *s) {
    for (int i = 0; i < s->n; i++) free_event(&s->ev[i]);
    s->n = 0;
}

void subs_clear(PCSubs *s) {
    if (!s) return;
    pc_mutex_lock(&s->lock);
    clear_locked(s);
    pc_mutex_unlock(&s->lock);
}

void subs_destroy(PCSubs *s) {
    if (!s) return;
    clear_locked(s);
    free(s->ev);
    free(s->cur_text);
    free(s->cur_bgra);
    pc_mutex_destroy(&s->lock);
    free(s);
}

// ASS dialogue as FFmpeg's decoders produce it: "ReadOrder,Layer,Style,Name,MarginL,MarginR,
// MarginV,Effect,Text". Returns the Text field with override blocks removed, \N -> newline,
// \h -> space. Plain text rects are cleaned the same way.
static char *clean_text(const char *src, int is_ass) {
    if (!src) return NULL;
    if (is_ass) {
        for (int commas = 0; *src && commas < 8; src++)
            if (*src == ',') commas++;
    }
    size_t n = strlen(src);
    char *out = malloc(n + 1);
    if (!out) return NULL;
    size_t o = 0;
    int depth = 0;
    for (size_t i = 0; i < n; i++) {
        char c = src[i];
        if (c == '{') { depth++; continue; }
        if (c == '}' && depth > 0) { depth--; continue; }
        if (depth) continue;
        if (c == '\\' && i + 1 < n && (src[i + 1] == 'N' || src[i + 1] == 'n')) { out[o++] = '\n'; i++; continue; }
        if (c == '\\' && i + 1 < n && src[i + 1] == 'h') { out[o++] = ' '; i++; continue; }
        if (c == '\r') continue;
        out[o++] = c;
    }
    // trim surrounding whitespace / newlines
    while (o > 0 && (out[o - 1] == '\n' || out[o - 1] == ' ')) o--;
    out[o] = 0;
    size_t lead = 0;
    while (out[lead] == '\n' || out[lead] == ' ') lead++;
    if (lead) memmove(out, out + lead, o - lead + 1);
    if (!out[0]) { free(out); return NULL; }
    return out;
}

static uint8_t *bitmap_bgra(const AVSubtitleRect *r) {
    if (r->w <= 0 || r->h <= 0 || !r->data[0] || !r->data[1]) return NULL;
    uint8_t *dst = malloc((size_t)r->w * r->h * 4);
    if (!dst) return NULL;
    const uint32_t *pal = (const uint32_t *)r->data[1];   // 0xAARRGGBB
    for (int y = 0; y < r->h; y++) {
        const uint8_t *src = r->data[0] + (size_t)y * r->linesize[0];
        uint8_t *d = dst + (size_t)y * r->w * 4;
        for (int x = 0; x < r->w; x++) {
            uint32_t c = src[x] < r->nb_colors ? pal[src[x]] : 0;
            unsigned a = c >> 24;
            d[x * 4 + 0] = (uint8_t)(((c & 0xFF) * a + 127) / 255);
            d[x * 4 + 1] = (uint8_t)((((c >> 8) & 0xFF) * a + 127) / 255);
            d[x * 4 + 2] = (uint8_t)((((c >> 16) & 0xFF) * a + 127) / 255);
            d[x * 4 + 3] = (uint8_t)a;
        }
    }
    return dst;
}

static int push_locked(PCSubs *s, SubEvent e) {
    if (s->n == s->cap) {
        int cap = s->cap ? s->cap * 2 : 256;
        SubEvent *n = realloc(s->ev, (size_t)cap * sizeof(*n));
        if (!n) return -1;
        s->ev = n;
        s->cap = cap;
    }
    // keep sorted by start (events mostly arrive in order: walk back from the end)
    int i = s->n;
    while (i > 0 && s->ev[i - 1].start > e.start) i--;
    memmove(&s->ev[i + 1], &s->ev[i], (size_t)(s->n - i) * sizeof(*s->ev));
    e.id = s->next_id++;
    s->ev[i] = e;
    s->n++;
    return 0;
}

static int is_duplicate_locked(PCSubs *s, double start, const char *text, int x, int y, int w, int h) {
    for (int i = s->n - 1; i >= 0 && s->ev[i].start > start - 0.5; i--) {
        SubEvent *e = &s->ev[i];
        if (fabs(e->start - start) > 0.001) continue;
        if (text && e->text && !strcmp(text, e->text)) return 1;
        if (!text && e->bgra && e->x == x && e->y == y && e->w == w && e->h == h) return 1;
    }
    return 0;
}

void subs_add(PCSubs *s, const AVSubtitle *sub, double start, double end, int ref_w, int ref_h) {
    if (!s || !sub || isnan(start)) return;
    pc_mutex_lock(&s->lock);
    // a new display replaces open-ended ones (bitmap formats signal their end this way)
    for (int i = 0; i < s->n; i++)
        if (isinf(s->ev[i].end) && s->ev[i].start < start - 0.0005) s->ev[i].end = start;
    if (!(end > start)) end = INFINITY;
    // text rects of one subtitle become one event
    char *joined = NULL;
    size_t jlen = 0;
    for (unsigned r = 0; r < sub->num_rects; r++) {
        const AVSubtitleRect *rect = sub->rects[r];
        if (rect->type == SUBTITLE_BITMAP) {
            if (is_duplicate_locked(s, start, NULL, rect->x, rect->y, rect->w, rect->h)) continue;
            uint8_t *bgra = bitmap_bgra(rect);
            if (!bgra) continue;
            SubEvent e = { .start = start, .end = end, .bgra = bgra, .x = rect->x, .y = rect->y,
                           .w = rect->w, .h = rect->h, .ref_w = ref_w, .ref_h = ref_h };
            if (push_locked(s, e) < 0) free(bgra);
        } else {
            char *t = clean_text(rect->ass ? rect->ass : rect->text, rect->ass != NULL);
            if (!t) continue;
            size_t tl = strlen(t);
            char *j = realloc(joined, jlen + tl + 2);
            if (!j) { free(t); continue; }
            joined = j;
            if (jlen) joined[jlen++] = '\n';
            memcpy(joined + jlen, t, tl + 1);
            jlen += tl;
            free(t);
        }
    }
    if (joined) {
        if (is_duplicate_locked(s, start, joined, 0, 0, 0, 0)) free(joined);
        else {
            SubEvent e = { .start = start, .end = end, .text = joined, .ref_w = ref_w, .ref_h = ref_h };
            if (push_locked(s, e) < 0) free(joined);
        }
    }
    pc_mutex_unlock(&s->lock);
}

void subs_prune(PCSubs *s, double t) {
    if (!s) return;
    pc_mutex_lock(&s->lock);
    int o = 0;
    for (int i = 0; i < s->n; i++) {
        if (s->ev[i].end < t) free_event(&s->ev[i]);
        else s->ev[o++] = s->ev[i];
    }
    s->n = o;
    pc_mutex_unlock(&s->lock);
}

int subs_load_file(PCSubs *s, const char *path, char *err, int errlen) {
    AVFormatContext *fmt = NULL;
    AVCodecContext *dec = NULL;
    AVPacket *pkt = NULL;
    int count = -1;
    if (avformat_open_input(&fmt, path, NULL, NULL) < 0) { snprintf(err, errlen, "Cannot open %s", path); goto done; }
    if (avformat_find_stream_info(fmt, NULL) < 0) { snprintf(err, errlen, "Cannot read %s", path); goto done; }
    int si = av_find_best_stream(fmt, AVMEDIA_TYPE_SUBTITLE, -1, -1, NULL, 0);
    if (si < 0) { snprintf(err, errlen, "No subtitles in %s", path); goto done; }
    AVStream *st = fmt->streams[si];
    const AVCodec *c = avcodec_find_decoder(st->codecpar->codec_id);
    if (!c || !(dec = avcodec_alloc_context3(c))) { snprintf(err, errlen, "Unsupported subtitle format"); goto done; }
    avcodec_parameters_to_context(dec, st->codecpar);
    dec->pkt_timebase = st->time_base;
    if (avcodec_open2(dec, c, NULL) < 0) { snprintf(err, errlen, "Cannot decode %s", path); goto done; }
    subs_clear(s);
    pkt = av_packet_alloc();
    count = 0;
    while (pkt && av_read_frame(fmt, pkt) >= 0) {
        if (pkt->stream_index == si) {
            AVSubtitle sub;
            int got = 0;
            if (avcodec_decode_subtitle2(dec, &sub, &got, pkt) >= 0 && got) {
                double base = sub.pts != AV_NOPTS_VALUE ? sub.pts / (double)AV_TIME_BASE
                            : pkt->pts != AV_NOPTS_VALUE ? pkt->pts * av_q2d(st->time_base) : NAN;
                double start = base + sub.start_display_time / 1000.0;
                double end = sub.end_display_time > sub.start_display_time && sub.end_display_time != UINT32_MAX
                                 ? base + sub.end_display_time / 1000.0
                             : pkt->duration > 0 ? start + pkt->duration * av_q2d(st->time_base) : INFINITY;
                subs_add(s, &sub, start, end, dec->width, dec->height);
                count++;
                avsubtitle_free(&sub);
            }
        }
        av_packet_unref(pkt);
    }
done:
    av_packet_free(&pkt);
    avcodec_free_context(&dec);
    avformat_close_input(&fmt);
    return count;
}

void subs_state(PCSubs *s, double t, PCSubtitleState *st) {
    memset(st, 0, sizeof(*st));
    if (!s) return;
    pc_mutex_lock(&s->lock);
    uint64_t key = 1469598103934665603ULL;
    int count = 0, any_bitmap = 0;
    for (int i = 0; i < s->n && s->ev[i].start <= t; i++) {
        SubEvent *e = &s->ev[i];
        if (t >= e->end) continue;
        key = (key ^ e->id) * 1099511628211ULL;
        count++;
        if (e->bgra) any_bitmap = 1;
    }
    if (key != s->key || count != s->active_count) {
        s->key = key;
        s->active_count = count;
        int serial = s->cur.serial + 1;
        memset(&s->cur, 0, sizeof(s->cur));
        s->cur.serial = serial;
        free(s->cur_text);
        s->cur_text = NULL;
        if (count && any_bitmap) {
            // union of the active bitmap rects, composited in order
            int x0 = INT32_MAX, y0 = INT32_MAX, x1 = INT32_MIN, y1 = INT32_MIN, rw = 0, rh = 0;
            for (int i = 0; i < s->n && s->ev[i].start <= t; i++) {
                SubEvent *e = &s->ev[i];
                if (t >= e->end || !e->bgra) continue;
                if (e->x < x0) x0 = e->x;
                if (e->y < y0) y0 = e->y;
                if (e->x + e->w > x1) x1 = e->x + e->w;
                if (e->y + e->h > y1) y1 = e->y + e->h;
                if (!rw) { rw = e->ref_w; rh = e->ref_h; }
            }
            int w = x1 - x0, h = y1 - y0;
            size_t need = w > 0 && h > 0 ? (size_t)w * h * 4 : 0;
            if (need > (size_t)s->cur_bgra_cap) {
                free(s->cur_bgra);
                s->cur_bgra = malloc(need);
                s->cur_bgra_cap = s->cur_bgra ? (int)need : 0;
            }
            if (need && s->cur_bgra) {
                memset(s->cur_bgra, 0, need);
                for (int i = 0; i < s->n && s->ev[i].start <= t; i++) {
                    SubEvent *e = &s->ev[i];
                    if (t >= e->end || !e->bgra) continue;
                    for (int y = 0; y < e->h; y++) {
                        uint8_t *d = s->cur_bgra + ((size_t)(e->y - y0 + y) * w + (e->x - x0)) * 4;
                        const uint8_t *src = e->bgra + (size_t)y * e->w * 4;
                        for (int x = 0; x < e->w; x++) {   // premultiplied "over"
                            unsigned a = src[x * 4 + 3];
                            for (int k = 0; k < 4; k++)
                                d[x * 4 + k] = (uint8_t)(src[x * 4 + k] + d[x * 4 + k] * (255 - a) / 255);
                        }
                    }
                }
                s->cur.kind = PC_SUB_BITMAP;
                s->cur.x = x0; s->cur.y = y0; s->cur.w = w; s->cur.h = h;
                s->cur.ref_w = rw; s->cur.ref_h = rh;
            }
        } else if (count) {
            size_t len = 0;
            for (int i = 0; i < s->n && s->ev[i].start <= t; i++)
                if (t < s->ev[i].end && s->ev[i].text) len += strlen(s->ev[i].text) + 1;
            s->cur_text = malloc(len + 1);
            if (s->cur_text) {
                size_t o = 0;
                for (int i = 0; i < s->n && s->ev[i].start <= t; i++) {
                    SubEvent *e = &s->ev[i];
                    if (t >= e->end || !e->text) continue;
                    if (o) s->cur_text[o++] = '\n';
                    size_t l = strlen(e->text);
                    memcpy(s->cur_text + o, e->text, l);
                    o += l;
                }
                s->cur_text[o] = 0;
                s->cur.kind = PC_SUB_TEXT;
                s->cur.text_len = (int)o;
            }
        }
    }
    *st = s->cur;
    pc_mutex_unlock(&s->lock);
}

int subs_text(PCSubs *s, char *buf, int len) {
    if (len > 0) buf[0] = 0;
    if (!s) return 0;
    pc_mutex_lock(&s->lock);
    if (s->cur.kind == PC_SUB_TEXT && s->cur_text && len > 0) snprintf(buf, len, "%s", s->cur_text);
    int serial = s->cur.serial;
    pc_mutex_unlock(&s->lock);
    return serial;
}

int subs_bitmap(PCSubs *s, uint8_t *bgra, int len) {
    if (!s) return 0;
    pc_mutex_lock(&s->lock);
    size_t need = (size_t)s->cur.w * s->cur.h * 4;
    if (s->cur.kind == PC_SUB_BITMAP && s->cur_bgra && need <= (size_t)len) memcpy(bgra, s->cur_bgra, need);
    int serial = s->cur.serial;
    pc_mutex_unlock(&s->lock);
    return serial;
}
