#!/bin/bash
# Proves the engine core is OS-independent: compiles engine.c + pc_output.c with the
# stub back-ends (-DPC_STUB_BACKENDS), fails if any of those objects reference an
# Apple framework symbol, then links tools/pcdump.c against them and runs it.
#   scripts/check_portable.sh [<media> <out> [pcdump flags]]
set -euo pipefail
cd "$(dirname "$0")/.."
ARCH=$(uname -m)
FF="${FFMPEG_PREFIX:-$PWD/vendor/ffmpeg/$ARCH}"
OUT=.build/portable
mkdir -p "$OUT"
CFLAGS=(-std=c11 -O2 -Wall -Wextra -Wno-unused-parameter -Wno-sign-compare -DPC_STUB_BACKENDS
        -ISources/PlayerCore -ISources/PlayerCore/include -I"$FF/include")
SRCS=(Sources/PlayerCore/engine.c Sources/PlayerCore/pc_output.c Sources/PlayerCore/pc_subtitle.c Sources/PlayerCore/pc_upmix.c
      Sources/PlayerCore/platform/stub/audio_stub.c Sources/PlayerCore/platform/stub/video_stub.c
      Sources/PlayerCore/platform/apple/audio_coreaudio.c Sources/PlayerCore/platform/apple/video_apple.c)
OBJS=()
for src in "${SRCS[@]}"; do
    obj="$OUT/$(basename "${src%.c}").o"
    clang "${CFLAGS[@]}" -c "$src" -o "$obj"
    OBJS+=("$obj")
    # no framework headers may be pulled in
    if clang "${CFLAGS[@]}" -M "$src" | tr ' \\' '\n\n' | grep -q '\.framework/'; then
        echo "FAIL: $src includes framework headers"; exit 1
    fi
done
# undefined symbols must be FFmpeg / libc only (mach_* is libSystem's clock on macOS)
BAD=$(nm -u "${OBJS[@]}" | grep -E '^_' | sed 's/^_//' | grep -E '^(CF|CV|CM|VT|Audio|kCV|kCF|kAudio|NS|objc_)' || true)
if [ -n "$BAD" ]; then echo "FAIL: OS media symbols referenced:"; echo "$BAD"; exit 1; fi
echo "core + stub back-ends: no framework headers, no OS media symbols"

# link a plain C driver (FFmpeg itself was built with VideoToolbox on macOS, so its
# own dependencies still need the frameworks at link time)
FFLIBS=(-L"$FF/lib" -lavfilter -lavformat -lavcodec -lswresample -lswscale -lavutil -lz -lbz2)
[ "$(uname)" = Darwin ] && FFLIBS+=(-framework VideoToolbox -framework CoreMedia -framework CoreVideo
                                   -framework CoreFoundation -framework CoreServices)
clang "${CFLAGS[@]}" tools/pcdump.c "${OBJS[@]}" "${FFLIBS[@]}" -lpthread -o "$OUT/pcdump"
echo "built $OUT/pcdump"
if [ $# -ge 2 ]; then "$OUT/pcdump" "$@"; fi
