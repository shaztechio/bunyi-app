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
import CryptoKit

func require(_ condition: Bool, _ message: String) throws {
    if !condition { throw NSError(domain: "DownloadVerification", code: 1,
                                  userInfo: [NSLocalizedDescriptionKey: message]) }
}
let args = CommandLine.arguments
guard args.count == 4 else { fatalError("Usage: <base URL> <object key> <SHA-256>") }
let original = URL(string: args[1].trimmingCharacters(in: CharacterSet(charactersIn: "/")) + "/" + args[2])!
var headRequest = URLRequest(url: original)
headRequest.httpMethod = "HEAD"
let (_, headResponse) = try await URLSession.shared.data(for: headRequest)
let head = headResponse as! HTTPURLResponse
try require(head.statusCode == 200, "HEAD redirect failed")
let size = Int64(head.value(forHTTPHeaderField: "Content-Length") ?? "")!
print("URLSession HEAD OK: \(size) bytes")
let start = Date()
let (file, fullResponse) = try await URLSession.shared.download(from: original)
defer { try? FileManager.default.removeItem(at: file) }
try require((fullResponse as! HTTPURLResponse).statusCode == 200, "Full download failed")
let input = try FileHandle(forReadingFrom: file)
defer { try? input.close() }
var hash = SHA256()
var received: Int64 = 0
while let bytes = try input.read(upToCount: 131072), !bytes.isEmpty {
    hash.update(data: bytes)
    received += Int64(bytes.count)
}
let digest = hash.finalize().map { String(format: "%02x", $0) }.joined()
try require(received == size && digest == args[3], "Full checksum or size mismatch")
print(String(format: "URLSession full file OK: %.2fs, %.2f MB/s, SHA-256 verified",
             Date().timeIntervalSince(start), Double(size) / Date().timeIntervalSince(start) / 1_000_000))

// Verify Range preservation through a fresh redirect and compare the received
// bytes against the same section of the complete, verified local download.
let offset = size / 2
let end = min(size - 1, offset + 1023)
var rangeRequest = URLRequest(url: original)
rangeRequest.setValue("bytes=\(offset)-\(end)", forHTTPHeaderField: "Range")
let (rangeData, rangeResponse) = try await URLSession.shared.data(for: rangeRequest)
let ranged = rangeResponse as! HTTPURLResponse
try require(ranged.statusCode == 206, "Range was lost during redirect")
try require(ranged.value(forHTTPHeaderField: "Content-Range") == "bytes \(offset)-\(end)/\(size)",
            "Unexpected Content-Range")
try input.seek(toOffset: UInt64(offset))
let expected = try input.read(upToCount: Int(end - offset + 1))
try require(rangeData == expected, "Range bytes do not match the verified file")
print("URLSession Range redirect OK: exact bytes verified")

