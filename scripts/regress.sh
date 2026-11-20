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

echo "Multichannel PCM, untranscoded (FLAC 5.1 -> f32le 6 ch at the source rate)"
run "$W/src.flac" "$W/o9.raw" --mc-pcm && {
    sz=$(wc -c < "$W/o9.raw"); want=$((6 * 48000 * 6 * 4))
    [ $(( sz > want - 96000 && sz < want + 96000 )) = 1 ] && pass "mc pcm: $sz bytes (~6 s, 6 ch float)" || fail "mc pcm: $sz bytes, want ~$want"
    ff -f f32le -ar 48000 -ch_layout "5.1(side)" -i "$W/o9.raw" -c:a pcm_s16le "$W/o9.wav"
    f=$("$PY" -I "$W/freqs.py" "$W/o9.wav")
    [ "$f" = "$SRC_FREQS" ] && pass "mc pcm: channel mapping ($f)" || fail "mc pcm: channels '$f' != source '$SRC_FREQS'"
}
echo "Audio track selection (AC3 + FLAC tracks, --track 1)"
ff -i "$W/ac3.mkv" -i "$W/src.flac" -map 0:a -map 1:a -c copy "$W/tracks.mkv"
run "$W/tracks.mkv" "$W/o10.raw" --track 1 --mc-pcm && {
    grep -q "^source: flac" "$W/log.txt" && grep -q "(playing)" "$W/log.txt" \
        && pass "track 1 selected: $(grep '^source:' "$W/log.txt")" || fail "track selection: $(head -3 "$W/log.txt")"
}

echo "Stereo upmix (stereo -> 5.1, FFmpeg surround filter)"
# per-channel levels of a 6-channel f32le dump (FL FR FC LFE BL BR), seconds 2..4; then a check
cat > "$W/upmix.py" <<'EOF2'
import sys, math
from array import array
a = array('f'); a.frombytes(open(sys.argv[1], 'rb').read()); ch = 6; rate = 48000
seg = a[2 * rate * ch: 4 * rate * ch]
db = []
for c in range(ch):
    x = seg[c::ch]; e = math.fsum(v * v for v in x) / max(1, len(x))
    db.append(10 * math.log10(e) if e > 1e-15 else -150.0)
FL, FR, FC, LFE, BL, BR = db
rule = sys.argv[2]
ok = {
    'left':      FL > max(FR, FC, BL, BR) + 20,                  # hard left stays front left
    'centre':    FC > FL - 6 and max(BL, BR) < FL - 30,          # centred -> centre (+ fronts), not rears
    'antiphase': min(BL, BR) > max(FL, FR, FC) + 6,              # out-of-phase ambience -> rears
    'norears':   max(BL, BR) < FL - 40,                          # surround=-40
    'nocentre':  FC < FL - 40,                                   # center=0: phantom centre
    'lfe':       LFE > FL - 20,                                  # bass reaches the sub
    'nolfe':     LFE < FL - 60,                                  # lfe=-40
    'mono':      FC > FL - 6,                                    # mono -> centre
}[rule]
print(('ok ' if ok else 'BAD ') + ' '.join('%s %.1f' % (n, v) for n, v in zip(['FL', 'FR', 'FC', 'LFE', 'BL', 'BR'], db)))
EOF2
up_check() { # name dump rule
    local r; r=$("$PY" -I "$W/upmix.py" "$2" "$3")
    case "$r" in ok*) pass "$1: ${r#ok }";; *) fail "$1: ${r#BAD }";; esac
}
ff -f lavfi -i "aevalsrc=0.25*sin(2*PI*1000*t)|0:s=48000:d=6" -t 6 -c:a flac "$W/up_left.flac"
ff -f lavfi -i "aevalsrc=0.25*sin(2*PI*1000*t)|0.25*sin(2*PI*1000*t):s=48000:d=6" -t 6 -c:a flac "$W/up_centre.flac"
ff -f lavfi -i "aevalsrc=0.25*sin(2*PI*1000*t)|-0.25*sin(2*PI*1000*t):s=48000:d=6" -t 6 -c:a flac "$W/up_anti.flac"
ff -f lavfi -i "aevalsrc=0.25*sin(2*PI*50*t)|0.25*sin(2*PI*50*t):s=48000:d=6" -t 6 -c:a flac "$W/up_bass.flac"
ff -f lavfi -i "aevalsrc=0.25*sin(2*PI*1000*t):s=48000:d=6" -t 6 -c:a flac "$W/up_mono.flac"
ff -f lavfi -i "aevalsrc='if(eq(n,48000),0.5,0)|if(eq(n,48000),0.5,0)':s=48000:d=3" -t 3 -c:a flac "$W/up_imp.flac"
run "$W/up_left.flac" "$W/u1.raw" --mc-pcm --upmix && up_check "upmix hard left" "$W/u1.raw" left
run "$W/up_centre.flac" "$W/u2.raw" --mc-pcm --upmix && {
    up_check "upmix centred" "$W/u2.raw" centre
    grep -q "upmixed from stereo" "$W/log.txt" && pass "upmix: output says 'upmixed from stereo'" || fail "upmix: $(grep '^output' "$W/log.txt")"
}
run "$W/up_anti.flac" "$W/u3.raw" --mc-pcm --upmix && up_check "upmix ambience" "$W/u3.raw" antiphase
run "$W/up_anti.flac" "$W/u4.raw" --mc-pcm --upmix-set surround=-40 && up_check "upmix surround=-40" "$W/u4.raw" norears
run "$W/up_centre.flac" "$W/u5.raw" --mc-pcm --upmix-set center=0 && up_check "upmix center=0" "$W/u5.raw" nocentre
run "$W/up_bass.flac" "$W/u6.raw" --mc-pcm --upmix-set lfe=0 && up_check "upmix lfe=0 dB" "$W/u6.raw" lfe
run "$W/up_bass.flac" "$W/u7.raw" --mc-pcm --upmix-set lfe=-40 && up_check "upmix lfe=-40" "$W/u7.raw" nolfe
run "$W/up_mono.flac" "$W/u8.raw" --mc-pcm --upmix && {
    up_check "upmix mono" "$W/u8.raw" mono
    grep -q "upmixed from mono" "$W/log.txt" && pass "upmix: mono source described" || fail "upmix mono: $(grep '^output' "$W/log.txt")"
}
# latency: the dump starts with the graph's priming, stamped 2048 samples early, so the impulse
# at 1.000 s must sit at index 48000 + 2048 (any other offset would put video out of sync)
run "$W/up_imp.flac" "$W/u9.raw" --mc-pcm --upmix && {
    k=$("$PY" -I -c "
import sys
from array import array
a = array('f'); a.frombytes(open(sys.argv[1], 'rb').read()); n = len(a) // 6
print(max(range(n), key=lambda i: abs(a[i * 6 + 2])))" "$W/u9.raw")
    [ "$k" = 50048 ] && pass "upmix latency: impulse at 48000+2048 (compensated in the timestamps)" || fail "upmix latency: impulse at $k, want 50048"
}
run "$W/up_centre.flac" "$W/u10.spdif" --upmix && {
    check_stream "upmix ac3" "$W/u10.spdif" "ac3" "5.1" 6144
    grep -q "upmixed from stereo, transcoded" "$W/log.txt" && pass "upmix ac3: transcoded from stereo" || fail "upmix ac3: $(grep '^output' "$W/log.txt")"
}
run "$W/up_centre.flac" "$W/u11.raw" && {
    grep -q "^output: PCM 2 ch" "$W/log.txt" && pass "upmix off: stereo stays stereo PCM" || fail "upmix off: $(grep '^output' "$W/log.txt")"
}

[ $FAIL = 0 ] && echo "ALL PASSED" || echo "SOME CHECKS FAILED"
exit $FAIL
