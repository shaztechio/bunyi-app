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

/// Exercises our URLSession delegate and file IO; the transport fixture replaces the network.
final class ModelTransferFixture: URLProtocol, @unchecked Sendable {
    static let payload = Data(repeating: 7, count: 4096)
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let offset = Int(request.value(forHTTPHeaderField: "Range")?
            .replacingOccurrences(of: "bytes=", with: "").replacingOccurrences(of: "-", with: "") ?? "0") ?? 0
        let resumed = offset > 0 && url.path != "/ignore-range"
        let start = resumed ? offset : 0
        var headers = ["Content-Length": String(Self.payload.count - start)]
        if resumed { headers["Content-Range"] = "bytes \(start)-4095/4096" }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: resumed ? 206 : 200,
            httpVersion: "HTTP/1.1", headerFields: headers)!, cacheStoragePolicy: .notAllowed)
        if url.path == "/one-byte" {
            client?.urlProtocol(self, didLoad: Data([7]))
            // Deliberately remain open. The test must see this byte before cancellation.
        } else {
            client?.urlProtocol(self, didLoad: Self.payload.subdata(in: start..<Self.payload.count))
            client?.urlProtocolDidFinishLoading(self)
        }
    }
    override func stopLoading() {}
}

extension DownloadProgressChecks {
    static func checkTransfers() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ModelTransferFixture.self]
        let digest = SHA256.hash(data: ModelTransferFixture.payload).map { String(format: "%02x", $0) }.joined()
        let dest = folder.appendingPathComponent("weights")
        let partial = dest.appendingPathExtension("incomplete")

        let receipt = DownloadReceiptMailbox()
        receipt.begin(file: "weights", completed: 0, total: 4096, fileTotal: 4096)
        let transfer = Task {
            try await ModelFileTransfer(destination: dest, digest: digest, expected: 4096, mailbox: receipt)
                .run(from: URL(string: "https://fixture.test/one-byte")!, configuration: configuration)
        }
        var sawByte = false
        for _ in 0..<500 {
            if receipt.snapshot().received == 1 { sawByte = true; break }
            try await Task.sleep(for: .milliseconds(10))
        }
        transfer.cancel()
        do { _ = try await transfer.value; preconditionFailure("Cancelled download succeeded") }
        catch let error as URLError { precondition(error.code == .cancelled) }
        precondition(sawByte, "The first byte was not observable before the rest of the file")
        precondition(!FileManager.default.fileExists(atPath: dest.path))

        for ignored in [false, true] {
            try ModelTransferFixture.payload.prefix(123).write(to: partial)
            let mailbox = DownloadReceiptMailbox()
            mailbox.begin(file: "weights", completed: 0, total: 4096, fileTotal: 4096)
            let route = ignored ? "ignore-range" : "resume"
            let code = try await ModelFileTransfer(destination: dest, digest: digest, expected: 4096, mailbox: mailbox)
                .run(from: URL(string: "https://fixture.test/\(route)")!, configuration: configuration)
            precondition(code == 200)
            let contents = try Data(contentsOf: dest)
            precondition(contents == ModelTransferFixture.payload)
            precondition(mailbox.snapshot().received == (ignored ? 4096 : 4096 - 123))
            precondition(mailbox.snapshot().fileBytes == 4096)
            precondition(!FileManager.default.fileExists(atPath: partial.path))
        }
        print("Model transfer checks passed: first-byte delivery, cancellation, accepted and ignored resume, checksum and file contents.")
    }
}
