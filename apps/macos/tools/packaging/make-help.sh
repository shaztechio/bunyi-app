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
# Builds Bunyi.help from HELP.md and installs it into an app bundle.
#
#   ./apps/macos/tools/packaging/make-help.sh [path/to/Bunyi.app]
#
# Defaults to dist/macos/Bunyi.app. project.yml runs this as a post-build script
# with the bundle Xcode just produced, so ⌘R and build-dist.sh both get help
# without a separate step.
#
# The book is generated rather than committed: an HTML file and a search index
# checked in beside the Markdown they come from is two copies of the same text,
# and the one nobody edits goes stale silently.

set -euo pipefail

root="$(cd "$(dirname "$0")/../../../.." && pwd)"
packaging="$root/apps/macos/tools/packaging"
app="${1:-$root/dist/macos/Bunyi.app}"
source_markdown="$root/apps/macos/HELP.md"
icon="$root/apps/macos/Assets.xcassets/AppIcon.appiconset/icon-256pt@1x.png"

[ -d "$app" ] || { printf 'error: %s does not exist.\n' "$app" >&2; exit 1; }
[ -f "$source_markdown" ] || { printf 'error: %s is missing.\n' "$source_markdown" >&2; exit 1; }

contents="$app/Contents"
help_book="$contents/Resources/Bunyi.help"
help_lproj="$help_book/Contents/Resources/en.lproj"
help_identifier="app.bunyi.help"
help_title="Bunyi Help"

# Rebuild from empty. A stale Bunyi.html left behind by a rename would still be
# indexed and still be served.
rm -rf "$help_book"
mkdir -p "$help_lproj" "$help_book/Contents/Resources/shrd"

cp "$packaging/BunyiHelp-Info.plist" "$help_book/Contents/Info.plist"
cp "$packaging/BunyiHelp-InfoPlist.strings" "$help_lproj/InfoPlist.strings"
[ -f "$icon" ] && cp "$icon" "$help_book/Contents/Resources/shrd/Bunyi.png"

# Keep the help book's version in step with the app. helpd caches a registered
# book by identifier and version, so a frozen version makes it keep serving
# stale content after the help source changes.
#
# Xcode's environment is the source of truth when this runs as a build phase.
# Reading the bundle's Info.plist instead only ever worked by accident: on a
# clean build the "Process Info.plist" phase has not run yet, so the file does
# not exist, and incremental builds were reading the PREVIOUS build's copy.
if [ -n "${MARKETING_VERSION:-}" ]; then
    app_short_version="$MARKETING_VERSION"
    app_build_version="${CURRENT_PROJECT_VERSION:-1}"
elif [ -f "$contents/Info.plist" ]; then
    app_short_version="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$contents/Info.plist")"
    app_build_version="$(/usr/libexec/PlistBuddy -c "Print :CFBundleVersion" "$contents/Info.plist")"
else
    printf 'error: no version available — neither MARKETING_VERSION nor %s\n' \
        "$contents/Info.plist" >&2
    exit 1
fi
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $app_short_version" "$help_book/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $app_build_version" "$help_book/Contents/Info.plist"

# Help Viewer resolves a book by this identifier, which has to match on both
# sides. project.yml is what sets CFBundleHelpBookName and CFBundleHelpBookFolder;
# this only repairs a bundle built some other way. Skipped when the plist is not
# there yet, because Xcode is about to write it — creating one here would be
# overwritten at best and would corrupt the bundle at worst.
if [ -f "$contents/Info.plist" ]; then
    /usr/libexec/PlistBuddy -c "Set :CFBundleHelpBookName $help_identifier" "$contents/Info.plist" 2>/dev/null \
      || /usr/libexec/PlistBuddy -c "Add :CFBundleHelpBookName string $help_identifier" "$contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleHelpBookFolder Bunyi.help" "$contents/Info.plist" 2>/dev/null \
      || /usr/libexec/PlistBuddy -c "Add :CFBundleHelpBookFolder string Bunyi.help" "$contents/Info.plist"
fi

swift "$packaging/render-help.swift" "$source_markdown" "$help_lproj/Bunyi.html" "$help_identifier"

# lsm is the indexer Help Viewer expects; without an index the book still opens
# but the search field returns nothing, which reads as a broken Help window.
hiutil -I lsm -C -ag -s en -l en -f "$help_lproj/Bunyi.helpindex" "$help_lproj"

test -s "$help_lproj/Bunyi.html"
test -s "$help_lproj/Bunyi.helpindex"

printf 'Built %s\n' "$help_book"
