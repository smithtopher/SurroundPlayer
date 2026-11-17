#!/bin/bash
# Builds Surround Player for Apple Silicon, Intel and as a universal app:
#   build/arm64/Surround Player.app      Apple Silicon
#   build/x86_64/Surround Player.app     Intel
#   build/universal/Surround Player.app  both
# plus a zip of each in build/. FFmpeg is linked statically (scripts/build_ffmpeg.sh),
# so the apps don't need Homebrew. Only the Command Line Tools are required.
# ARCHS="arm64" ./scripts/build_app.sh builds a single architecture.
set -euo pipefail
cd "$(dirname "$0")/.."

ARCHS="${ARCHS:-arm64 x86_64}"
MIN_MACOS=14.0
NAME="Surround Player"
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Resources/Info.plist)

ARCHS="$ARCHS" ./scripts/build_ffmpeg.sh
if [ ! -f Resources/AppIcon.icns ] || [ icon.png -nt Resources/AppIcon.icns ]; then ./scripts/make_icns.sh; fi

make_bundle() { # <dir> <binary>
    local app="$1/$NAME.app"
    rm -rf "$app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp "$2" "$app/Contents/MacOS/SurroundPlayer"
    cp Resources/Info.plist "$app/Contents/Info.plist"
    cp Resources/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
    codesign --force --sign - "$app"
    # fail loudly if anything outside the OS slipped in (e.g. a Homebrew dylib)
    if otool -L "$app/Contents/MacOS/SurroundPlayer" | grep -E "^\s+/" | grep -vE "^\s+/(System|usr/lib)/"; then
        echo "error: non-system library linked"; exit 1
    fi
}

BINS=()
for ARCH in $ARCHS; do
    echo "== $ARCH"
    FFMPEG_PREFIX="$PWD/vendor/ffmpeg/$ARCH" swift build -c release --product SurroundPlayer \
        --triple "$ARCH-apple-macosx$MIN_MACOS" --scratch-path ".build/$ARCH" 2>&1 \
        | grep -v "no platform load command found"   # nasm objects carry no macOS version tag; harmless
    BIN=".build/$ARCH/release/SurroundPlayer"
    BINS+=("$BIN")
    make_bundle "build/$ARCH" "$BIN"
done

if [ ${#BINS[@]} -gt 1 ]; then
    mkdir -p build/universal
    lipo -create "${BINS[@]}" -output build/universal/SurroundPlayer
    make_bundle build/universal build/universal/SurroundPlayer
    rm build/universal/SurroundPlayer
fi

for dir in build/arm64 build/x86_64 build/universal; do
    [ -d "$dir/$NAME.app" ] || continue
    label=$(basename "$dir"); [ "$label" = x86_64 ] && label=intel; [ "$label" = arm64 ] && label=apple-silicon
    zip="build/SurroundPlayer-$VERSION-$label.zip"
    rm -f "$zip"
    ditto -c -k --keepParent "$dir/$NAME.app" "$zip"
    echo "$(lipo -archs "$dir/$NAME.app/Contents/MacOS/SurroundPlayer")	$zip"
done
