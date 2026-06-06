#!/usr/bin/env bash
set -euo pipefail

PUBLISH_DIR="publish"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

BUNDLE_NAME="Shoko Companion.app"
DMG_NAME="shoko-companion-osx-arm64.dmg"
ICON_PNG="$SCRIPT_DIR/../linux/AppDir/shoko-companion.png"
ICON_DIR="$SCRIPT_DIR"

# Build paths
CONTENTS="$BUNDLE_NAME/Contents"
MACOS_DIR="$CONTENTS/MacOS"
RESOURCES_DIR="$CONTENTS/Resources"

# Create bundle structure
rm -rf "$BUNDLE_NAME"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# Copy binary
cp "$PUBLISH_DIR/shoko-companion" "$MACOS_DIR/shoko-companion"
chmod +x "$MACOS_DIR/shoko-companion"

# Copy Info.plist
cp "$SCRIPT_DIR/Info.plist" "$CONTENTS/"

# Generate icon.icns from source PNG using native macOS tools
if [ -f "$ICON_DIR/icon.icns" ]; then
    cp "$ICON_DIR/icon.icns" "$RESOURCES_DIR/icon.icns"
else
    # Generate from PNG at build time
    ICONSET_DIR="$(mktemp -d)/icon.iconset"
    mkdir -p "$ICONSET_DIR"
    for size in 16 32 64 128 256 512; do
        sips -z $size $size "$ICON_PNG" --out "$ICONSET_DIR/icon_${size}x${size}.png" >/dev/null 2>&1
        if [ $size -le 256 ]; then
            sips -z $((size*2)) $((size*2)) "$ICON_PNG" --out "$ICONSET_DIR/icon_${size}x${size}@2x.png" >/dev/null 2>&1
        fi
    done
    iconutil -c icns -o "$RESOURCES_DIR/icon.icns" "$ICONSET_DIR"
    rm -rf "$(dirname "$ICONSET_DIR")"
fi

# Ad-hoc sign the bundle (required for Apple Silicon compatibility)
codesign --deep --force --sign - "$BUNDLE_NAME" 2>/dev/null || true

# Create DMG
rm -f "$DMG_NAME"
hdiutil create -volname "Shoko Companion" -srcfolder "$BUNDLE_NAME" -ov -format UDZO "$DMG_NAME"

echo "Packaging complete: $DMG_NAME"
