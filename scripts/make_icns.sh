#!/bin/bash
# icon.png -> Resources/AppIcon.icns
set -euo pipefail
cd "$(dirname "$0")/.."
TMP=$(mktemp -d)
swiftc -O scripts/make_icon.swift -o "$TMP/make_icon"
"$TMP/make_icon" icon.png "$TMP/icon_1024.png"
SET="$TMP/AppIcon.iconset"; mkdir -p "$SET"
for s in 16 32 128 256 512; do
    sips -z $s $s "$TMP/icon_1024.png" --out "$SET/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2)); sips -z $d $d "$TMP/icon_1024.png" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$SET" -o Resources/AppIcon.icns
cp "$TMP/icon_1024.png" Resources/AppIcon-1024.png
rm -rf "$TMP"
echo "Wrote Resources/AppIcon.icns"
