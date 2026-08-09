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
# Build Bunyi and place the .app in dist/macos/ at the repo root.
# Usage: apps/macos/build-dist.sh [Debug|Release]   (default: Release)
set -euo pipefail

cd "$(dirname "$0")"
CONFIG="${1:-Release}"
DERIVED="build/DerivedData"
DIST="$PWD/../../dist/macos"
APP_NAME="Bunyi.app"

# Regenerate the Xcode project from project.yml when XcodeGen is available.
if command -v xcodegen >/dev/null; then
    xcodegen generate
fi

# NB: do NOT pass CODE_SIGNING_ALLOWED=NO. An unsigned app loses its
# entitlements, so the sandbox never engages and Application Support resolves
# outside the app container — the models folder would look empty and
# re-download. Ad-hoc signing ("-", per project.yml) keeps the sandbox.
xcodebuild -project Bunyi.xcodeproj \
    -scheme "Bunyi" \
    -configuration "$CONFIG" \
    -destination 'platform=macOS' \
    -derivedDataPath "$DERIVED" \
    build

rm -rf "$DIST"
mkdir -p "$DIST"
ditto "$DERIVED/Build/Products/$CONFIG/$APP_NAME" "$DIST/$APP_NAME"
echo "Built $DIST/$APP_NAME"
