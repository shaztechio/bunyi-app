//
//  VoiceLibrary.swift
//  Qwen3 TTS Studio
//
//  Saved voice-clone prompts: a name, the reference clip, and its
//  transcript. Cloned voices can't become real model presets (those are
//  trained speaker tokens in spkId), but saving the recipe makes them
//  reusable in one click.
//

import Foundation

struct SavedVoice: Identifiable, Codable, Hashable {
    let id: UUID
    var name: String
    var fileName: String
    var transcript: String
    var createdAt: Date
}

@MainActor
@Observable
final class VoiceLibrary {
    private(set) var voices: [SavedVoice] = []

    private let log = LogStore.shared

    /// Kept in the app's own Application Support, not the models folder —
    /// these are small and shouldn't move with a custom models location.
    private let dir: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory,
                                           in: .userDomainMask)[0]
            .appendingPathComponent("Qwen3TTSStudio/Voices", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private var indexURL: URL { dir.appendingPathComponent("voices.json") }

    init() { load() }

    func audioURL(for voice: SavedVoice) -> URL {
        dir.appendingPathComponent(voice.fileName)
    }

    func voice(with id: UUID?) -> SavedVoice? {
        guard let id else { return nil }
        return voices.first { $0.id == id }
    }

    /// Copies the clip into the container so it survives relaunches without
    /// needing a security-scoped bookmark for the user's original file.
    @discardableResult
    func save(name: String, audioURL source: URL,
              transcript: String) throws -> SavedVoice {
        let id = UUID()
        let ext = source.pathExtension.isEmpty ? "wav" : source.pathExtension
        let fileName = "\(id.uuidString).\(ext)"

        let scoped = source.startAccessingSecurityScopedResource()
        defer { if scoped { source.stopAccessingSecurityScopedResource() } }
        try FileManager.default.copyItem(
            at: source, to: dir.appendingPathComponent(fileName))

        let voice = SavedVoice(id: id, name: name, fileName: fileName,
                               transcript: transcript, createdAt: .now)
        voices.append(voice)
        sortVoices()
        persist()
        log.log("Saved voice \"\(name)\"")
        return voice
    }

    func delete(_ voice: SavedVoice) {
        try? FileManager.default.removeItem(at: audioURL(for: voice))
        voices.removeAll { $0.id == voice.id }
        persist()
        log.log("Deleted voice \"\(voice.name)\"")
    }

    private func sortVoices() {
        voices.sort {
            $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
    }

    private func load() {
        guard let data = try? Data(contentsOf: indexURL),
              let decoded = try? JSONDecoder().decode([SavedVoice].self, from: data)
        else { return }
        // Drop entries whose audio went missing so the picker never offers a
        // voice that can't be generated.
        voices = decoded.filter {
            FileManager.default.fileExists(atPath: audioURL(for: $0).path)
        }
        sortVoices()
    }

    private func persist() {
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(voices).write(to: indexURL, options: .atomic)
        } catch {
            log.log("Couldn't save the voice library: \(error.localizedDescription)")
        }
    }
}
