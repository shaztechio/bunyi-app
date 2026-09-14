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
enum OutputMetadataChecks {
    static func main() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("bunyi-metadata-\(UUID().uuidString)")
        try FileManager.default.createDirectory(
            at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let currentURL = directory.appendingPathComponent("current.wav")
        try emptyWAV().write(to: currentURL)
        let current = OutputMetadata(
            mode: "Preset voice", text: "Hello", language: "auto",
            speaker: "ryan", modelRepo: "example/model",
            appVersion: "1.2.3", created: Date())
        precondition(current.platform == "macOS")
        try WAVMetadata.embed(current, in: currentURL)
        precondition(WAVMetadata.read(from: currentURL)?.platform == "macOS")

        // Old metadata has no platform field and must remain readable.
        let legacyURL = directory.appendingPathComponent("legacy.wav")
        try emptyWAV().write(to: legacyURL)
        let legacy = OutputMetadata(
            mode: "Voice clone", text: "Hello", language: "auto",
            referenceTranscript: "Reference", modelRepo: "example/model",
            appVersion: "1.2.2", platform: nil, created: Date())
        try WAVMetadata.embed(legacy, in: legacyURL)
        precondition(WAVMetadata.read(from: legacyURL)?.platform == nil)

        print("Output metadata checks passed: macOS producer and legacy decoding.")
    }

    private static func emptyWAV() -> Data {
        var data = Data("RIFF".utf8)
        data.append(withUnsafeBytes(of: UInt32(4).littleEndian) { Data($0) })
        data.append(Data("WAVE".utf8))
        return data
    }
}
