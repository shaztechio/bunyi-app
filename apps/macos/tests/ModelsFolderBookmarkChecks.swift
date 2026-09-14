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

@main
@MainActor
enum ModelsFolderBookmarkChecks {
    static func main() throws {
        let suite = "app.bunyi.bookmark-tests.\(UUID().uuidString)"
        guard let defaults = UserDefaults(suiteName: suite) else {
            throw CocoaError(.featureUnsupported)
        }
        defer { defaults.removePersistentDomain(forName: suite) }

        let legacy = Data([0x01, 0x02, 0x03])
        defaults.set(legacy, forKey: ModelsFolderBookmarkStore.legacyKey)
        precondition(ModelsFolderBookmarkStore.read(from: defaults) == legacy)
        precondition(defaults.data(forKey: ModelsFolderBookmarkStore.key) == legacy)
        precondition(defaults.object(
            forKey: ModelsFolderBookmarkStore.legacyKey) == nil)

        let replacement = Data([0x04, 0x05])
        ModelsFolderBookmarkStore.write(replacement, to: defaults)
        precondition(ModelsFolderBookmarkStore.read(from: defaults) == replacement)

        ModelsFolderBookmarkStore.clear(from: defaults)
        precondition(ModelsFolderBookmarkStore.read(from: defaults) == nil)
        print("Models-folder checks passed: legacy bookmark migration, write and reset.")
    }
}
