#ifndef PC_OUTPUT_H
#define PC_OUTPUT_H

#include <stdint.h>
#include <stdatomic.h>

// IEC 61937 burst = one compressed frame padded to its repetition period:
// AC3 1536 samples * 4 bytes = 6144; DTS 512 samples * 4 = 2048.
#define PC_AC3_BURST_BYTES 6144
#define PC_MAX_BURST_BYTES 8192

typedef struct PCOut PCOut;

// bitstream=1: exclusive (hog) mode, 2ch/16-bit/48 kHz digital stream carrying IEC 61937.
//              The ring then holds little-endian s16 stereo frames.
// bitstream=0: shared mode, ring holds interleaved float32 in the device's channel count/rate.
PCOut *pc_out_open(const char *device_uid, int bitstream, char *err, int errlen);
// Testing sink: writes the ring-format bytes to a file, never blocks.
PCOut *pc_out_open_file(const char *path, int bitstream, int pcm_channels, double pcm_rate, char *err, int errlen);
void pc_out_close(PCOut *o);

int pc_out_start(PCOut *o);
double pc_out_rate(PCOut *o);
int pc_out_channels(PCOut *o);          // ring channels
double pc_out_bytes_per_sec(PCOut *o);
const char *pc_out_description(PCOut *o);

// Bitstream only. The burst size the stream is written in (writes are always whole
// bursts); set before pc_out_start. Defaults to the AC3 size.
void pc_out_set_burst_size(PCOut *o, int bytes);
int pc_out_burst_size(PCOut *o);
// A pre-packed IEC 61937 burst of encoded silence (len = burst size), played while
// paused/underrunning so the receiver stays locked (Kodi's "keep alive").
void pc_out_set_silence_burst(PCOut *o, const uint8_t *burst, int len);

// Blocks until `len` bytes fit. Aborts (returns -1) when *serial != my_serial.
int pc_out_write(PCOut *o, const uint8_t *data, int len, double pts,
                 const _Atomic int *serial, int my_serial);
void pc_out_flush(PCOut *o);
void pc_out_set_paused(PCOut *o, int paused);
double pc_out_buffered_seconds(PCOut *o); // ring contents only
double pc_out_latency(PCOut *o);
// Presentation timestamp of the audio currently leaving the device (NAN if unknown).
double pc_out_clock(PCOut *o);
void pc_out_debug(PCOut *o);

#endif
