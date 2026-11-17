#!/bin/sh
# Requires Android SDK 36 and a JDK compatible with the upstream Gradle build.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
source="$root/.build/upstream"
version=889bae6a92172f4fb8fa19ca173e330ef64e2abf
output=${1:-"$root/.build/MonoRelay-Android.apk"}
case "$output" in /*) ;; *) output="$PWD/$output" ;; esac
test ! -e "$output"
mkdir -p "$root/.build"
if [ ! -d "$source/.git" ]; then
    git clone --depth 1 --branch v3.5.7 https://github.com/dhun2666/scrcpy-client-for-android.git "$source"
fi
if [ "$(git -C "$source" rev-parse HEAD)" != "$version" ]; then
    printf '%s\n' 'The checked-out Android engine does not match the pinned commit.' >&2
    exit 1
fi
if git -C "$source" apply --check "$root/upstream.patch"; then
    git -C "$source" apply "$root/upstream.patch"
else
    git -C "$source" apply --reverse --check "$root/upstream.patch"
fi
cp "$root/../Resources/stream-presets.json" "$source/app/src/main/assets/"
(
    cd "$source"
    ./gradlew --no-daemon :app:assembleDebug
)
apk=$(find "$source/app/build/outputs/apk/debug" -maxdepth 1 -name '*.apk' | head -1)
test -f "$apk"
mkdir -p "$(dirname -- "$output")"
cp "$apk" "$output"
printf '%s\n' "$output"
