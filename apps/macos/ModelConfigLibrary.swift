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

/// The three per-mode model sources, saved under a name.
///
/// Worth naming and keeping because the values are long, easy to mistype, and
/// come in sets: switching between Hugging Face and a self-hosted mirror means
/// changing all three together, and getting one wrong loads a model that runs
/// and produces nonsense.
struct ModelConfig: Identifiable, Codable, Hashable {
    let id: UUID
    var name: String
    /// Empty means "use the built-in default for that mode" — the same meaning
    /// an empty field has in Settings, so a saved blank is not a broken value.
    var presetVoice: String
    var voiceDesign: String
    var voiceClone: String
    var savedAt: Date

    /// A one-line summary for the list: what this config actually points at.
    var summary: String {
        let values = [presetVoice, voiceDesign, voiceClone]
        if values.allSatisfy(\.isEmpty) { return "All three on the defaults" }
        let hosts = Set(values.compactMap { value -> String? in
            guard !value.isEmpty else { return nil }
            if let url = URL(string: value), let host = url.host { return host }
            return value.split(separator: "/").first.map(String.init)
        })
        let blanks = values.filter(\.isEmpty).count
        let where_ = hosts.sorted().joined(separator: ", ")
        return blanks == 0 ? where_ : "\(where_), \(blanks) on the default"
    }
}

@MainActor
@Observable
final class ModelConfigLibrary {
    private(set) var configs: [ModelConfig] = []

    private let log = LogStore.shared

    /// Application Support, not the models folder: these are a few hundred
    /// bytes describing *where* models come from, and must not travel with a
    /// relocated models directory — or vanish when one is deleted.
    private let dir: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory,
                                           in: .userDomainMask)[0]
            .appendingPathComponent("Bunyi/ModelConfigs", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private var indexURL: URL { dir.appendingPathComponent("configs.json") }

    init() { load() }

    @discardableResult
    func save(name: String, presetVoice: String, voiceDesign: String,
              voiceClone: String) throws -> ModelConfig {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let config = ModelConfig(
            id: UUID(),
            name: trimmed.isEmpty ? "Untitled" : trimmed,
            presetVoice: presetVoice.trimmingCharacters(in: .whitespaces),
            voiceDesign: voiceDesign.trimmingCharacters(in: .whitespaces),
            voiceClone: voiceClone.trimmingCharacters(in: .whitespaces),
            savedAt: Date()
        )
        // Replacing by name rather than accumulating near-duplicates: saving
        // "Self-hosted" twice means correcting it, not keeping both.
        configs.removeAll { $0.name.caseInsensitiveCompare(config.name) == .orderedSame }
        configs.append(config)
        configs.sort { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        try persist()
        log.log("Saved model configuration \"\(config.name)\"")
        return config
    }

    func delete(_ config: ModelConfig) {
        configs.removeAll { $0.id == config.id }
        try? persist()
        log.log("Deleted model configuration \"\(config.name)\"")
    }

    // MARK: Persistence

    private func load() {
        guard let data = try? Data(contentsOf: indexURL) else { return }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        configs = (try? decoder.decode([ModelConfig].self, from: data)) ?? []
    }

    private func persist() throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(configs).write(to: indexURL, options: .atomic)
    }
}
