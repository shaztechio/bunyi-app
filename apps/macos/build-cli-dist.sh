#!/bin/zsh
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
# Build the standalone Apple-Silicon CLI archive in dist/macos/.
# Usage: apps/macos/build-cli-dist.sh [Debug|Release]   (default: Release)

set -euo pipefail

script_dir="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$script_dir/../.." && pwd)"
configuration="${1:-Release}"
derived="$script_dir/build/CLI-DerivedData"
dist="$root/dist/macos"
project="$script_dir/project.yml"

case "$configuration" in
  Debug|Release) ;;
  *) printf 'error: configuration must be Debug or Release.\n' >&2; exit 2 ;;
esac

if command -v xcodegen >/dev/null; then
  (cd "$script_dir" && xcodegen generate)
fi

xcodebuild -project "$script_dir/Bunyi.xcodeproj" \
  -scheme BunyiCLI \
  -configuration "$configuration" \
  -destination 'platform=macOS,arch=arm64' \
  -derivedDataPath "$derived" \
  -quiet \
  build

product="$derived/Build/Products/$configuration"
binary="$product/bunyi"
core_framework="$product/BunyiMLXCore.framework"
metal_bundle="$product/mlx-swift_Cmlx.bundle"
if [ ! -x "$binary" ]; then
  printf 'error: CLI build did not produce an executable at %s.\n' "$binary" >&2
  exit 1
fi
if [ ! -f "$metal_bundle/Contents/Resources/default.metallib" ]; then
  printf 'error: MLX Metal resources are missing from %s.\n' "$metal_bundle" >&2
  exit 1
fi
if [ ! -f "$core_framework/Versions/A/BunyiMLXCore" ]; then
  printf 'error: shared runtime framework is missing from %s.\n' \
    "$core_framework" >&2
  exit 1
fi

short="$(sed -n 's/^ *MARKETING_VERSION: *//p' "$project" | head -1)"
build="$(sed -n 's/^ *CURRENT_PROJECT_VERSION: *//p' "$project" | head -1)"
name="Bunyi-CLI-$short-$build-macos-arm64"
mkdir -p "$dist"
stage="$(mktemp -d "$dist/.bunyi-cli-stage.XXXXXX")"
trap 'rm -rf "$stage"' EXIT
package="$stage/$name"
mkdir -p "$package/Frameworks" "$package/completions/bash" "$package/completions/zsh" \
  "$package/completions/fish"

ditto "$binary" "$package/bunyi"
ditto "$core_framework" "$package/Frameworks/BunyiMLXCore.framework"
ditto "$metal_bundle" "$package/mlx-swift_Cmlx.bundle"
ditto "$root/LICENSE" "$package/LICENSE"
ditto "$root/spec/CREDITS.json" "$package/CREDITS.json"
ditto "$script_dir/CLI.md" "$package/README.md"
ditto "$script_dir/tools/completions/bunyi.bash" \
  "$package/completions/bash/bunyi"
ditto "$script_dir/tools/completions/_bunyi" \
  "$package/completions/zsh/_bunyi"
ditto "$script_dir/tools/completions/bunyi.fish" \
  "$package/completions/fish/bunyi.fish"
chmod 0755 "$package/bunyi"

# The tool and its shared runtime must carry the same local signature before
# the smoke test. Ad-hoc signatures have no Team ID, so library validation
# cannot establish that relationship; the release step replaces these with
# matching hardened-runtime Developer ID signatures before publication.
codesign --force --deep --sign - \
  "$package/Frameworks/BunyiMLXCore.framework"
codesign --force --sign - "$package/bunyi"

test "$(file -b "$package/bunyi")" = "Mach-O 64-bit executable arm64"
core_binary="$package/Frameworks/BunyiMLXCore.framework/Versions/A/BunyiMLXCore"
test "$(file -b "$core_binary")" = "Mach-O 64-bit dynamically linked shared library arm64"
otool -l "$package/bunyi" | grep -q '@executable_path/Frameworks'
unexpected_dependencies() {
  otool -L "$1" | tail -n +2 | awk '{print $1}' \
    | grep -Ev '^(/usr/lib/|/System/Library/|@rpath/BunyiMLXCore\.framework/)' || true
}
for executable in "$package/bunyi" "$core_binary"; do
  unexpected="$(unexpected_dependencies "$executable")"
  if [ -n "$unexpected" ]; then
    printf 'error: %s has an unpackaged dynamic dependency:\n%s\n' \
      "$executable" "$unexpected" >&2
    exit 1
  fi
done
vtool -show-build "$package/bunyi" | grep -Eq 'minos 15(\.0+)?$'
"$package/bunyi" version --json | python3 -c \
  'import json,sys; d=json.load(sys.stdin); assert d["ok"] and d["version"]'

archive="$dist/$name.zip"
rm -f "$archive"
ditto -c -k --sequesterRsrc --keepParent "$package" "$archive"
printf 'Built %s\n' "$archive"
