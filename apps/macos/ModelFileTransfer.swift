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

/// Streams model bytes to the resumable file, reporting real data receipts.
/// All delegate state is confined to a serial queue; only completion uses a lock.
final class ModelFileTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let destination: URL
    private let partial: URL
    private let digest: String?
    private let expected: Int64?
    private let mailbox: DownloadReceiptMailbox
    private var handle: FileHandle?
    private var hasher = SHA256()
    private var offset: Int64 = 0
    private var written: Int64 = 0
    private var total: Int64 = 0
    private var code = 0
    private var failure: Error?
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Int, Error>?
    private var activeTask: URLSessionDataTask?
    private var reconnect = false
    var reconnectRequested: Bool { lock.withLock { reconnect } }

    func requestReconnect() {
        let task = lock.withLock {
            reconnect = true
            return activeTask
        }
        task?.cancel()
    }

    init(destination: URL, digest: String?, expected: Int64?, mailbox: DownloadReceiptMailbox) {
        self.destination = destination
        self.partial = destination.appendingPathExtension(HTTPFileDownloader.partialExtension)
        self.digest = digest
        self.expected = expected
        self.mailbox = mailbox
    }

    func run(from url: URL, configuration: URLSessionConfiguration = .default) async throws -> Int {
        try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
        offset = (try? partial.resourceValues(forKeys: [.fileSizeKey]).fileSize).map(Int64.init) ?? 0
        if let expected, offset >= expected { offset = 0 }
        var request = URLRequest(url: url)
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        if offset > 0 { request.setValue("bytes=\(offset)-", forHTTPHeaderField: "Range") }
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue)
        defer { session.finishTasksAndInvalidate() }
        let task = session.dataTask(with: request)
        lock.withLock { activeTask = task }
        defer { lock.withLock { activeTask = nil } }
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.withLock { self.continuation = continuation }
                task.resume()
                if Task.isCancelled { task.cancel() }
            }
        } onCancel: { task.cancel() }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard code == 200 || code == 206 else { completionHandler(.cancel); return }
        do {
            if code == 206 {
                guard offset > 0, let range = (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Range"),
                      range.hasPrefix("bytes \(offset)-") else { throw URLError(.badServerResponse) }
            } else { offset = 0 }
            total = response.expectedContentLength > 0 ? response.expectedContentLength + offset : expected ?? 0
            if offset == 0 {
                _ = FileManager.default.createFile(atPath: partial.path, contents: nil)
            } else if digest != nil {
                mailbox.verifying()
                let existing = try FileHandle(forReadingFrom: partial)
                defer { try? existing.close() }
                while let chunk = try existing.read(upToCount: 1 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
            }
            handle = try FileHandle(forWritingTo: partial)
            if offset == 0 { try handle?.truncate(atOffset: 0) }
            else { try handle?.seekToEnd() }
            mailbox.prepared(offset: offset, total: total)
            completionHandler(.allow)
        } catch {
            failure = error
            completionHandler(.cancel)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil else { return }
        // Receipt precedes disk IO. A buffered write is never mistaken for a network read.
        mailbox.receive(Int64(data.count), total: total)
        do {
            try handle?.write(contentsOf: data)
            if digest != nil { hasher.update(data: data) }
            written += Int64(data.count)
        } catch { failure = error; dataTask.cancel() }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let continuation = lock.withLock { let saved = self.continuation; self.continuation = nil; return saved }
        guard let continuation else { return }
        do {
            try handle?.close(); handle = nil
            if let failure { throw failure }
            if code != 0 && code != 200 && code != 206 { continuation.resume(returning: code); return }
            if let error { throw error }
            mailbox.verifying()
            if total > 0 && offset + written != total { throw URLError(.networkConnectionLost) }
            if let digest {
                let actual = hasher.finalize().map { String(format: "%02x", $0) }.joined()
                guard actual == digest else {
                    try? FileManager.default.removeItem(at: partial)
                    throw DownloadError.checksumMismatch(file: destination.lastPathComponent, expected: digest, actual: actual)
                }
            }
            if FileManager.default.fileExists(atPath: destination.path) { try FileManager.default.removeItem(at: destination) }
            try FileManager.default.moveItem(at: partial, to: destination)
            continuation.resume(returning: 200)
        } catch { continuation.resume(throwing: error) }
    }
}
