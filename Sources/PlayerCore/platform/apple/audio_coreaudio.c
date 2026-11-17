// CoreAudio HAL audio device back-end (macOS).
//
// Bitstream path (equivalent of Kodi's WASAPI exclusive / AESinkDARWINOSX passthrough):
//   1. take exclusive ownership of the device (kAudioDevicePropertyHogMode)
//   2. disable mixing (kAudioDevicePropertySupportsMixing = 0) so the HAL does no
//      volume / mixing / dithering on our samples
//   3. switch the stream's physical format to a digital AC3 format if the device
//      offers one, otherwise to 2ch 16-bit integer LPCM @ 48 kHz (what HDMI exposes)
//   4. ask for a virtual format identical to the physical one, so IOProc buffers
//      reach the hardware unconverted (pc_output.c handles int16/int32/float buffers)
//   5. on close restore the original format, mixing and release hog mode.
#if defined(__APPLE__) && !defined(PC_STUB_BACKENDS)

#include "../../pc_audio_device.h"
#include "../../pc_platform.h"

#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct PCAudioDevice {
    AudioDeviceID dev;
    AudioStreamID stream;
    int stream_buffer;              // index of our stream in the IOProc buffer list
    AudioDeviceIOProcID procid;
    int running;
    int hogged, mixing_changed, phys_changed, virt_changed;
    AudioStreamBasicDescription orig_phys, orig_virt;
    int bytes_per_frame;
    PCRenderCallback cb;
    void *ctx;
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

int pcad_list(PCDevice *out, int max) {
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

// Score a physical format for carrying IEC 61937; higher is better, <0 unusable.
static int score_bitstream_format(const AudioStreamRangedDescription *r) {
    const AudioStreamBasicDescription *f = &r->mFormat;
    if (!range_has_48k(r)) return -1;
    if (is_compressed_format(f->mFormatID)) return 100;   // true digital format (optical/S/PDIF, some HDMI)
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
        pc_sleep_us(10000);
    }
    return -1;
}

static void fourcc_str(UInt32 id, char out[5]) {
    out[0] = (char)(id >> 24); out[1] = (char)(id >> 16); out[2] = (char)(id >> 8); out[3] = (char)id; out[4] = 0;
}

// ---------------------------------------------------------------------------
// IOProc

static OSStatus ioproc(AudioObjectID dev, const AudioTimeStamp *now, const AudioBufferList *in,
                       const AudioTimeStamp *intime, AudioBufferList *out, const AudioTimeStamp *outtime, void *ud) {
    (void)dev; (void)in; (void)intime;
    PCAudioDevice *d = ud;
    for (UInt32 i = 0; i < out->mNumberBuffers; i++)
        if ((int)i != d->stream_buffer) memset(out->mBuffers[i].mData, 0, out->mBuffers[i].mDataByteSize);
    if (d->stream_buffer >= (int)out->mNumberBuffers) return noErr;
    AudioBuffer *b = &out->mBuffers[d->stream_buffer];
    uint64_t host = (outtime->mFlags & kAudioTimeStampHostTimeValid) ? outtime->mHostTime : now->mHostTime;
    d->cb(d->ctx, b->mData, (int)(b->mDataByteSize / d->bytes_per_frame), (int64_t)AudioConvertHostTimeToNanos(host));
    return noErr;
}

// ---------------------------------------------------------------------------
// open / close

static void restore_device(PCAudioDevice *d) {
    if (d->virt_changed && !d->phys_changed)
        setp(d->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(d->orig_virt), &d->orig_virt);
    if (d->phys_changed) set_physical_format(d->stream, &d->orig_phys);
    if (d->mixing_changed) {
        UInt32 mix = 1;
        setp(d->dev, kAudioDevicePropertySupportsMixing, kAudioObjectPropertyScopeGlobal, sizeof(mix), &mix);
    }
    if (d->hogged) {
        pid_t pid = -1;
        setp(d->dev, kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, sizeof(pid), &pid);
    }
    d->virt_changed = d->phys_changed = d->mixing_changed = d->hogged = 0;
}

PCAudioDevice *pcad_open(const char *uid, PCStreamType type, PCDeviceFormat *fmt, char *err, int errlen) {
    int bitstream = type != PC_STREAM_PCM;   // the digital format carries AC3 and DTS alike
    PCAudioDevice *d = calloc(1, sizeof(*d));
    memset(fmt, 0, sizeof(*fmt));
    d->dev = device_for_uid(uid);
    if (!d->dev) { snprintf(err, errlen, "Audio device not found"); goto fail; }
    d->stream = first_output_stream(d->dev);
    if (!d->stream) { snprintf(err, errlen, "Audio device has no output stream"); goto fail; }

    char name[256];
    device_name(d->dev, name, sizeof(name));
    UInt32 transport = device_transport(d->dev);
    getp(d->stream, kAudioStreamPropertyPhysicalFormat, kAudioObjectPropertyScopeGlobal, sizeof(d->orig_phys), &d->orig_phys);
    getp(d->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(d->orig_virt), &d->orig_virt);

    if (bitstream) {
        // Never send IEC 61937 to something that would play it as PCM noise
        // (speakers, headphones, Bluetooth): only HDMI/DisplayPort or devices
        // that advertise a compressed format (optical / S/PDIF interfaces).
        int digital = transport == kAudioDeviceTransportTypeHDMI ||
                      transport == kAudioDeviceTransportTypeDisplayPort || device_has_compressed(d->dev);
        if (!digital) {
            snprintf(err, errlen, "'%s' is not a digital (HDMI/optical) output", name);
            goto fail;
        }
        // 1. exclusive access
        pid_t hog = -1;
        getp(d->dev, kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, sizeof(hog), &hog);
        if (hog != -1 && hog != getpid()) {
            snprintf(err, errlen, "'%s' is in exclusive use by another application (pid %d)", name, hog);
            goto fail;
        }
        if (hog != getpid()) {
            pid_t me = getpid();
            if (setp(d->dev, kAudioDevicePropertyHogMode, kAudioObjectPropertyScopeGlobal, sizeof(me), &me) == noErr)
                d->hogged = 1;
        }
        // 2. no mixing -> the HAL passes our integer samples straight through
        AudioObjectPropertyAddress ma = addr(kAudioDevicePropertySupportsMixing, kAudioObjectPropertyScopeGlobal);
        Boolean settable = false;
        if (AudioObjectHasProperty(d->dev, &ma) && AudioObjectIsPropertySettable(d->dev, &ma, &settable) == noErr && settable) {
            UInt32 mix = 0;
            if (setp(d->dev, kAudioDevicePropertySupportsMixing, kAudioObjectPropertyScopeGlobal, sizeof(mix), &mix) == noErr)
                d->mixing_changed = 1;
        }
        // 3. choose a physical format that can carry IEC 61937
        UInt32 size = 0;
        AudioStreamRangedDescription *fmts = getp_alloc(d->stream, kAudioStreamPropertyAvailablePhysicalFormats, kAudioObjectPropertyScopeGlobal, &size);
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
        if (set_physical_format(d->stream, &want) != 0) {
            snprintf(err, errlen, "Could not switch '%s' to a bitstream-capable format", name);
            goto fail;
        }
        d->phys_changed = !same_format(&want, &d->orig_phys);
        Float64 sr = 48000.0;
        setp(d->dev, kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal, sizeof(sr), &sr);

        // 4. an integer virtual format identical to the physical one (no conversion)
        if (setp(d->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(want), &want) == noErr)
            d->virt_changed = 1;
        AudioStreamBasicDescription virt;
        getp(d->stream, kAudioStreamPropertyVirtualFormat, kAudioObjectPropertyScopeGlobal, sizeof(virt), &virt);
        fmt->channels = (int)virt.mChannelsPerFrame;
        fmt->bits = (int)virt.mBitsPerChannel;
        fmt->is_float = (virt.mFormatFlags & kAudioFormatFlagIsFloat) != 0;
        fmt->rate = virt.mSampleRate;
        if (is_compressed_format(virt.mFormatID)) { fmt->is_float = 0; fmt->bits = 16; fmt->channels = 2; }
        if (fmt->channels != 2 || fmt->rate != 48000.0 ||
            !(fmt->bits == 16 || fmt->bits == 32) || (fmt->is_float && fmt->bits != 32)) {
            snprintf(err, errlen, "Unsupported stream format on '%s' (%u ch, %u bit, %.0f Hz)",
                     name, (unsigned)virt.mChannelsPerFrame, (unsigned)virt.mBitsPerChannel, virt.mSampleRate);
            goto fail;
        }
        char fcc[5];
        fourcc_str(want.mFormatID, fcc);
        snprintf(fmt->description, sizeof(fmt->description), "%s (%s), exclusive%s, physical %s %u-bit 48 kHz, IOProc %s%d",
                 name, transport_name(transport), d->hogged ? "" : " (hog mode unavailable!)",
                 is_compressed_format(want.mFormatID) ? fcc : "LPCM", (unsigned)want.mBitsPerChannel,
                 fmt->is_float ? "float" : "int", fmt->bits);
    } else {
        AudioStreamBasicDescription virt = d->orig_virt;
        if (virt.mFormatID != kAudioFormatLinearPCM || !(virt.mFormatFlags & kAudioFormatFlagIsFloat) ||
            virt.mBitsPerChannel != 32 || (virt.mFormatFlags & kAudioFormatFlagIsNonInterleaved)) {
            snprintf(err, errlen, "'%s' is not in a float PCM format (is another app bitstreaming to it?)", name);
            goto fail;
        }
        fmt->is_float = 1;
        fmt->bits = 32;
        fmt->channels = (int)virt.mChannelsPerFrame;
        fmt->rate = virt.mSampleRate;
        snprintf(fmt->description, sizeof(fmt->description), "%s (%s), shared, %d ch float %.0f Hz",
                 name, transport_name(transport), fmt->channels, fmt->rate);
    }
    d->bytes_per_frame = fmt->channels * fmt->bits / 8;

    UInt32 dlat = 0, slat = 0;
    getp(d->dev, kAudioDevicePropertyLatency, kAudioObjectPropertyScopeOutput, sizeof(dlat), &dlat);
    getp(d->stream, kAudioStreamPropertyLatency, kAudioObjectPropertyScopeGlobal, sizeof(slat), &slat);
    fmt->latency = (double)(dlat + slat) / fmt->rate;

    if (AudioDeviceCreateIOProcID(d->dev, ioproc, d, &d->procid) != noErr) {
        snprintf(err, errlen, "AudioDeviceCreateIOProcID failed");
        goto fail;
    }
    return d;
fail:
    pcad_close(d);
    return NULL;
}

int pcad_start(PCAudioDevice *d, PCRenderCallback cb, void *ctx) {
    d->cb = cb;
    d->ctx = ctx;
    if (AudioDeviceStart(d->dev, d->procid) != noErr) return -1;
    d->running = 1;
    return 0;
}

void pcad_close(PCAudioDevice *d) {
    if (!d) return;
    if (d->procid) {
        if (d->running) AudioDeviceStop(d->dev, d->procid);
        AudioDeviceDestroyIOProcID(d->dev, d->procid);
    }
    if (d->dev) restore_device(d);
    free(d);
}

#endif
