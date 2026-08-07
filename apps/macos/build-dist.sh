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

xcodebuild -project Qwen3TTSStudio.xcodeproj \
    -scheme "Qwen3 TTS Studio" \
    -configuration "$CONFIG" \
    -destination 'platform=macOS' \
    -derivedDataPath "$DERIVED" \
    build CODE_SIGNING_ALLOWED=NO

rm -rf "$DIST"
mkdir -p "$DIST"
ditto "$DERIVED/Build/Products/$CONFIG/$APP_NAME" "$DIST/$APP_NAME"
echo "Built $DIST/$APP_NAME"
