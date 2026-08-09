#!/bin/bash
# Copyright 2026 Shazron Abdullah and Bunyi contributors
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
#
# Builds the drag-to-Applications disk image from dist/macos/Bunyi.app.
#
#   ./apps/macos/tools/packaging/make-dmg.sh [output.dmg]
#
# The window layout comes from a committed .DS_Store rather than from driving
# Finder with AppleScript at build time. Finder scripting needs a real desktop
# session and a volume mounted where Finder can see it; on a CI runner that is
# either flaky or impossible. Capturing the layout once and replaying it is
# deterministic and needs nothing but hdiutil.
#
# The committed layout came from Sandfort, with the app record's filename key
# rewritten from "Sandfort.app" to "Bunyi.app" — .DS_Store positions are keyed by
# filename, so the copied file would otherwise have left Bunyi.app unplaced while
# still positioning Applications. To change the layout: mount a read-write image,
# arrange it in Finder, and copy the resulting .DS_Store over
# tools/packaging/dmg-layout.DS_Store. "Bunyi.app" and "Applications" must keep
# those exact names.
#
# The image still builds if the layout is missing; the window just opens with
# Finder's default arrangement.

set -euo pipefail

root="$(cd "$(dirname "$0")/../../../.." && pwd)"
app="$root/dist/macos/Bunyi.app"
layout="$root/apps/macos/tools/packaging/dmg-layout.DS_Store"
background="$root/apps/macos/tools/packaging/dmg-background.tiff"
volume_name="Bunyi"

[ -d "$app" ] || {
  printf 'error: %s does not exist. Run apps/macos/build-dist.sh first.\n' "$app" >&2
  exit 1
}

# build-dist.sh ad-hoc signs, so running this straight afterwards would wrap an
# app that cannot be distributed inside a properly signed image: the container
# passes Gatekeeper and the app inside is rejected. Say so rather than produce it
# quietly. The release path signs the app first, so this only warns for a local
# build.
if codesign -dvvv "$app" 2>&1 | grep -q 'Signature=adhoc'; then
  printf 'warning: %s is ad-hoc signed, not Developer ID signed.\n' "$app" >&2
  printf 'The image will build, but the app inside it will fail Gatekeeper.\n' >&2
  printf 'Run tools/packaging/sign-and-notarize.sh for a distributable image.\n\n' >&2
fi

short="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$app/Contents/Info.plist")"
build="$(/usr/libexec/PlistBuddy -c 'Print CFBundleVersion' "$app/Contents/Info.plist")"
output="${1:-$root/dist/macos/Bunyi-$short-$build.dmg}"

staging="$(mktemp -d)"
cleanup() { rm -rf "$staging"; }
trap cleanup EXIT

# ditto rather than cp: it preserves the bundle's extended attributes and the
# code signature, which a naive copy can strip.
ditto "$app" "$staging/Bunyi.app"
ln -s /Applications "$staging/Applications"
[ -f "$layout" ] && cp "$layout" "$staging/.DS_Store"

# The .DS_Store references .background/background.tiff by path, so the artwork has
# to travel with it or the window falls back to a plain background.
if [ -f "$background" ]; then
  mkdir -p "$staging/.background"
  cp "$background" "$staging/.background/background.tiff"
fi

rm -f "$output"

# Built read-write first, then converted. The background picture is referenced
# from the .DS_Store by a bookmark that encodes the volume's identity, so it has
# to be created against the very volume that ships. A bookmark made anywhere
# else -- a scratch image, another project's volume -- resolves "stale", and
# Finder will not draw a background from a stale bookmark. It falls back to the
# plain colour with no error, which looks identical to forgetting the artwork.
rw_image="$staging.rw.dmg"
rm -f "$rw_image"
hdiutil create \
  -volname "$volume_name" \
  -srcfolder "$staging" \
  -format UDRW \
  -ov -quiet \
  "$rw_image"

# The mount point is not on hdiutil's last line -- a partitioned image prints
# scheme entries after it, with an empty mount-point column -- so pick the line
# that actually has one rather than the last line.
mount_point="$(hdiutil attach "$rw_image" -nobrowse -noautoopen \
  | grep -o '/Volumes/.*' | head -1 | sed 's/[[:space:]]*$//')"
[ -d "$mount_point" ] || { printf 'error: the read-write image did not mount.\n' >&2; exit 1; }

if [ -f "$layout" ] && [ -f "$mount_point/.background/background.tiff" ]; then
    bookmark="$staging.bookmark"
    swift "$root/apps/macos/tools/packaging/make-background-bookmark.swift" \
        "$mount_point/.background/background.tiff" "$bookmark"
    python3 "$root/apps/macos/tools/packaging/set-dmg-background.py" \
        "$mount_point/.DS_Store" "$bookmark"
    rm -f "$bookmark"
fi

hdiutil detach "$mount_point" -quiet

# UDZO is the widely compatible compressed format. UDBZ and ULFO compress a
# little better but are not worth a format question on someone else's Mac.
hdiutil convert "$rw_image" -format UDZO -o "$output" -quiet
rm -f "$rw_image"

printf 'Built %s\n' "$output"

# The disk image is signed too, not just the app inside it. An unsigned image
# still mounts, but signing it lets Gatekeeper evaluate the container the user
# actually downloaded rather than only what falls out of it.
identity="${BUNYI_SIGN_IDENTITY:-}"
if [ -z "$identity" ]; then
  found=""
  while IFS= read -r line; do
    [ -n "$line" ] && [ -z "$found" ] && found="$line"
  done < <(security find-identity -v -p codesigning \
    | sed -n 's/.*"\(Developer ID Application: [^"]*\)".*/\1/p')
  identity="$found"
fi

if [ -n "$identity" ]; then
  codesign --force --timestamp --sign "$identity" "$output"
  codesign --verify --strict "$output"
  printf 'Signed the disk image as: %s\n' "$identity"
else
  printf 'No Developer ID identity found; the disk image is unsigned.\n' >&2
  printf 'Fine for a local build, not for distribution.\n' >&2
fi
