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

public struct SavedVoice: Identifiable, Codable, Hashable {
    public let id: UUID
    public var name: String
    public var fileName: String
    public var transcript: String
    public var transcriptAudioSeconds: TimeInterval?
    public var createdAt: Date
}

/// The shared saved-voice file uses readable ISO-8601 timestamps. Bunyi's
/// earliest macOS builds used JSONEncoder's default numeric Date encoding, so
/// the decoder accepts that legacy representation long enough to migrate it.
enum SavedVoiceFile {
    private static var preciseDateFormatter: ISO8601DateFormatter {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }

    private static var wholeDateFormatter: ISO8601DateFormatter {
        ISO8601DateFormatter()
    }

    static func decode(_ data: Data) throws -> [SavedVoice] {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            if let text = try? container.decode(String.self),
               let date = preciseDateFormatter.date(from: text)
                    ?? wholeDateFormatter.date(from: text) {
                return date
            }
            if let seconds = try? container.decode(Double.self) {
                return Date(timeIntervalSinceReferenceDate: seconds)
            }
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "createdAt must be an ISO-8601 timestamp")
        }
        return try decoder.decode([SavedVoice].self, from: data)
    }

    static func encode(_ voices: [SavedVoice]) throws -> Data {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(preciseDateFormatter.string(from: date))
        }
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try encoder.encode(voices)
    }

    static func containsLegacyNumericDate(_ data: Data) -> Bool {
        guard let entries = try? JSONSerialization.jsonObject(with: data)
                as? [[String: Any]] else { return false }
        return entries.contains { $0["createdAt"] is NSNumber }
    }
}
