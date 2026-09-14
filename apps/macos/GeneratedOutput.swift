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

import Foundation

/// One generated WAV on disk. `id` is the URL: the folder is the record, so a
/// file that disappears from the folder disappears from History.
public struct GeneratedOutput: Identifiable, Hashable, Sendable {
    public let url: URL
    public let created: Date
    public let byteCount: Int64

    public init(url: URL, created: Date, byteCount: Int64) {
        self.url = url
        self.created = created
        self.byteCount = byteCount
    }

    public var id: URL { url }
    public var name: String { url.deletingPathExtension().lastPathComponent }

    /// Filenames are `<Mode>-<ISO8601 timestamp>.wav`, so the mode is the part
    /// before the first dash. Falls back to the whole name for anything the
    /// user dropped in the folder themselves.
    public var mode: String {
        let parts = name.split(separator: "-", maxSplits: 1)
        return parts.count == 2 ? String(parts[0]) : name
    }
}
