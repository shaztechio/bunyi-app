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
enum PackagedModelDefaultsChecks {
    static func main() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(
            "bunyi-packaged-defaults-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(
            at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }

        let file = folder.appendingPathComponent(PackagedModelDefaults.fileName)
        for (source, expected) in [
            ("mirror", PackagedModelDefaults.ModelDownloadSource.mirror),
            ("huggingFace", .huggingFace),
        ] {
            let json = """
                {"schemaVersion":1,"modelDownloadSource":"\(source)","future":true}
                """
            try Data(json.utf8).write(to: file)
            let loaded = PackagedModelDefaults.load(from: file)
            precondition(loaded.modelDownloadSource == expected)
            let unchanged = try String(contentsOf: file, encoding: .utf8)
            precondition(unchanged == json)
        }

        let invalid: [String?] = [
            nil,
            "",
            "{",
            "null",
            "[]",
            "{\"schemaVersion\":2,\"modelDownloadSource\":\"mirror\"}",
            "{\"schemaVersion\":1.0,\"modelDownloadSource\":\"mirror\"}",
            "{\"schemaVersion\":\"1\",\"modelDownloadSource\":\"mirror\"}",
            "{\"schemaVersion\":1,\"modelDownloadSource\":\"unknown\"}",
            "{\"schemaVersion\":1,\"modelDownloadSource\":true}",
            "{\"schemaVersion\":1}",
        ]
        for json in invalid {
            try? FileManager.default.removeItem(at: file)
            if let json { try Data(json.utf8).write(to: file) }
            let loaded = PackagedModelDefaults.load(from: file)
            precondition(loaded.modelDownloadSource == .huggingFace)
            precondition(loaded.diagnostic.contains("using Hugging Face"))
        }

        let executable = folder.appendingPathComponent("package/bunyi")
        let resources = folder.appendingPathComponent("Bunyi.app/Contents/Resources")
        let cliURL = PackagedModelDefaults.distributionURL(
            bundleIdentifier: "app.bunyi.cli",
            executableURL: executable,
            resourceURL: resources)
        precondition(cliURL == executable.deletingLastPathComponent()
            .appendingPathComponent(PackagedModelDefaults.fileName))
        let appURL = PackagedModelDefaults.distributionURL(
            bundleIdentifier: "app.bunyi.Bunyi",
            executableURL: executable,
            resourceURL: resources)
        precondition(appURL == resources
            .appendingPathComponent(PackagedModelDefaults.fileName))
    }
}
