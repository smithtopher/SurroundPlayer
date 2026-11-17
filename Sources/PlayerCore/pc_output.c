// CoreAudio HAL output.
//
// Bitstream path (equivalent of Kodi's WASAPI exclusive / AESinkDARWINOSX passthrough):
//   1. take exclusive ownership of the device (kAudioDevicePropertyHogMode)
//   2. disable mixing (kAudioDevicePropertySupportsMixing = 0) so the HAL does no
//      volume / mixing / dithering on our samples
//   3. switch the stream's physical format to a digital AC3 format if the device
//      offers one, otherwise to 2ch 16-bit integer LPCM @ 48 kHz (what HDMI exposes)
//   4. feed IEC 61937 bursts as if they were 16-bit stereo PCM. If the HAL insists on
//      a float virtual format, s16 -> float (/32768) round-trips bit exactly.
//   5. on close restore the original format, mixing and release hog mode.
#include "pc_output.h"
#include "playercore.h"

#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>
#include <math.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define MAX_MARKERS 1024

typedef struct { int64_t pos; double pts; } PtsMarker;

enum { BS_BOUNDARY = 0, BS_REAL = 1, BS_SILENCE = 2 };

struct PCOut {
    // device
    AudioDeviceID dev;
    AudioStreamID stream;
    int stream_buffer;              // index in the IOProc buffer list
    AudioDeviceIOProcID procid;
    int bitstream;
    int hogged, mixing_changed, phys_changed, virt_changed;
    AudioStreamBasicDescription orig_phys, orig_virt;
    // virtual (IOProc buffer) format
    int virt_float, virt_bits, virt_channels;
    // ring format
    int channels;
    double rate;
    double bps;                     // ring bytes per second
    int ring_bpf;                   // ring bytes per frame
    double latency;                 // seconds, device + stream
    // ring buffer (single producer / IOProc consumer)
    uint8_t *ring;
    int64_t cap;
    _Atomic int64_t wpos, rpos;
    _Atomic int64_t flush_target;
    _Atomic int flush_gen, seen_gen;
    _Atomic int paused, running;
    // bitstream burst state (IOProc only)
    int bs_state, bs_phase;
    uint8_t silence[PC_AC3_BURST_BYTES];
    _Atomic int have_silence;
    // clock record, written by IOProc (seqlock)
    _Atomic uint32_t seq;
    _Atomic int64_t rec_pos, rec_len;
    _Atomic uint64_t rec_host;
    // pts markers
    pthread_mutex_t mlock;
    PtsMarker markers[MAX_MARKERS];
    int mstart, mcount;
    double frozen;
    // file sink
    FILE *file;
    double file_pts;
    char desc[512];
};

// ---------------------------------------------------------------------------
// property helpers

static AudioObjectPropertyAddress addr(AudioObjectPropertySelector s, AudioObjectPropertyScope sc) {
    AudioObjectPropertyAddress a = { s, sc, kAudioObjectPropertyElementMain };
    return a;
}

static OSStatus getp(AudioObjectID o, AudioObjectPropertySelector s, AudioObjectPropertyScope sc, UInt32 size, void *data) {
    AudioObjectPropertyAddress a = addr(s, sc);
    return AudioObjectGetPropertyData(o, &a, 0, NULL, &size, data);
}

static OSStatus setp(AudioObjectID o, AudioObjectPropertySelector s, AudioObjectPropertyScope sc, UInt32 size, const void *data) {
    AudioObjectPropertyAddress a = addr(s, sc);
    return AudioObjectSetPropertyData(o, &a, 0, NULL, size, data);
}

static void *getp_alloc(AudioObjectID o, AudioObjectPropertySelector s, AudioObjectPropertyScope sc, UInt32 *size_out) {
    AudioObjectPropertyAddress a = addr(s, sc);
    UInt32 size = 0;
    if (AudioObjectGetPropertyDataSize(o, &a, 0, NULL, &size) != noErr || size == 0) return NULL;
    void *buf = malloc(size);
    if (AudioObjectGetPropertyData(o, &a, 0, NULL, &size, buf) != noErr) { free(buf); return NULL; }
    if (size_out) *size_out = size;
    return buf;
}

static void cfstr_to_c(CFStringRef s, char *buf, int len) {
    buf[0] = 0;
    if (s) CFStringGetCString(s, buf, len, kCFStringEncodingUTF8);
}

static void device_name(AudioDeviceID d, char *buf, int len) {
    CFStringRef s = NULL;
    getp(d, kAudioObjectPropertyName, kAudioObjectPropertyScopeGlobal, sizeof(s), &s);
    cfstr_to_c(s, buf, len);
    if (s) CFRelease(s);
}

static UInt32 device_transport(AudioDeviceID d) {
    UInt32 t = 0;
    getp(d, kAudioDevicePropertyTransportType, kAudioObjectPropertyScopeGlobal, sizeof(t), &t);
    return t;
}

static const char *transport_name(UInt32 t) {
    switch (t) {
    case kAudioDeviceTransportTypeHDMI: return "HDMI";
    case kAudioDeviceTransportTypeDisplayPort: return "DisplayPort";
    case kAudioDeviceTransportTypeBuiltIn: return "built-in";
    case kAudioDeviceTransportTypeUSB: return "USB";
    case kAudioDeviceTransportTypeBluetooth: case kAudioDeviceTransportTypeBluetoothLE: return "Bluetooth";
    case kAudioDeviceTransportTypeAirPlay: return "AirPlay";
    case kAudioDeviceTransportTypeThunderbolt: return "Thunderbolt";
    case kAudioDeviceTransportTypeVirtual: return "virtual";
    case kAudioDeviceTransportTypeAggregate: return "aggregate";
    default: return "other";
    }
}

static AudioStreamID first_output_stream(AudioDeviceID d) {
    UInt32 size = 0;
    AudioStreamID *ids = getp_alloc(d, kAudioDevicePropertyStreams, kAudioObjectPropertyScopeOutput, &size);
    AudioStreamID s = 0;
    if (ids && size >= sizeof(AudioStreamID)) s = ids[0];
    free(ids);
    return s;
}

static int is_compressed_format(UInt32 id) {
    return id == kAudioFormat60958AC3 || id == kAudioFormatAC3 || id == 'IAC3';
}

static int output_channel_count(AudioDeviceID d) {
    UInt32 size = 0;
    AudioBufferList *bl = getp_alloc(d, kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeOutput, &size);
    int ch = 0;
    if (bl) {
        for (UInt32 i = 0; i < bl->mNumberBuffers; i++) ch += bl->mBuffers[i].mNumberChannels;
        free(bl);
    }
    return ch;
}

static int device_has_compressed(AudioDeviceID d) {
    AudioStreamID s = first_output_stream(d);
    if (!s) return 0;
    UInt32 size = 0;
    AudioStreamRangedDescription *f = getp_alloc(s, kAudioStreamPropertyAvailablePhysicalFormats, kAudioObjectPropertyScopeGlobal, &size);
    int found = 0;
    if (f) {
        for (UInt32 i = 0; i < size / sizeof(*f); i++)
            if (is_compressed_format(f[i].mFormat.mFormatID)) found = 1;
        free(f);
    }
    return found;
}

static AudioDeviceID default_output_device(void) {
    AudioDeviceID d = 0;
    getp(kAudioObjectSystemObject, kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, sizeof(d), &d);
    return d;
}

static AudioDeviceID device_for_uid(const char *uid) {
    if (!uid || !uid[0]) return default_output_device();
    CFStringRef s = CFStringCreateWithCString(NULL, uid, kCFStringEncodingUTF8);
    AudioDeviceID d = kAudioObjectUnknown;
    AudioObjectPropertyAddress a = addr(kAudioHardwarePropertyTranslateUIDToDevice, kAudioObjectPropertyScopeGlobal);
    UInt32 size = sizeof(d);
    AudioObjectGetPropertyData(kAudioObjectSystemObject, &a, sizeof(s), &s, &size, &d);
    CFRelease(s);
    if (d == kAudioObjectUnknown) return 0;
    return d;
}

int pc_list_output_devices(PCDevice *out, int max) {
    UInt32 size = 0;
    AudioDeviceID *ids = getp_alloc(kAudioObjectSystemObject, kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, &size);
    if (!ids) return 0;
    AudioDeviceID def = default_output_device();
    int n = 0;
    for (UInt32 i = 0; i < size / sizeof(AudioDeviceID) && n < max; i++) {
        int ch = output_channel_count(ids[i]);
        if (ch <= 0) continue;
        PCDevice *d = &out[n++];
        memset(d, 0, sizeof(*d));
        CFStringRef uid = NULL;
        getp(ids[i], kAudioDevicePropertyDeviceUID, kAudioObjectPropertyScopeGlobal, sizeof(uid), &uid);
        cfstr_to_c(uid, d->uid, sizeof(d->uid));
        if (uid) CFRelease(uid);
        device_name(ids[i], d->name, sizeof(d->name));
        d->output_channels = ch;
        UInt32 t = device_transport(ids[i]);
        d->is_hdmi = t == kAudioDeviceTransportTypeHDMI || t == kAudioDeviceTransportTypeDisplayPort;
        d->has_compressed_formats = device_has_compressed(ids[i]);
        d->is_digital = d->is_hdmi || d->has_compressed_formats;
        d->is_default = ids[i] == def;
    }
    free(ids);
    return n;
}

// ---------------------------------------------------------------------------
// format selection

static int range_has_48k(const AudioStreamRangedDescription *r) {
    if (r->mFormat.mSampleRate == 48000.0) return 1;
    return r->mSampleRateRange.mMinimum <= 48000.0 && r->mSampleRateRange.mMaximum >= 48000.0;
}

// Score a physical format for carrying IEC 61937 AC3; higher is better, <0 unusable.
static int score_bitstream_format(const AudioStreamRangedDescription *r) {
    const AudioStreamBasicDescription *f = &r->mFormat;
    if (!range_has_48k(r)) return -1;
    if (is_compressed_format(f->mFormatID)) return 100;   // true digital AC3 format (optical/S/PDIF)
    if (f->mFormatID != kAudioFormatLinearPCM) return -1;
    if (f->mChannelsPerFrame != 2) return -1;
    if (f->mFormatFlags & kAudioFormatFlagIsFloat) return -1;
    if (f->mFormatFlags & kAudioFormatFlagIsNonInterleaved) return -1;
    if (f->mBitsPerChannel == 16) return 90;               // HDMI: 2ch 16-bit 48k LPCM
    if (f->mBitsPerChannel == 24 || f->mBitsPerChannel == 32) return 50; // carries s16 in the MSBs
    return -1;
}

static int same_format(const AudioStreamBasicDescription *a, const AudioStreamBasicDescription *b) {
    return a->mFormatID == b->mFormatID && a->mSampleRate == b->mSampleRate &&
           a->mChannelsPerFrame == b->mChannelsPerFrame && a->mBitsPerChannel == b->mBitsPerChannel;
}

static int set_physical_format(AudioStreamID s, const AudioStreamBasicDescription *want) {
    AudioStreamBasicDescription cur;
    if (getp(s, kAudioStreamPropertyPhysicalFormat, kAudioObjectPropertyScopeGlobal, sizeof(cur), &cur) == noErr &&
        same_format(&cur, want))
        return 0;
    if (setp(s, kAudioStreamPropertyPhysicalFormat, kAudioObjectPropertyScopeGlobal, sizeof(*want), want) != noErr)
        return -1;
    // The change is asynchronous; wait for the HAL to report it (Kodi does the same).
    for (int i = 0; i < 200; i++) {
        if (getp(s, kAudioStreamPropertyPhysicalFormat, kAudioObjectPropertyScopeGlobal, sizeof(cur), &cur) == noErr &&
            same_format(&cur, want))
            return 0;
        usleep(10000);
    }
    return -1;
}

static void fourcc_str(UInt32 id, char out[5]) {
    out[0] = (char)(id >> 24); out[1] = (char)(id >> 16); out[2] = (char)(id >> 8); out[3] = (char)id; out[4] = 0;
}

// ---------------------------------------------------------------------------
// IOProc

static void store_record(PCOut *o, int64_t pos, int64_t len, uint64_t host) {
    uint32_t s = atomic_load_explicit(&o->seq, memory_order_relaxed);
    atomic_store_explicit(&o->seq, s + 1, memory_order_release);
    atomic_store_explicit(&o->rec_pos, pos, memory_order_relaxed);
    atomic_store_explicit(&o->rec_len, len, memory_order_relaxed);
    atomic_store_explicit(&o->rec_host, host, memory_order_relaxed);
    atomic_store_explicit(&o->seq, s + 2, memory_order_release);
}

// Write `frames` stereo s16 frames from src into the device buffer in its virtual format.
static void emit_s16(PCOut *o, uint8_t *dst, const int16_t *src, int frames) {
    int n = frames * 2;
    if (!o->virt_float && o->virt_bits == 16) {
        memcpy(dst, src, (size_t)n * 2);
    } else if (o->virt_float) {
        float *d = (float *)dst;
        for (int i = 0; i < n; i++) d[i] = (float)src[i] * (1.0f / 32768.0f);
    } else { // 32-bit integer container
        int32_t *d = (int32_t *)dst;
        for (int i = 0; i < n; i++) d[i] = (int32_t)src[i] * 65536;
    }
}

static void emit_ring(PCOut *o, uint8_t *dst, int64_t pos, int frames) {
    int vbpf = o->virt_channels * (o->virt_bits / 8);
    while (frames > 0) {
        int64_t off = pos % o->cap;
        int chunk = (int)((o->cap - off) / o->ring_bpf);
        if (chunk > frames) chunk = frames;
        if (o->bitstream) emit_s16(o, dst, (const int16_t *)(o->ring + off), chunk);
        else memcpy(dst, o->ring + off, (size_t)chunk * o->ring_bpf);
        dst += chunk * vbpf;
        pos += (int64_t)chunk * o->ring_bpf;
        frames -= chunk;
    }
}

static OSStatus ioproc(AudioObjectID dev, const AudioTimeStamp *now, const AudioBufferList *in,
                       const AudioTimeStamp *intime, AudioBufferList *out, const AudioTimeStamp *outtime, void *ud) {
    (void)dev; (void)in; (void)intime;
    PCOut *o = ud;
    for (UInt32 i = 0; i < out->mNumberBuffers; i++)
        if ((int)i != o->stream_buffer) memset(out->mBuffers[i].mData, 0, out->mBuffers[i].mDataByteSize);
    if (o->stream_buffer >= (int)out->mNumberBuffers) return noErr;

    AudioBuffer *b = &out->mBuffers[o->stream_buffer];
    int vbpf = o->virt_channels * (o->virt_bits / 8);
    int frames = (int)(b->mDataByteSize / vbpf);
    uint8_t *dst = b->mData;

    int g = atomic_load_explicit(&o->flush_gen, memory_order_acquire);
    int64_t r = atomic_load_explicit(&o->rpos, memory_order_relaxed);
    if (g != atomic_load_explicit(&o->seen_gen, memory_order_relaxed)) {
        int64_t t = atomic_load_explicit(&o->flush_target, memory_order_relaxed);
        if (t > r) r = t;
        if (o->bs_state == BS_REAL) { o->bs_state = BS_BOUNDARY; o->bs_phase = 0; }
        atomic_store_explicit(&o->seen_gen, g, memory_order_release);
    }
    int64_t w = atomic_load_explicit(&o->wpos, memory_order_acquire);
    int paused = atomic_load_explicit(&o->paused, memory_order_relaxed);

    int done = 0, first_real_frame = -1, real_contig = 1;
    int64_t first_real_pos = 0, real_len = 0;

    if (o->bitstream) {
        // Bursts are always written whole, so we only ever switch between real data
        // and silence on burst boundaries; the receiver never sees a torn frame.
        while (done < frames) {
            int left = frames - done;
            if (o->bs_state == BS_SILENCE) {
                int n = (PC_AC3_BURST_BYTES - o->bs_phase) / 4;
                if (n > left) n = left;
                emit_s16(o, dst + done * vbpf, (const int16_t *)(o->silence + o->bs_phase), n);
                o->bs_phase += n * 4; done += n;
                if (first_real_frame >= 0) real_contig = 0;
                if (o->bs_phase >= PC_AC3_BURST_BYTES) { o->bs_phase = 0; o->bs_state = BS_BOUNDARY; }
            } else if (o->bs_state == BS_REAL) {
                int n = (PC_AC3_BURST_BYTES - o->bs_phase) / 4;
                int avail = (int)((w - r) / 4);
                if (n > left) n = left;
                if (n > avail) n = avail;
                if (n <= 0) { o->bs_state = BS_BOUNDARY; o->bs_phase = 0; continue; }
                emit_ring(o, dst + done * vbpf, r, n);
                if (first_real_frame < 0) { first_real_frame = done; first_real_pos = r; }
                if (real_contig) real_len += n * 4;
                r += n * 4; o->bs_phase += n * 4; done += n;
                if (o->bs_phase >= PC_AC3_BURST_BYTES) { o->bs_phase = 0; o->bs_state = BS_BOUNDARY; }
            } else {
                if (!paused && w - r >= PC_AC3_BURST_BYTES) {
                    o->bs_state = BS_REAL; o->bs_phase = 0;
                } else if (atomic_load_explicit(&o->have_silence, memory_order_acquire)) {
                    o->bs_state = BS_SILENCE; o->bs_phase = 0;
                } else {
                    memset(dst + done * vbpf, 0, (size_t)left * vbpf);
                    done = frames;
                }
            }
        }
    } else {
        int avail = paused ? 0 : (int)((w - r) / o->ring_bpf);
        int n = avail < frames ? avail : frames;
        if (n > 0) {
            emit_ring(o, dst, r, n);
            first_real_frame = 0; first_real_pos = r; real_len = (int64_t)n * o->ring_bpf;
            r += real_len;
        }
        if (n < frames) memset(dst + n * vbpf, 0, (size_t)(frames - n) * vbpf);
    }

    atomic_store_explicit(&o->rpos, r, memory_order_release);

    if (first_real_frame >= 0) {
        uint64_t host = (outtime->mFlags & kAudioTimeStampHostTimeValid) ? outtime->mHostTime : now->mHostTime;
        host += AudioConvertNanosToHostTime((UInt64)((double)first_real_frame / o->rate * 1e9));
        store_record(o, first_real_pos, real_len, host);
    }
    return noErr;
}

// ---------------------------------------------------------------------------
// open / close

static PCOut *alloc_out(void) {
    PCOut *o = calloc(1, sizeof(*o));
    pthread_mutex_init(&o->mlock, NULL);
    o->stream_buffer = 0;
    o->frozen = NAN;
    return o;
}

static void alloc_ring(PCOut *o, double seconds) {
    int64_t cap = (int64_t)(o->bps * seconds);
    if (o->bitstream) cap = (cap / PC_AC3_BURST_BYTES + 1) * PC_AC3_BURST_BYTES;
    else cap -= cap % o->ring_bpf;
    o->cap = cap;
    o->ring = calloc(1, (size_t)cap);
}

static void release_device(PCOut *o) {
    if (!o->dev) return;
    if (o->phys_changed) set_physical_format(o->stream, &o->orig_phys);
    if (o->mixing_changed) {
        UInt32 mix = 1;
        setp(o->dev, kAudioDevicePropertySupportsMixing, kAudioObjectPropertyScopeGlobal, sizeof(mix), &mix);
    }
    if (o->hogged) {
        pid_t pid = -1;
        setp(o->dev, kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, sizeof(pid), &pid);
    }
    o->phys_changed = o->mixing_changed = o->hogged = 0;
}

PCOut *pc_out_open(const char *device_uid, int bitstream, char *err, int errlen) {
    PCOut *o = alloc_out();
    o->bitstream = bitstream;
    o->dev = device_for_uid(device_uid);
    if (!o->dev) { snprintf(err, errlen, "Audio device not found"); goto fail; }
    o->stream = first_output_stream(o->dev);
    if (!o->stream) { snprintf(err, errlen, "Audio device has no output stream"); goto fail; }

    char name[256];
    device_name(o->dev, name, sizeof(name));
    UInt32 transport = device_transport(o->dev);
    getp(o->stream, kAudioStreamPropertyPhysicalFormat, kAudioObjectPropertyScopeGlobal, sizeof(o->orig_phys), &o->orig_phys);
    getp(o->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(o->orig_virt), &o->orig_virt);

    if (bitstream) {
        // Never send IEC 61937 to something that would play it as PCM noise
        // (speakers, headphones, Bluetooth): only HDMI/DisplayPort or devices
        // that advertise a compressed AC3 format (optical / S/PDIF interfaces).
        int digital = transport == kAudioDeviceTransportTypeHDMI ||
                      transport == kAudioDeviceTransportTypeDisplayPort || device_has_compressed(o->dev);
        if (!digital) {
            snprintf(err, errlen, "'%s' is not a digital (HDMI/optical) output", name);
            goto fail;
        }
        // 1. exclusive access
        pid_t hog = -1;
        getp(o->dev, kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, sizeof(hog), &hog);
        if (hog != -1 && hog != getpid()) {
            snprintf(err, errlen, "'%s' is in exclusive use by another application (pid %d)", name, hog);
            goto fail;
        }
        if (hog != getpid()) {
            pid_t me = getpid();
            if (setp(o->dev, kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, sizeof(me), &me) == noErr) {
                o->hogged = 1;
            }
        }
        // 2. no mixing -> the HAL passes our integer samples straight through
        AudioObjectPropertyAddress ma = addr(kAudioDevicePropertySupportsMixing, kAudioObjectPropertyScopeGlobal);
        Boolean settable = false;
        if (AudioObjectHasProperty(o->dev, &ma) && AudioObjectIsPropertySettable(o->dev, &ma, &settable) == noErr && settable) {
            UInt32 mix = 0;
            if (setp(o->dev, kAudioDevicePropertySupportsMixing, kAudioObjectPropertyScopeGlobal, sizeof(mix), &mix) == noErr)
                o->mixing_changed = 1;
        }
        // 3. choose a physical format that can carry IEC 61937
        UInt32 size = 0;
        AudioStreamRangedDescription *fmts = getp_alloc(o->stream, kAudioStreamPropertyAvailablePhysicalFormats, kAudioObjectPropertyScopeGlobal, &size);
        int best = -1, best_score = -1;
        for (UInt32 i = 0; fmts && i < size / sizeof(*fmts); i++) {
            int sc = score_bitstream_format(&fmts[i]);
            if (sc > best_score) { best_score = sc; best = (int)i; }
        }
        if (best < 0) {
            free(fmts);
            snprintf(err, errlen, "'%s' offers no 2-channel 48 kHz integer or AC3 format for bitstreaming", name);
            goto fail;
        }
        AudioStreamBasicDescription want = fmts[best].mFormat;
        free(fmts);
        want.mSampleRate = 48000.0;
        if (set_physical_format(o->stream, &want) != 0) {
            snprintf(err, errlen, "Could not switch '%s' to a bitstream-capable format", name);
            goto fail;
        }
        o->phys_changed = !same_format(&want, &o->orig_phys);
        Float64 sr = 48000.0;
        setp(o->dev, kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, sizeof(sr), &sr);

        // 4. ask for an integer virtual format identical to the physical one (no conversion)
        if (setp(o->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(want), &want) == noErr)
            o->virt_changed = 1;
        AudioStreamBasicDescription virt;
        getp(o->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(virt), &virt);
        o->virt_channels = (int)virt.mChannelsPerFrame;
        o->virt_bits = (int)virt.mBitsPerChannel;
        o->virt_float = (virt.mFormatFlags & kAudioFormatFlagIsFloat) != 0;
        if (is_compressed_format(virt.mFormatID)) { o->virt_float = 0; o->virt_bits = 16; o->virt_channels = 2; }
        if (o->virt_channels != 2 || virt.mSampleRate != 48000.0 ||
            !(o->virt_bits == 16 || o->virt_bits == 32) || (o->virt_float && o->virt_bits != 32)) {
            snprintf(err, errlen, "Unsupported stream format on '%s' (%u ch, %u bit, %.0f Hz)",
                     name, (unsigned)virt.mChannelsPerFrame, (unsigned)virt.mBitsPerChannel, virt.mSampleRate);
            goto fail;
        }
        o->channels = 2;
        o->rate = 48000.0;
        o->ring_bpf = 4;
        o->bps = 48000.0 * 4;
        char fcc[5];
        fourcc_str(want.mFormatID, fcc);
        snprintf(o->desc, sizeof(o->desc), "%s (%s), exclusive%s, physical %s %u-bit 48 kHz, IOProc %s%d",
                 name, transport_name(transport), o->hogged ? "" : " (hog mode unavailable!)",
                 is_compressed_format(want.mFormatID) ? fcc : "LPCM", (unsigned)want.mBitsPerChannel,
                 o->virt_float ? "float" : "int", o->virt_bits);
    } else {
        AudioStreamBasicDescription virt = o->orig_virt;
        if (virt.mFormatID != kAudioFormatLinearPCM || !(virt.mFormatFlags & kAudioFormatFlagIsFloat) ||
            virt.mBitsPerChannel != 32 || (virt.mFormatFlags & kAudioFormatFlagIsNonInterleaved)) {
            snprintf(err, errlen, "'%s' is not in a float PCM format (is another app bitstreaming to it?)", name);
            goto fail;
        }
        o->virt_float = 1; o->virt_bits = 32;
        o->virt_channels = (int)virt.mChannelsPerFrame;
        o->channels = o->virt_channels;
        o->rate = virt.mSampleRate;
        o->ring_bpf = o->channels * 4;
        o->bps = o->rate * o->ring_bpf;
        snprintf(o->desc, sizeof(o->desc), "%s (%s), shared, %d ch float %.0f Hz",
                 name, transport_name(transport), o->channels, o->rate);
    }

    UInt32 dlat = 0, slat = 0;
    getp(o->dev, kAudioDevicePropertyLatency, kAudioObjectPropertyScopeOutput, sizeof(dlat), &dlat);
    getp(o->stream, kAudioStreamPropertyLatency, kAudioObjectPropertyScopeGlobal, sizeof(slat), &slat);
    o->latency = (double)(dlat + slat) / o->rate;

    alloc_ring(o, 0.4);
    if (AudioDeviceCreateIOProcID(o->dev, ioproc, o, &o->procid) != noErr) {
        snprintf(err, errlen, "AudioDeviceCreateIOProcID failed");
        goto fail;
    }
    return o;
fail:
    pc_out_close(o);
    return NULL;
}

PCOut *pc_out_open_file(const char *path, int bitstream, int pcm_channels, double pcm_rate, char *err, int errlen) {
    PCOut *o = alloc_out();
    o->file = fopen(path, "wb");
    if (!o->file) { snprintf(err, errlen, "Cannot open %s", path); free(o); return NULL; }
    o->bitstream = bitstream;
    o->channels = bitstream ? 2 : pcm_channels;
    o->rate = bitstream ? 48000.0 : pcm_rate;
    o->ring_bpf = bitstream ? 4 : pcm_channels * 4;
    o->bps = o->rate * o->ring_bpf;
    o->file_pts = NAN;
    snprintf(o->desc, sizeof(o->desc), "file %s (%s)", path, bitstream ? "IEC 61937 s16le 48 kHz" : "f32le");
    return o;
}

void pc_out_close(PCOut *o) {
    if (!o) return;
    if (o->file) fclose(o->file);
    if (o->procid) {
        if (atomic_load(&o->running)) AudioDeviceStop(o->dev, o->procid);
        AudioDeviceDestroyIOProcID(o->dev, o->procid);
    }
    if (o->virt_changed && !o->phys_changed)
        setp(o->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(o->orig_virt), &o->orig_virt);
    release_device(o);
    pthread_mutex_destroy(&o->mlock);
    free(o->ring);
    free(o);
}

int pc_out_start(PCOut *o) {
    if (o->file || atomic_load(&o->running)) return 0;
    if (AudioDeviceStart(o->dev, o->procid) != noErr) return -1;
    atomic_store(&o->running, 1);
    return 0;
}

double pc_out_rate(PCOut *o) { return o->rate; }
int pc_out_channels(PCOut *o) { return o->channels; }
double pc_out_bytes_per_sec(PCOut *o) { return o->bps; }
const char *pc_out_description(PCOut *o) { return o->desc; }

void pc_out_set_silence_burst(PCOut *o, const uint8_t *burst, int len) {
    if (len != PC_AC3_BURST_BYTES) return;
    memcpy(o->silence, burst, PC_AC3_BURST_BYTES);
    atomic_store_explicit(&o->have_silence, 1, memory_order_release);
}

// ---------------------------------------------------------------------------
// producer side

static void add_marker(PCOut *o, int64_t pos, double pts) {
    pthread_mutex_lock(&o->mlock);
    // drop markers that are long played (keep ~2 s of history for latency lookback)
    int64_t keep_from = atomic_load(&o->rpos) - (int64_t)(o->bps * 2);
    while (o->mcount > 1) {
        PtsMarker *next = &o->markers[(o->mstart + 1) % MAX_MARKERS];
        if (next->pos > keep_from) break;
        o->mstart = (o->mstart + 1) % MAX_MARKERS;
        o->mcount--;
    }
    if (o->mcount == MAX_MARKERS) { o->mstart = (o->mstart + 1) % MAX_MARKERS; o->mcount--; }
    o->markers[(o->mstart + o->mcount) % MAX_MARKERS] = (PtsMarker){ pos, pts };
    o->mcount++;
    pthread_mutex_unlock(&o->mlock);
}

int pc_out_write(PCOut *o, const uint8_t *data, int len, double pts, const _Atomic int *serial, int my_serial) {
    if (o->file) {
        fwrite(data, 1, (size_t)len, o->file);
        o->file_pts = pts + len / o->bps;
        return 0;
    }
    if (len > o->cap) return -1;
    int64_t w = atomic_load_explicit(&o->wpos, memory_order_relaxed);
    for (;;) {
        if (serial && atomic_load(serial) != my_serial) return -1;
        int64_t r = atomic_load_explicit(&o->rpos, memory_order_acquire);
        int flush_pending = atomic_load(&o->flush_gen) != atomic_load(&o->seen_gen);
        if (!flush_pending && o->cap - (w - r) >= len) break;
        usleep(2000);
    }
    if (!isnan(pts)) add_marker(o, w, pts);
    int64_t off = w % o->cap;
    int first = (int)(o->cap - off < len ? o->cap - off : len);
    memcpy(o->ring + off, data, (size_t)first);
    if (first < len) memcpy(o->ring, data + first, (size_t)(len - first));
    atomic_store_explicit(&o->wpos, w + len, memory_order_release);
    return 0;
}

void pc_out_flush(PCOut *o) {
    if (o->file) { o->file_pts = NAN; return; }
    pthread_mutex_lock(&o->mlock);
    o->mcount = 0;
    pthread_mutex_unlock(&o->mlock);
    atomic_store(&o->flush_target, atomic_load(&o->wpos));
    int g = atomic_fetch_add(&o->flush_gen, 1) + 1;
    if (!atomic_load(&o->running)) {
        atomic_store(&o->rpos, atomic_load(&o->flush_target));
        atomic_store(&o->seen_gen, g);
        return;
    }
    for (int i = 0; i < 100 && atomic_load(&o->seen_gen) != g; i++) usleep(2000);
}

void pc_out_set_paused(PCOut *o, int paused) {
    if (paused) {
        o->frozen = pc_out_clock(o);
        atomic_store(&o->paused, 1);
    } else {
        atomic_store(&o->paused, 0);
        o->frozen = NAN;
    }
}

double pc_out_buffered_seconds(PCOut *o) {
    if (o->file) return 0;
    int64_t w = atomic_load(&o->wpos), r = atomic_load(&o->rpos);
    return (double)(w - r) / o->bps;
}

double pc_out_latency(PCOut *o) { return o->file ? 0 : o->latency; }

double pc_out_clock(PCOut *o) {
    if (o->file) return o->file_pts;
    if (atomic_load(&o->paused) && !isnan(o->frozen)) return o->frozen;
    int64_t pos, len;
    uint64_t host;
    for (;;) {
        uint32_t s1 = atomic_load_explicit(&o->seq, memory_order_acquire);
        if (s1 & 1) continue;
        pos = atomic_load_explicit(&o->rec_pos, memory_order_relaxed);
        len = atomic_load_explicit(&o->rec_len, memory_order_relaxed);
        host = atomic_load_explicit(&o->rec_host, memory_order_relaxed);
        if (atomic_load_explicit(&o->seq, memory_order_acquire) == s1) break;
    }
    if (host == 0) return NAN;
    uint64_t nowh = AudioGetCurrentHostTime();
    double elapsed = nowh >= host ? (double)AudioConvertHostTimeToNanos(nowh - host) * 1e-9
                                   : -(double)AudioConvertHostTimeToNanos(host - nowh) * 1e-9;
    elapsed -= o->latency;
    double p = (double)pos + elapsed * o->bps;
    if (p > (double)(pos + len)) p = (double)(pos + len);

    double pts = NAN;
    pthread_mutex_lock(&o->mlock);
    for (int i = o->mcount - 1; i >= 0; i--) {
        PtsMarker *m = &o->markers[(o->mstart + i) % MAX_MARKERS];
        if ((double)m->pos <= p) { pts = m->pts + (p - (double)m->pos) / o->bps; break; }
    }
    pthread_mutex_unlock(&o->mlock);
    return pts;
}

void pc_out_debug(PCOut *o) {
    if (!o) return;
    fprintf(stderr, "[out] w=%lld r=%lld flush_gen=%d seen=%d state=%d phase=%d paused=%d markers=%d rec_pos=%lld rec_len=%lld clock=%.3f\n",
            (long long)atomic_load(&o->wpos), (long long)atomic_load(&o->rpos), atomic_load(&o->flush_gen),
            atomic_load(&o->seen_gen), o->bs_state, o->bs_phase, atomic_load(&o->paused), o->mcount,
            (long long)atomic_load(&o->rec_pos), (long long)atomic_load(&o->rec_len), pc_out_clock(o));
}
