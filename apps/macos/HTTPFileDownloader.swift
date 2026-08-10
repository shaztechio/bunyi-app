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

/// Downloads one file, reporting bytes as they arrive.
///
/// `URLSession.download(from:)` is shorter but reports nothing until it
/// finishes, and it writes to the system temp directory rather than the models
/// folder. On the self-hosted path that produced a download that looked dead:
/// the progress bar advances only when a whole file completes, and one file is
/// nearly the entire model, so for minutes nothing moved — while the disk
/// monitor, watching a models folder that was not growing, announced that the
/// connection had stalled. Both were wrong; the transfer was at full speed.
///
/// Streaming the body with `URLSession.bytes` would put the bytes where they
/// belong, but `AsyncBytes` yields one byte at a time: measured against the
/// real server it could not manage 100 MB in two minutes, where `curl` did
/// 200 MB in 23 s. So this uses a download task with a delegate, which keeps
/// URLSession's own transfer performance and adds the progress callbacks that
/// were missing.
/// Why a download that arrived intact is still not usable.
enum DownloadError: LocalizedError {
    case checksumMismatch(file: String, expected: String, actual: String)

    var errorDescription: String? {
        switch self {
        case .checksumMismatch(let file, let expected, let actual):
            // The digests are truncated: twelve characters is plenty to tell
            // two files apart by eye, and sixty-four in an alert is a wall.
            // The log has already recorded them in full.
            "\(file) downloaded but did not match the checksum your server "
            + "published (expected \(expected.prefix(12))…, got "
            + "\(actual.prefix(12))…). The file may be corrupt or still "
            + "uploading. Try again, and re-check the manifest if it persists."
        }
    }
}

final class HTTPFileDownloader: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    /// Written to while in flight, renamed on success — the same extension
    /// `hasCompleteModel` treats as an unfinished download, so an interrupted
    /// transfer can never be mistaken for a complete model.
    static let partialExtension = "incomplete"

    private let destination: URL
    /// Lowercase hex SHA-256 the finished file must match, when the manifest
    /// supplied one. nil means "no expectation" — a manifest.txt server, or a
    /// file the manifest does not list a digest for.
    private let expectedSHA256: String?
    private let onProgress: @Sendable (Int64, Int64) -> Void

    /// Guards the continuation. The delegate can report completion twice —
    /// `didFinishDownloadingTo` then `didCompleteWithError(nil)` — and resuming
    /// a continuation twice is a crash, not a warning.
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Int, Error>?
    private var session: URLSession?

    private init(destination: URL, expectedSHA256: String?,
                 onProgress: @escaping @Sendable (Int64, Int64) -> Void) {
        self.destination = destination
        self.expectedSHA256 = expectedSHA256?.lowercased()
        self.onProgress = onProgress
    }

    /// Downloads `url` to `destination`, returning the HTTP status code.
    ///
    /// A non-200 is returned rather than thrown: some files in a model are
    /// optional, and the caller is the one that knows which.
    @discardableResult
    static func download(
        from url: URL,
        to destination: URL,
        expectedSHA256: String? = nil,
        onProgress: @escaping @Sendable (Int64, Int64) -> Void
    ) async throws -> Int {
        let downloader = HTTPFileDownloader(destination: destination,
                                            expectedSHA256: expectedSHA256,
                                            onProgress: onProgress)
        return try await downloader.run(url: url)
    }

    /// SHA-256 of a file, read in chunks.
    ///
    /// Chunked because the weights are gigabytes: `Data(contentsOf:)` would
    /// map the whole 3.86 GB file to hash it. 1 MB is large enough that the
    /// read syscalls disappear against the hashing itself.
    static func sha256Hex(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func run(url: URL) async throws -> Int {
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        let session = URLSession(configuration: .default, delegate: self, delegateQueue: queue)
        self.session = session
        defer { session.finishTasksAndInvalidate() }

        let task = session.downloadTask(with: url)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.lock()
                self.continuation = continuation
                lock.unlock()
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
    }

    private func finish(_ result: Result<Int, Error>) {
        lock.lock()
        let continuation = self.continuation
        self.continuation = nil
        lock.unlock()
        continuation?.resume(with: result)
    }

    // MARK: URLSessionDownloadDelegate

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        onProgress(totalBytesWritten, totalBytesExpectedToWrite)
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        let code = (downloadTask.response as? HTTPURLResponse)?.statusCode ?? 0
        guard code == 200 else {
            finish(.success(code))
            return
        }
        // The file at `location` is deleted as soon as this method returns, so
        // it has to be moved here and now, not in the async caller.
        do {
            let fm = FileManager.default
            try fm.createDirectory(at: destination.deletingLastPathComponent(),
                                   withIntermediateDirectories: true)
            let partial = destination.appendingPathExtension(Self.partialExtension)
            if fm.fileExists(atPath: partial.path) { try fm.removeItem(at: partial) }
            try fm.moveItem(at: location, to: partial)
            // The rename below is what declares this file trustworthy —
            // `hasCompleteModel` treats anything still carrying .incomplete as
            // an interrupted download. So the digest is checked here, in
            // between: a file that fails is removed and never becomes a
            // destination path, which means a retry re-fetches it rather than
            // finding it already present and skipping it.
            if let expected = expectedSHA256 {
                let actual = try Self.sha256Hex(of: partial)
                guard actual == expected else {
                    try? fm.removeItem(at: partial)
                    finish(.failure(DownloadError.checksumMismatch(
                        file: destination.lastPathComponent,
                        expected: expected,
                        actual: actual)))
                    return
                }
            }
            if fm.fileExists(atPath: destination.path) {
                try fm.removeItem(at: destination)
            }
            try fm.moveItem(at: partial, to: destination)
            finish(.success(code))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        // Success already resumed the continuation in didFinishDownloadingTo;
        // finish() ignores the second call.
        if let error {
            finish(.failure(error))
        }
    }
}
