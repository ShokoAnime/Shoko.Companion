#!/usr/bin/env bash
# Publish every RID and package what a Linux box can package.
#
# Usage: scripts/package.sh <version> [rid...]
#
# The Windows installer and the macOS .dmg are not built here: iscc,
# codesign and hdiutil need their own operating systems, and the Gitea
# runner has only Linux. Those two ship as archives of the published
# binary instead; .github/workflows/ still builds the real installers on
# GitHub's matrix.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"

version="${1:-}"
[ -n "$version" ] || { echo "usage: scripts/package.sh <version> [rid...]" >&2; exit 1; }
shift || true

rids=("$@")
[ ${#rids[@]} -gt 0 ] || rids=(linux-x64 linux-arm64 win-x64 osx-arm64)

dist="$repo/dist"
rm -rf "$dist" publish
mkdir -p "$dist"

for rid in "${rids[@]}"; do
    echo "== $rid"
    rm -rf publish
    dotnet publish Shoko.Companion/Shoko.Companion.csproj \
        --configuration Release \
        --runtime "$rid" \
        --self-contained \
        -p:Version="$version" \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        --output publish
    rm -f publish/*.pdb

    case "$rid" in
        linux-*)
            bash packaging/linux/build.sh "$rid"
            mv "shoko-companion-${rid}.AppImage" \
               "$dist/shoko-companion-${rid}-${version}.AppImage"
            ;;
        win-x64)
            (cd publish && zip -qr "$dist/shoko-companion-${rid}-${version}.zip" .)
            ;;
        osx-arm64)
            tar -czf "$dist/shoko-companion-${rid}-${version}.tar.gz" -C publish .
            ;;
        *)
            echo "no packaging rule for $rid" >&2
            exit 1
            ;;
    esac
done

rm -rf publish
(cd "$dist" && sha256sum ./* > SHA256SUMS)

echo
ls -la "$dist"
