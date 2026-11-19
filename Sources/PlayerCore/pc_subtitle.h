// PORTABLE: decoded subtitle events (text or bitmap) on the playback timeline, with a
// timed lookup for what should be on screen. No OS code: FFmpeg + pc_platform.h only.
#ifndef PC_SUBTITLE_H
#define PC_SUBTITLE_H

#include "playercore.h"
#include <libavcodec/avcodec.h>

typedef struct PCSubs PCSubs;

PCSubs *subs_create(void);
void subs_destroy(PCSubs *s);
void subs_clear(PCSubs *s);

// Adds a decoded subtitle. `start`/`end` are seconds on the playback timeline (end INFINITY
// = until the next subtitle replaces it, as bitmap formats do). An AVSubtitle with no rects
// clears what is showing at `start`. ref_w/ref_h: the canvas bitmap rects are placed on.
void subs_add(PCSubs *s, const AVSubtitle *sub, double start, double end, int ref_w, int ref_h);

// Drops events that ended before `t` (keeps memory bounded for long bitmap tracks).
void subs_prune(PCSubs *s, double t);

// Decodes a whole subtitle file (.srt, .ass/.ssa, .vtt, ...; text must be UTF-8) into `s`,
// replacing its contents. Returns the number of events, or -1 (err filled).
int subs_load_file(PCSubs *s, const char *path, char *err, int errlen);

// What is on screen at time `t` (see PCSubtitleState in playercore.h).
void subs_state(PCSubs *s, double t, PCSubtitleState *st);
// Copy the current text / premultiplied BGRA bitmap; return the serial they belong to.
int subs_text(PCSubs *s, char *buf, int len);
int subs_bitmap(PCSubs *s, uint8_t *bgra, int len);

#endif
