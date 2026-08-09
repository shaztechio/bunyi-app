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
# Rewrites the background reference inside tools/packaging/dmg-layout.DS_Store
# so it points at THIS project's disk image.
#
#   ./apps/macos/tools/packaging/capture-dmg-background.sh
#
# Finder stores the background picture in the layout's `icvp` record as a
# reference to a file on the mounted volume, not as a relative path. A layout
# copied from another project therefore names that project's volume, resolves
# to nothing here, and Finder silently falls back to the plain background
# colour — the .tiff ships inside the image and is never drawn.
#
# Hand-patching the old AliasRecord's strings is not enough: the record also
# carries volume identifiers and file ids from the machine that captured it.
# So this mounts a real read-write volume named "Bunyi", puts the artwork
# where the image will have it, and asks macOS itself to produce the
# reference — correct by construction rather than by editing.

set -euo pipefail

root="$(cd "$(dirname "$0")/../../../.." && pwd)"
packaging="$root/apps/macos/tools/packaging"
layout="$packaging/dmg-layout.DS_Store"
background="$packaging/dmg-background.tiff"
volume_name="Bunyi"

[ -f "$layout" ] || { printf 'error: %s is missing.\n' "$layout" >&2; exit 1; }
[ -f "$background" ] || { printf 'error: %s is missing.\n' "$background" >&2; exit 1; }

if [ -d "/Volumes/$volume_name" ]; then
    printf 'error: /Volumes/%s is already mounted. Eject it first.\n' "$volume_name" >&2
    exit 1
fi

work="$(mktemp -d)"
mounted=""
cleanup() {
    [ -n "$mounted" ] && hdiutil detach "$mounted" -quiet 2>/dev/null || true
    rm -rf "$work"
}
trap cleanup EXIT

# A small read-write image is enough: only the path and the volume matter.
hdiutil create -size 10m -fs HFS+ -volname "$volume_name" -quiet "$work/scratch.dmg"
mounted="$(hdiutil attach "$work/scratch.dmg" -nobrowse -noautoopen | awk -F'\t' 'END{print $NF}')"
[ -d "$mounted" ] || { printf 'error: the scratch volume did not mount.\n' >&2; exit 1; }

mkdir -p "$mounted/.background"
cp "$background" "$mounted/.background/background.tiff"

cat > "$work/bookmark.swift" <<'SWIFT'
import Foundation

// Bookmark data is what current Finder writes for the background picture, and
// it is what Finder resolves on mount. Generating it against a live volume is
// the whole point of this script.
let path = CommandLine.arguments[1]
let out = CommandLine.arguments[2]
let url = URL(fileURLWithPath: path)
let data = try (url as NSURL).bookmarkData(
    options: [.suitableForBookmarkFile],
    includingResourceValuesForKeys: nil,
    relativeTo: nil
)
try data.write(to: URL(fileURLWithPath: out))
FileHandle.standardError.write(Data("bookmark: \(data.count) bytes for \(path)\n".utf8))
SWIFT

swift "$work/bookmark.swift" "$mounted/.background/background.tiff" "$work/bookmark.bin"

python3 "$packaging/set-dmg-background.py" "$layout" "$work/bookmark.bin"

printf 'Updated %s\n' "$layout"
printf 'Rebuild the image with make-dmg.sh, then open it to check the artwork.\n'
