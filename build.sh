#!/bin/sh
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
app=${1:-"$root/.build/MonoRelay.app"}
case "$app" in /*) ;; *) app="$PWD/$app" ;; esac
if [ -e "$app" ]; then
    printf 'Build destination already exists: %s\n' "$app" >&2
    exit 1
fi
mkdir -p "$(dirname -- "$app")"
stage=$(mktemp -d "$(dirname -- "$app")/.questcast.XXXXXX")
trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
bundle="$stage/MonoRelay.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources" "$stage/MonoRelay.iconset"
cp "$root/Resources/Info.plist" "$bundle/Contents/Info.plist"
cp "$root/Resources/stream-presets.json" "$root/Resources/MonoRelay.svg" "$bundle/Contents/Resources/"
xcrun swiftc -swift-version 5 -parse-as-library -O -target "$(uname -m)-apple-macos14.0" \
    "$root"/Sources/*.swift -o "$bundle/Contents/MacOS/MonoRelay"
icon="$bundle/Contents/Resources/MonoRelay.svg"
for size in 16 32 128 256 512; do
    sips -s format png -z "$size" "$size" "$icon" --out "$stage/MonoRelay.iconset/icon_${size}x${size}.png" >/dev/null
    retina=$((size * 2))
    sips -s format png -z "$retina" "$retina" "$icon" --out "$stage/MonoRelay.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$stage/MonoRelay.iconset" -o "$bundle/Contents/Resources/MonoRelay.icns"
codesign --force --sign "${MONORELAY_SIGN_IDENTITY:--}" "$bundle"
codesign --verify --strict "$bundle"
mv -- "$bundle" "$app"
printf '%s\n' "$app"
