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
enum SavedVoiceFormatChecks {
    static func main() throws {
        let created = Date(timeIntervalSince1970: 1_789_403_834.125)
        let voice = SavedVoice(
            id: UUID(), name: "Amadeus", fileName: "voice.wav",
            transcript: "Your table is ready.", transcriptAudioSeconds: 10,
            createdAt: created)

        let encoded = try SavedVoiceFile.encode([voice])
        let object = try JSONSerialization.jsonObject(with: encoded)
            as? [[String: Any]]
        let timestamp = object?.first?["createdAt"] as? String
        precondition(timestamp?.contains("T") == true)
        precondition(timestamp?.hasSuffix("Z") == true)
        let roundTrip = try SavedVoiceFile.decode(encoded)
        precondition(roundTrip == [voice])

        // JSONEncoder's legacy default Date representation is seconds since
        // Apple's 2001 reference date, not Unix time.
        let legacySeconds = 812_345_678.25
        let legacy = try JSONSerialization.data(withJSONObject: [[
            "id": UUID().uuidString,
            "name": "Legacy",
            "fileName": "legacy.wav",
            "transcript": "Legacy recording.",
            "createdAt": legacySeconds,
        ]])
        precondition(SavedVoiceFile.containsLegacyNumericDate(legacy))
        let migrated = try SavedVoiceFile.decode(legacy)
        precondition(migrated.count == 1)
        precondition(abs(migrated[0].createdAt.timeIntervalSinceReferenceDate
                         - legacySeconds) < 0.001)
        precondition(migrated[0].transcriptAudioSeconds == nil)

        let migratedData = try SavedVoiceFile.encode(migrated)
        precondition(!SavedVoiceFile.containsLegacyNumericDate(migratedData))
        let migratedObject = try JSONSerialization.jsonObject(with: migratedData)
            as? [[String: Any]]
        precondition(migratedObject?.first?["createdAt"] is String)

        print("Saved voice checks passed: ISO-8601 output and numeric-date migration.")
    }
}
