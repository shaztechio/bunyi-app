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

/// Stores the macOS security-scoped bookmark under the settings key named by
/// DATA-FORMATS. The first implementation used an internal-mechanics suffix;
/// read migrates that key atomically from the caller's point of view.
@MainActor
enum ModelsFolderBookmarkStore {
    static let key = "modelsFolder"
    static let legacyKey = "modelsFolderBookmark"

    static func read(from defaults: UserDefaults = .standard) -> Data? {
        if let data = defaults.data(forKey: key) { return data }
        guard let legacy = defaults.data(forKey: legacyKey) else { return nil }
        defaults.set(legacy, forKey: key)
        defaults.removeObject(forKey: legacyKey)
        return legacy
    }

    static func write(_ data: Data, to defaults: UserDefaults = .standard) {
        defaults.set(data, forKey: key)
        defaults.removeObject(forKey: legacyKey)
    }

    static func clear(from defaults: UserDefaults = .standard) {
        defaults.removeObject(forKey: key)
        defaults.removeObject(forKey: legacyKey)
    }
}
