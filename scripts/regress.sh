#!/bin/bash
# Behavioural regression test of the engine's output stream, runnable on any OS with
# bash, ffmpeg/ffprobe and python3 (macOS, or MSYS2/Git Bash on Windows). Generates
# its own test media, runs every output mode through a dump tool, and checks:
#   - codec / layout / rate of the IEC 61937 stream (ffprobe -f spdif)
#   - burst spacing (AC3 6144 bytes, DTS 2048 bytes)
#   - every channel carries the same tone as the source's channel (channel mapping)
#   - passthrough payloads are bit-identical to the source
# Nothing is played on any device.
#   scripts/regress.sh [dump-tool]   (default: .build/portable/pcdump from check_portable.sh)
set -uo pipefail
cd "$(dirname "$0")/.."
DUMP="${1:-.build/portable/pcdump}"
[ -x "$DUMP" ] || { echo "dump tool $DUMP not found (run scripts/check_portable.sh first)"; exit 2; }
PY=$(command -v python3 || command -v python) || { echo "python3 not found"; exit 2; }   # Windows: often just "python"
W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT
FAIL=0
pass() { echo "  ok    $1"; }
fail() { echo "  FAIL  $1"; FAIL=1; }
ff() { ffmpeg -hide_banner -loglevel error -y "$@"; }

echo "generating test media in $W"
# one distinct tone per channel, explicitly mapped (FL 300, FR 500, FC 700, LFE 60, SL 1100, SR 1300)
ff -f lavfi -i sine=f=300:sample_rate=48000 -f lavfi -i sine=f=500:sample_rate=48000 -f lavfi -i sine=f=700:sample_rate=48000 \
   -f lavfi -i sine=f=60:sample_rate=48000 -f lavfi -i sine=f=1100:sample_rate=48000 -f lavfi -i sine=f=1300:sample_rate=48000 \
   -filter_complex "join=inputs=6:channel_layout=5.1(side):map=0.0-FL|1.0-FR|2.0-FC|3.0-LFE|4.0-SL|5.0-SR[a]" \
   -map "[a]" -t 6 -c:a flac "$W/src.flac"
ff -i "$W/src.flac" -c:a eac3 -b:a 640k "$W/eac3.mkv"
ff -i "$W/src.flac" -c:a ac3 -b:a 448k "$W/ac3.mkv"
# DTS source made with the engine's own (patched) encoder: stock FFmpeg's dca encoder
# can crash (patches/ffmpeg/0001-dcaenc-fix-lfe-history-read.patch)
"$DUMP" "$W/src.flac" "$W/dts.spdif" --dts > /dev/null && ff -f spdif -i "$W/dts.spdif" -c copy "$W/dts.mkv"
ff -f lavfi -i sine=f=440:sample_rate=44100 -t 4 -ac 2 -c:a libmp3lame "$W/stereo.mp3" 2>/dev/null ||
  ff -f lavfi -i sine=f=440:sample_rate=44100 -t 4 -ac 2 -c:a flac "$W/stereo.flac"
STEREO=$(ls "$W"/stereo.* | head -1)

# per-channel dominant frequency (zero crossings), seconds 1..4
cat > "$W/freqs.py" <<'EOF'
import sys, wave, struct
w = wave.open(sys.argv[1]); ch = w.getnchannels(); sr = w.getframerate()
w.readframes(sr); data = w.readframes(sr * 3)
n = len(data) // (2 * ch); s = struct.unpack('<%dh' % (n * ch), data)
print(' '.join(str(round(sum(1 for i in range(1, n) if (x[i-1] < 0) != (x[i] < 0)) / 2 / (n / sr), -1))
               for x in (s[c::ch] for c in range(ch))))
EOF
cat > "$W/bursts.py" <<'EOF'
import sys
d = open(sys.argv[1], 'rb').read()
offs = [i for i in range(0, len(d) - 4, 2) if d[i:i+4] == b'\x72\xf8\x1f\x4e']
print(sorted(set(offs[i+1] - offs[i] for i in range(len(offs) - 1))) if len(offs) > 1 else [])
EOF
ff -i "$W/src.flac" -c:a pcm_s16le "$W/src.wav"
SRC_FREQS=$("$PY" -I "$W/freqs.py" "$W/src.wav")
echo "source channel tones: $SRC_FREQS"

check_stream() { # name file expect-codec expect-layout expect-spacing
    local info; info=$(ffprobe -hide_banner -f spdif -i "$2" 2>&1 | grep "Stream #0")
    echo "$info" | grep -q "$3" && echo "$info" | grep -q "48000 Hz" && echo "$info" | grep -q "$4" \
        && pass "$1: $3 $4 48 kHz" || fail "$1: got '$info'"
    local sp; sp=$("$PY" -I "$W/bursts.py" "$2")
    [ "$sp" = "[$5]" ] && pass "$1: bursts every $5 bytes" || fail "$1: burst spacing $sp, want [$5]"
}
check_channels() { # name file
    ff -f spdif -i "$2" -c:a pcm_s16le "$W/out.wav"
    local f; f=$("$PY" -I "$W/freqs.py" "$W/out.wav")
    [ "$f" = "$SRC_FREQS" ] && pass "$1: channel mapping ($f)" || fail "$1: channels '$f' != source '$SRC_FREQS'"
}
check_passthrough() { # name dump source fmt
    ff -i "$3" -map 0:a -c copy -f "$4" "$W/es.src"
    ff -f spdif -i "$2" -c copy -f "$4" "$W/es.out"
    cmp -s "$W/es.src" "$W/es.out" && pass "$1: payload bit-identical to source" || fail "$1: payload differs from source"
}
run() { "$DUMP" "$@" > "$W/log.txt" 2>&1 || { fail "dump $*: $(cat "$W/log.txt")"; return 1; }; }

echo "AC3 transcode (E-AC3 5.1 source)"
run "$W/eac3.mkv" "$W/o1.spdif" && { check_stream "ac3" "$W/o1.spdif" "ac3" "5.1(side)" 6144; check_channels "ac3" "$W/o1.spdif"; }
echo "DTS transcode (E-AC3 5.1 source)"
run "$W/eac3.mkv" "$W/o2.spdif" --dts && { check_stream "dts" "$W/o2.spdif" "dts" "5.1(side)" 2048; check_channels "dts" "$W/o2.spdif"; }
echo "AC3 passthrough"
run "$W/ac3.mkv" "$W/o3.spdif" && { check_stream "ac3 pass" "$W/o3.spdif" "ac3" "5.1(side)" 6144; check_passthrough "ac3 pass" "$W/o3.spdif" "$W/ac3.mkv" ac3; }
echo "DTS passthrough (DTS mode)"
run "$W/dts.mkv" "$W/o4.spdif" --dts && { check_stream "dts pass" "$W/o4.spdif" "dts" "5.1(side)" 2048; check_passthrough "dts pass" "$W/o4.spdif" "$W/dts.mkv" dts; }
echo "DTS source in AC3 mode -> AC3"
run "$W/dts.mkv" "$W/o5.spdif" && { check_stream "dts->ac3" "$W/o5.spdif" "ac3" "5.1(side)" 6144; check_channels "dts->ac3" "$W/o5.spdif"; }
echo "AC3 source in DTS mode -> passthrough"
run "$W/ac3.mkv" "$W/o6.spdif" --dts && check_passthrough "ac3 in dts mode" "$W/o6.spdif" "$W/ac3.mkv" ac3
echo "Stereo encoded as DTS"
run "$STEREO" "$W/o7.spdif" --dts --stereo && check_stream "stereo dts" "$W/o7.spdif" "dts" "stereo" 2048
echo "PCM (FLAC 5.1 -> f32le stereo 48 kHz)"
run "$W/src.flac" "$W/o8.raw" --pcm && {
    sz=$(wc -c < "$W/o8.raw"); want=$((6 * 48000 * 2 * 4))
    [ $(( sz > want - 48000 && sz < want + 48000 )) = 1 ] && pass "pcm: $sz bytes (~6 s stereo float)" || fail "pcm: $sz bytes, want ~$want"
}

[ $FAIL = 0 ] && echo "ALL PASSED" || echo "SOME CHECKS FAILED"
exit $FAIL
