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

/// What produced a generated WAV, carried inside the file itself.
///
/// The filename only records the mode and a timestamp, so everything that
/// actually determined the audio — the text, the voice, the model — was lost
/// the moment the file left the app. Embedding it means a WAV found later, or
/// sent to someone else, still says how to make it again.
struct OutputMetadata: Codable, Hashable {
    var mode: String
    var text: String
    var language: String

    // The three modes choose a voice in three different ways, and the UI reuses
    // one text field for two of them — "Style" in preset voice, "Voice" in
    // voice design. Storing both under one key would leave a reader unable to
    // tell a delivery instruction from a voice description, so each gets its
    // own, and only the one belonging to the mode is filled.
    /// Preset voice: the speaker chosen from the model's list.
    var speaker: String?
    /// Preset voice: the optional delivery instruction.
    var style: String?
    /// Voice design: the description the voice was built from.
    var voiceDescription: String?
    /// Voice clone: the transcript of the reference clip.
    var referenceTranscript: String?

    var modelRepo: String
    var appVersion: String
    var created: Date

    /// Shown as the title in players that read RIFF INFO.
    var title: String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return mode }
        let firstLine = trimmed.split(separator: "\n").first.map(String.init) ?? trimmed
        return firstLine.count <= 60 ? firstLine : String(firstLine.prefix(59)) + "…"
    }

    /// The voice, however this mode chose one — for display, and for `IART`.
    /// For a clone the reference transcript is the only thing that identifies
    /// which voice it was, so it stands in rather than a generic label.
    var voiceSummary: String? {
        if let speaker, !speaker.isEmpty { return speaker }
        if let voiceDescription, !voiceDescription.isEmpty { return voiceDescription }
        if let referenceTranscript, !referenceTranscript.isEmpty {
            return "Clone of “\(referenceTranscript)”"
        }
        return nil
    }
}

/// Reads and writes a RIFF `LIST`/`INFO` chunk in a WAV file.
///
/// WAV is RIFF, so tagging is a standard chunk rather than a sidecar file:
/// `afinfo`, ffprobe, and most editors already show it. The individual INFO
/// fields are what those tools display; the whole record is additionally
/// stored as JSON in `ICMT`, because there is no standard field for "the
/// prompt", and inventing four-character codes nothing else understands would
/// buy nothing over one comment field.
enum WAVMetadata {
    private static let listID = "LIST"
    private static let infoID = "INFO"

    /// Fractional seconds, so the timestamp survives to the millisecond.
    /// `.iso8601` truncates to whole seconds. Not exact — `Date` is finer than
    /// milliseconds — so a decoded record is not `==` to the one written; it is
    /// a readable timestamp in a field humans read, not a key to compare on.
    /// Built per use rather than cached: `ISO8601DateFormatter` is not
    /// `Sendable`, and a shared static one is a concurrency error under Swift 6.
    /// This runs once per generated file, so the allocation is not worth an
    /// `nonisolated(unsafe)` escape hatch.
    private static var precise: ISO8601DateFormatter {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }

    private static var whole: ISO8601DateFormatter { ISO8601DateFormatter() }

    private static let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(precise.string(from: date))
        }
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }()

    private static let decoder: JSONDecoder = {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let text = try decoder.singleValueContainer().decode(String.self)
            // Whole seconds too, so a file written before fractional seconds
            // were used still reads.
            guard let date = precise.date(from: text) ?? whole.date(from: text) else {
                throw DecodingError.dataCorrupted(.init(
                    codingPath: decoder.codingPath,
                    debugDescription: "not an ISO 8601 date: \(text)"
                ))
            }
            return date
        }
        return decoder
    }()

    /// Appends the INFO chunk to an existing WAV, in place.
    ///
    /// Appending rather than rewriting: the audio data is untouched, so this
    /// cannot corrupt the samples even if the metadata is wrong. A reader that
    /// ignores unknown chunks — which the format requires — sees the same file
    /// it saw before.
    static func embed(_ metadata: OutputMetadata, in url: URL) throws {
        var data = try Data(contentsOf: url)
        guard data.count >= 12,
              data.prefix(4) == Data("RIFF".utf8),
              data[8..<12] == Data("WAVE".utf8) else {
            throw MetadataError.notAWAVFile
        }

        // RIFF chunks are word-aligned; a file ending on an odd byte needs a
        // pad before anything can follow it.
        if data.count % 2 == 1 { data.append(0) }

        data.append(try infoChunk(for: metadata))

        // The RIFF size field counts everything after itself, so it has to grow
        // by exactly what was appended or the file is malformed.
        let riffSize = UInt32(data.count - 8).littleEndian
        data.replaceSubrange(4..<8, with: withUnsafeBytes(of: riffSize) { Data($0) })

        try data.write(to: url, options: .atomic)
    }

    /// Returns the embedded record, or nil for a WAV written by anything else.
    static func read(from url: URL) -> OutputMetadata? {
        guard let data = try? Data(contentsOf: url),
              let comment = string(forField: "ICMT", in: data),
              let json = comment.data(using: .utf8) else { return nil }
        return try? decoder.decode(OutputMetadata.self, from: json)
    }

    // MARK: Chunk construction

    private static func infoChunk(for metadata: OutputMetadata) throws -> Data {
        let json = String(decoding: try encoder.encode(metadata), as: UTF8.self)

        var fields: [(String, String)] = [
            ("INAM", metadata.title),
            ("IART", metadata.voiceSummary ?? metadata.mode),
            ("ISFT", "Bunyi \(metadata.appVersion)"),
            ("ICRD", ISO8601DateFormatter().string(from: metadata.created)),
            ("IGNR", "Speech"),
            ("ICMT", json),
        ]
        fields.removeAll { $0.1.isEmpty }

        var body = Data(infoID.utf8)
        for (id, value) in fields {
            body.append(subchunk(id: id, text: value))
        }

        var chunk = Data(listID.utf8)
        chunk.append(withUnsafeBytes(of: UInt32(body.count).littleEndian) { Data($0) })
        chunk.append(body)
        return chunk
    }

    private static func subchunk(id: String, text: String) -> Data {
        // INFO values are null-terminated, and the terminator counts toward the
        // declared size. Then the chunk itself pads to an even length.
        var value = Data(text.utf8)
        value.append(0)

        var chunk = Data(id.utf8)
        chunk.append(withUnsafeBytes(of: UInt32(value.count).littleEndian) { Data($0) })
        chunk.append(value)
        if value.count % 2 == 1 { chunk.append(0) }
        return chunk
    }

    // MARK: Chunk parsing

    /// Walks the top-level chunks looking for `LIST`/`INFO`, then the field.
    private static func string(forField field: String, in data: Data) -> String? {
        guard data.count >= 12, data.prefix(4) == Data("RIFF".utf8) else { return nil }

        var offset = 12
        while offset + 8 <= data.count {
            let id = data[offset..<offset + 4]
            let size = Int(data[offset + 4..<offset + 8].withUnsafeBytes {
                $0.loadUnaligned(as: UInt32.self).littleEndian
            })
            let body = offset + 8
            guard size >= 0, body + size <= data.count else { return nil }

            if id == Data(listID.utf8), size >= 4,
               data[body..<body + 4] == Data(infoID.utf8) {
                if let found = infoValue(field, inINFO: data[(body + 4)..<(body + size)]) {
                    return found
                }
            }
            // Chunks are padded to even lengths, and the pad is not counted in
            // the size — skipping it would misread every following chunk.
            offset = body + size + (size % 2)
        }
        return nil
    }

    private static func infoValue(_ wanted: String, inINFO body: Data) -> String? {
        var offset = body.startIndex
        while offset + 8 <= body.endIndex {
            let id = body[offset..<offset + 4]
            let size = Int(body[offset + 4..<offset + 8].withUnsafeBytes {
                $0.loadUnaligned(as: UInt32.self).littleEndian
            })
            let start = offset + 8
            guard size >= 0, start + size <= body.endIndex else { return nil }

            if id == Data(wanted.utf8) {
                var bytes = body[start..<start + size]
                if bytes.last == 0 { bytes = bytes.dropLast() }
                return String(decoding: bytes, as: UTF8.self)
            }
            offset = start + size + (size % 2)
        }
        return nil
    }

    enum MetadataError: LocalizedError {
        case notAWAVFile

        var errorDescription: String? {
            switch self {
            case .notAWAVFile: "That file is not a WAV."
            }
        }
    }
}
