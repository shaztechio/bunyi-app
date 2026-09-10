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

import CryptoKit
import Foundation

extension DownloadProgressChecks {
    static func checkTransfers() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let base = CommandLine.arguments[1]
        let payload = Data(repeating: 7, count: 4096)
        let configuration = URLSessionConfiguration.ephemeral
        let digest = SHA256.hash(data: payload).map { String(format: "%02x", $0) }.joined()
        let dest = folder.appendingPathComponent("weights")
        let partial = dest.appendingPathExtension("incomplete")

        let receipt = DownloadReceiptMailbox()
        receipt.begin(file: "weights", completed: 0, total: 4096, fileTotal: 4096)
        let transfer = Task {
            try await ModelFileTransfer(destination: dest, digest: digest, expected: 4096, mailbox: receipt)
                .run(from: URL(string: "\(base)/one-byte")!, configuration: configuration)
        }
        var sawByte = false
        for _ in 0..<500 {
            if receipt.snapshot().received == 1 { sawByte = true; break }
            try await Task.sleep(for: .milliseconds(10))
        }
        transfer.cancel()
        do { _ = try await transfer.value; preconditionFailure("Cancelled download succeeded") }
        catch let error as URLError { precondition(error.code == .cancelled) }
        _ = try await URLSession.shared.data(from: URL(string: "\(base)/release")!)
        precondition(sawByte, "The first byte was not observable before the rest of the file")
        precondition(!FileManager.default.fileExists(atPath: dest.path))

        for ignored in [false, true] {
            try payload.prefix(123).write(to: partial)
            let mailbox = DownloadReceiptMailbox()
            mailbox.begin(file: "weights", completed: 0, total: 4096, fileTotal: 4096)
            let route = ignored ? "ignore-range" : "resume"
            let code = try await ModelFileTransfer(destination: dest, digest: digest, expected: 4096, mailbox: mailbox)
                .run(from: URL(string: "\(base)/\(route)")!, configuration: configuration)
            precondition(code == 200)
            let contents = try Data(contentsOf: dest)
            precondition(contents == payload)
            precondition(mailbox.snapshot().received == (ignored ? 4096 : 4096 - 123))
            precondition(mailbox.snapshot().fileBytes == 4096)
            precondition(!FileManager.default.fileExists(atPath: partial.path))
        }
        print("Model transfer checks passed: first-byte delivery, cancellation, accepted and ignored resume, checksum and file contents.")
    }
}
