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

private final class CancellationProbe: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false

    func cancel() { lock.withLock { cancelled = true } }
    func read() -> Bool { lock.withLock { cancelled } }
}

@main
enum BackupFileTreeChecks {
    static func main() throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("bunyi-backup-tree-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("source")
        let nested = source.appendingPathComponent("org/repo")
        try FileManager.default.createDirectory(
            at: nested, withIntermediateDirectories: true)
        let payload = Data("model data".utf8)
        try payload.write(to: nested.appendingPathComponent("config.json"))

        let copied = root.appendingPathComponent("copied")
        try BackupFileTree.copyDirectory(
            from: source, to: copied, isCancelled: { false })
        let copiedPayload = try Data(contentsOf: copied
            .appendingPathComponent("org/repo/config.json"))
        precondition(copiedPayload == payload)

        let large = Data(repeating: 0x5a, count: 17 * 1024 * 1024)
        try large.write(to: nested.appendingPathComponent("weights.bin"))
        let partial = root.appendingPathComponent("partial")
        let probe = CancellationProbe()
        var cancelled = false
        do {
            try BackupFileTree.copyDirectory(
                from: source,
                to: partial,
                isCancelled: { probe.read() },
                didCopyBytes: { _ in probe.cancel() })
        } catch is CancellationError {
            cancelled = true
        }
        precondition(cancelled)
        let partialWeights = partial
            .appendingPathComponent("org/repo/weights.bin")
        precondition(!FileManager.default.fileExists(atPath: partialWeights.path))
        print("Backup file-tree checks passed: nested copy and mid-file cancellation.")
    }
}
