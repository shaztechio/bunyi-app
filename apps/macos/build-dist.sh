#!/bin/zsh
# Build Qwen3 TTS Studio and place the .app in dist/macos/ at the repo root.
# Usage: apps/macos/build-dist.sh [Debug|Release]   (default: Release)
set -euo pipefail

cd "$(dirname "$0")"
CONFIG="${1:-Release}"
DERIVED="build/DerivedData"
DIST="$PWD/../../dist/macos"
APP_NAME="Qwen3 TTS Studio.app"

# Regenerate the Xcode project from project.yml when XcodeGen is available.
if command -v xcodegen >/dev/null; then
    xcodegen generate
fi

# NB: do NOT pass CODE_SIGNING_ALLOWED=NO. An unsigned app loses its
# entitlements, so the sandbox never engages and Application Support resolves
# outside the app container — the models folder would look empty and
# re-download. Ad-hoc signing ("-", per project.yml) keeps the sandbox.
xcodebuild -project Qwen3TTSStudio.xcodeproj \
    -scheme "Qwen3 TTS Studio" \
    -configuration "$CONFIG" \
    -destination 'platform=macOS' \
    -derivedDataPath "$DERIVED" \
    build

rm -rf "$DIST"
mkdir -p "$DIST"
ditto "$DERIVED/Build/Products/$CONFIG/$APP_NAME" "$DIST/$APP_NAME"
echo "Built $DIST/$APP_NAME"
