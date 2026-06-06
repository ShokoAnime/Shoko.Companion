#!/usr/bin/env bash
set -euo pipefail

RID=$1
PUBLISH_DIR="publish"

case "$RID" in
    linux-x64)   ARCH=x86_64  ;;
    linux-arm64) ARCH=aarch64 ;;
    *) echo "Unknown RID: $RID"; exit 1 ;;
esac

APPDIR="$(dirname "$0")/AppDir"
BUILD_DIR="$(mktemp -d)"
OUTPUT_NAME="shoko-companion-${RID}.AppImage"

# Download appimagetool (always x86_64 — runner is x86_64; ARCH env controls target)
APPIMAGETOOL_IMG="$BUILD_DIR/appimagetool.AppImage"
wget -qO "$APPIMAGETOOL_IMG" "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
chmod +x "$APPIMAGETOOL_IMG"

# Extract to bypass FUSE requirement on CI runners
(cd "$BUILD_DIR" && "$APPIMAGETOOL_IMG" --appimage-extract >/dev/null 2>&1)
APPIMAGETOOL="$BUILD_DIR/squashfs-root/AppRun"

# Build AppDir with the published binary
mkdir -p "$BUILD_DIR/AppDir/usr/bin"
cp "$PUBLISH_DIR/shoko-companion" "$BUILD_DIR/AppDir/usr/bin/shoko-companion"
cp "$APPDIR/shoko-companion.desktop" "$BUILD_DIR/AppDir/"
cp "$APPDIR/shoko-companion.png" "$BUILD_DIR/AppDir/"

# Create AppRun entry point
cat > "$BUILD_DIR/AppDir/AppRun" <<'RUNEOF'
#!/usr/bin/env bash
set -euo pipefail
APPDIR="$(dirname "$(readlink -f "$0")")"
exec "$APPDIR/usr/bin/shoko-companion" "$@"
RUNEOF
chmod +x "$BUILD_DIR/AppDir/AppRun"

# Generate AppImage
ARCH="$ARCH" "$APPIMAGETOOL" "$BUILD_DIR/AppDir" "$OUTPUT_NAME"

echo "Packaging complete: $OUTPUT_NAME"
