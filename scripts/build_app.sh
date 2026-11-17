#!/bin/bash
# Builds "Surround Player.app" into ./build. Requires: Command Line Tools, `brew install ffmpeg`.
set -euo pipefail
cd "$(dirname "$0")/.."
swift build -c release --product SurroundPlayer
APP="build/Surround Player.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/SurroundPlayer "$APP/Contents/MacOS/SurroundPlayer"
cp Resources/Info.plist "$APP/Contents/Info.plist"
codesign --force --sign - "$APP"
echo "Built $APP"
