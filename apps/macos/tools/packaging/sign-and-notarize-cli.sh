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
# Developer ID sign and notarize the standalone macOS CLI archive.
# Run apps/macos/build-cli-dist.sh Release first.

set -euo pipefail

root="$(cd "$(dirname "$0")/../../../.." && pwd)"
project="$root/apps/macos/project.yml"
short="$(sed -n 's/^ *MARKETING_VERSION: *//p' "$project" | head -1)"
build="$(sed -n 's/^ *CURRENT_PROJECT_VERSION: *//p' "$project" | head -1)"
name="Bunyi-CLI-$short-$build-macos-arm64"
archive="${BUNYI_CLI_ARCHIVE:-$root/dist/macos/$name.zip}"
profile="${BUNYI_NOTARY_PROFILE:-bunyi}"

if [ ! -f "$archive" ]; then
  printf 'error: %s does not exist. Run apps/macos/build-cli-dist.sh first.\n' \
    "$archive" >&2
  exit 1
fi
identity="${BUNYI_SIGN_IDENTITY:-}"
if [ -z "$identity" ]; then
  found=()
  while IFS= read -r line; do
    [ -n "$line" ] && found+=("$line")
  done < <(security find-identity -v -p codesigning \
    | sed -n 's/.*"\(Developer ID Application: [^"]*\)".*/\1/p')
  if [ "${#found[@]}" -eq 1 ]; then
    identity="${found[0]}"
  elif [ "${#found[@]}" -eq 0 ]; then
    printf 'error: no Developer ID Application certificate found.\n' >&2
    exit 1
  else
    printf 'error: several Developer ID Application certificates found; set BUNYI_SIGN_IDENTITY.\n' >&2
    exit 1
  fi
fi
printf 'Signing CLI as: %s\n' "$identity"

work="$(mktemp -d "${TMPDIR:-/tmp}/bunyi-cli-sign.XXXXXX")"
trap 'rm -rf "$work"' EXIT
ditto -x -k "$archive" "$work"
package="$work/$name"
binary="$package/bunyi"
if [ ! -x "$binary" ]; then
  printf 'error: archive does not contain executable %s.\n' "$binary" >&2
  exit 1
fi

while IFS= read -r nested; do
  codesign --force --options runtime --timestamp --sign "$identity" "$nested"
done < <(find "$package" \( -name '*.framework' -o -name '*.dylib' \) -print)
codesign --force --options runtime --timestamp --sign "$identity" "$binary"
codesign --verify --strict --verbose=2 "$binary"
while IFS= read -r nested; do
  codesign --verify --strict --verbose=2 "$nested"
  codesign -dvvv "$nested" 2>&1 | grep -q 'flags=.*runtime'
done < <(find "$package" \( -name '*.framework' -o -name '*.dylib' \) -print)
codesign -dvvv "$binary" 2>&1 | grep -q 'flags=.*runtime'
test "$(file -b "$binary")" = "Mach-O 64-bit executable arm64"
vtool -show-build "$binary" | grep -Eq 'minos 15(\.0+)?$'
"$binary" version --json | python3 -c \
  'import json,sys; d=json.load(sys.stdin); assert d["ok"] and d["version"]'

signed="$work/$name.zip"
ditto -c -k --sequesterRsrc --keepParent "$package" "$signed"
ditto "$signed" "$archive"

notarize() {
  if [ -n "${BUNYI_APPLE_ID:-}" ] && [ -n "${BUNYI_TEAM_ID:-}" ] \
     && [ -n "${BUNYI_APP_PASSWORD:-}" ]; then
    xcrun notarytool submit "$1" \
      --apple-id "$BUNYI_APPLE_ID" \
      --team-id "$BUNYI_TEAM_ID" \
      --password "$BUNYI_APP_PASSWORD" \
      --wait
  else
    xcrun notarytool submit "$1" --keychain-profile "$profile" --wait
  fi
}

printf 'Submitting the CLI archive for notarization…\n'
notarize "$archive"
# `spctl --assess --type execute` only accepts app-style bundles. It reports a
# correctly signed and notarized standalone Mach-O tool as "does not seem to be
# an app", so the authoritative CLI checks are notarytool's accepted result
# above plus the strict hardened-runtime signature checks performed before it.
printf 'Signed and notarized: %s\n' "$archive"
