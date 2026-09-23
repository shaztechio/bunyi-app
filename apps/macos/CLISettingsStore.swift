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

public struct CLISettings: Codable, Sendable {
    public var sentenceGapMilliseconds: Int? = nil
    public var modelsFolder: String? = nil
    public var unloadOnModeSwitch: Bool? = nil
    public var modelSource: [String: String]? = nil

    public static let empty = CLISettings()
}

public enum CLISettingsStore {
    public static var isCLI: Bool {
        Bundle.main.bundleIdentifier == "app.bunyi.cli"
    }

    public static var dataRoot: URL {
        FileManager.default.urls(
            for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Bunyi", isDirectory: true)
    }

    public static var settingsURL: URL {
        dataRoot.appendingPathComponent("settings.json")
    }

    public static func load() throws -> CLISettings {
        guard FileManager.default.fileExists(atPath: settingsURL.path) else {
            return .empty
        }
        return try JSONDecoder().decode(
            CLISettings.self, from: Data(contentsOf: settingsURL))
    }

    public static func loadLenient() -> CLISettings {
        (try? load()) ?? .empty
    }

    public static func save(_ settings: CLISettings) throws {
        try FileManager.default.createDirectory(
            at: dataRoot, withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700])
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o700], ofItemAtPath: dataRoot.path)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(settings).write(to: settingsURL, options: .atomic)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600], ofItemAtPath: settingsURL.path)
    }

    public static func source(for mode: TTSMode) -> String? {
        loadLenient().modelSource?[modeKey(mode)]
    }

    public static func modeKey(_ mode: TTSMode) -> String {
        switch mode {
        case .presetVoice: "preset"
        case .voiceDesign: "design"
        case .voiceClone: "clone"
        }
    }
}
