// Copyright 2026 Shazron Abdullah and Bunyi contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Writes bookmark data for a file to a path.
//
//   swift make-background-bookmark.swift <file> <output.bin>
//
// make-dmg.sh runs this against the mounted read-write image, before that
// image is converted to the compressed one that ships. The volume identity
// baked into the bookmark is therefore the identity of the volume users
// actually mount. A bookmark captured against any other volume resolves
// "stale", and Finder will not draw a background from a stale bookmark --
// it silently falls back to the plain colour, which looks exactly like
// having forgotten the artwork.

import Foundation

guard CommandLine.arguments.count == 3 else {
    FileHandle.standardError.write(Data(
        "usage: make-background-bookmark.swift <file> <output.bin>\n".utf8
    ))
    exit(64)
}

let source = URL(fileURLWithPath: CommandLine.arguments[1])
let output = URL(fileURLWithPath: CommandLine.arguments[2])

let data = try (source as NSURL).bookmarkData(
    options: [],
    includingResourceValuesForKeys: nil,
    relativeTo: nil
)
try data.write(to: output)

// Resolving it here is the check that matters: a bookmark that comes back
// stale on the machine that just made it will never work anywhere else.
var stale = false
_ = try? URL(resolvingBookmarkData: data, options: [], relativeTo: nil,
             bookmarkDataIsStale: &stale)
if stale {
    FileHandle.standardError.write(Data(
        "warning: the bookmark resolved stale on creation\n".utf8
    ))
}
FileHandle.standardError.write(Data("bookmark: \(data.count) bytes\n".utf8))
