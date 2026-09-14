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
//  VoiceLibrary.swift
//  Bunyi
//
//  Saved voice-clone prompts: a name, the reference clip, and its
//  transcript. Cloned voices can't become real model presets (those are
//  trained speaker tokens in spkId), but saving the recipe makes them
//  reusable in one click.
//

import Foundation

@MainActor
@Observable
public final class VoiceLibrary {
    public private(set) var voices: [SavedVoice] = []

    private let log = LogStore.shared

    /// Kept in the app's own Application Support, not the models folder —
    /// these are small and shouldn't move with a custom models location.
    private let dir: URL = {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory,
                                           in: .userDomainMask)[0]
            .appendingPathComponent("Bunyi/Voices", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: dir, withIntermediateDirectories: true)
        return dir
    }()

    private var indexURL: URL { dir.appendingPathComponent("voices.json") }

    public init() { load() }

    public func audioURL(for voice: SavedVoice) -> URL {
        dir.appendingPathComponent(voice.fileName)
    }

    public func voice(with id: UUID?) -> SavedVoice? {
        guard let id else { return nil }
        return voices.first { $0.id == id }
    }

    /// Copies the clip into the container so it survives relaunches without
    /// needing a security-scoped bookmark for the user's original file.
    @discardableResult
    public func save(name: String, audioURL source: URL,
              transcript: String,
              transcriptAudioSeconds: TimeInterval? = nil) throws -> SavedVoice {
        let id = UUID()
        let ext = source.pathExtension.isEmpty ? "wav" : source.pathExtension
        let fileName = "\(id.uuidString).\(ext)"

        let scoped = source.startAccessingSecurityScopedResource()
        defer { if scoped { source.stopAccessingSecurityScopedResource() } }
        try FileManager.default.copyItem(
            at: source, to: dir.appendingPathComponent(fileName))

        let voice = SavedVoice(id: id, name: name, fileName: fileName,
                               transcript: transcript,
                               transcriptAudioSeconds: transcriptAudioSeconds,
                               createdAt: .now)
        voices.append(voice)
        sortVoices()
        do {
            try persistStrict()
        } catch {
            voices.removeAll { $0.id == voice.id }
            try? FileManager.default.removeItem(
                at: dir.appendingPathComponent(fileName))
            throw error
        }
        log.log("Saved voice \"\(name)\"")
        return voice
    }

    public func delete(_ voice: SavedVoice) {
        do {
            try deleteStrict(voice)
        } catch {
            log.log("Couldn't delete voice \"\(voice.name)\": \(error.localizedDescription)")
        }
    }

    public func deleteStrict(_ voice: SavedVoice) throws {
        try FileManager.default.removeItem(at: audioURL(for: voice))
        voices.removeAll { $0.id == voice.id }
        try persistStrict()
        log.log("Deleted voice \"\(voice.name)\"")
    }

    /// A legacy voice may have been saved before automatic transcription
    /// completed. Once Generate recovers it, keep the result with the recipe
    /// so future selections do not transcribe the same clip again.
    public func updateTranscript(
        _ transcript: String,
        audioSeconds: TimeInterval?,
        for id: UUID
    ) throws {
        let trimmed = transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              let index = voices.firstIndex(where: { $0.id == id }) else { return }
        let previous = voices[index].transcript
        let previousSeconds = voices[index].transcriptAudioSeconds
        voices[index].transcript = trimmed
        voices[index].transcriptAudioSeconds = audioSeconds
        do {
            try persistStrict()
        } catch {
            voices[index].transcript = previous
            voices[index].transcriptAudioSeconds = previousSeconds
            throw error
        }
        log.log("Updated transcript for saved voice \"\(voices[index].name)\"")
    }

    private func sortVoices() {
        voices.sort {
            $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
        }
    }

    private func load() {
        guard let data = try? Data(contentsOf: indexURL),
              let decoded = try? SavedVoiceFile.decode(data)
        else { return }
        // Drop entries whose audio went missing so the picker never offers a
        // voice that can't be generated.
        let legacyDate = SavedVoiceFile.containsLegacyNumericDate(data)
        voices = decoded.filter {
            FileManager.default.fileExists(atPath: audioURL(for: $0).path)
        }
        sortVoices()
        if legacyDate || voices.count != decoded.count {
            do {
                try persistStrict()
                if legacyDate {
                    log.log("Updated saved-voice timestamps to ISO-8601")
                }
            } catch {
                log.log("Could not update the saved-voices index: "
                    + error.localizedDescription)
            }
        }
    }

    private func persistStrict() throws {
        try SavedVoiceFile.encode(voices).write(to: indexURL, options: .atomic)
    }
}
