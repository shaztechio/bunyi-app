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

public struct WhisperModelStatus: Sendable {
    public let id: String
    public let source: String
    public let folder: URL
    public let modelFile: URL
    public let expectedBytes: Int64
    public let downloadedBytes: Int64
    public let complete: Bool
    public let partialFiles: [String]
}

public struct WhisperDiskSpaceError: LocalizedError, Sendable {
    public let requiredBytes: Int64
    public let availableBytes: Int64?

    public var errorDescription: String? {
        let required = requiredBytes.formatted(.byteCount(style: .file))
        let available = availableBytes?.formatted(.byteCount(style: .file)) ?? "unknown"
        return "The transcription model needs about \(required), but \(available) is available."
    }
}

/// The one local transcription asset used by the macOS CLI.
///
/// `base`, not `base.en`: Bunyi offers ten languages. The immutable size and
/// SHA-256 come from the publisher's Hugging Face LFS manifest, so a resumed
/// transfer is verified before becoming the reusable model.
@MainActor
public enum WhisperModelStore {
    public nonisolated static let id = "whisper"
    public nonisolated static let source = "ggerganov/whisper.cpp"
    public nonisolated static let fileName = "ggml-base.bin"
    public nonisolated static let expectedBytes: Int64 = 147_951_465
    public nonisolated static let sha256 =
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"
    public nonisolated static let revision =
        "5359861c739e955e79d9a303bcbc70fb988958b1"

    public static var folder: URL {
        ModelsLocation.current()
            .appendingPathComponent("models/ggerganov/whisper.cpp", isDirectory: true)
    }

    public static var modelFile: URL {
        folder.appendingPathComponent(fileName)
    }

    public static var partialFile: URL {
        modelFile.appendingPathExtension(HTTPFileDownloader.partialExtension)
    }

    public static func status() -> WhisperModelStatus {
        let finalBytes = fileSize(modelFile)
        let partialBytes = fileSize(partialFile)
        return WhisperModelStatus(
            id: id,
            source: source,
            folder: folder,
            modelFile: modelFile,
            expectedBytes: expectedBytes,
            downloadedBytes: min(expectedBytes, max(finalBytes, partialBytes)),
            complete: finalBytes == expectedBytes,
            partialFiles: partialBytes > 0 ? [fileName + ".incomplete"] : [])
    }

    public static func isComplete() -> Bool {
        fileSize(modelFile) == expectedBytes
    }

    nonisolated private static func fileSize(_ url: URL) -> Int64 {
        Int64((try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
    }
}

/// One resumable, checksummed Whisper model transfer. The CLI samples its
/// mailbox at 4 Hz, exactly like the TTS downloader, without exposing delegate
/// callbacks across the framework boundary.
@MainActor
public final class WhisperModelDownload {
    private let mailbox = DownloadReceiptMailbox()
    private var activeTransfer: ModelFileTransfer?

    public init() {}

    public func snapshot() -> ModelDownloadProgress {
        mailbox.snapshot()
    }

    public func requestReconnect() {
        activeTransfer?.requestReconnect()
    }

    public func run(ownsLease: Bool = false) async throws -> URL {
        let root = ModelsLocation.current()
        let lease = ownsLease ? nil : try ModelOperationLease(
            modelsRoot: root, operation: "whisper.download")
        defer { withExtendedLifetime(lease) {} }

        let status = WhisperModelStore.status()
        // Only the explicit partial participates in Range resume. A final
        // file that is short or corrupt stays untouched until replacement.
        let reusableBytes = Int64((try? WhisperModelStore.partialFile
            .resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        if status.complete {
            let valid = try await Task.detached(priority: .utility) {
                try HTTPFileDownloader.sha256Hex(of: status.modelFile)
                    == WhisperModelStore.sha256
            }.value
            if valid { return status.modelFile }
        }

        let remaining = max(
            0, WhisperModelStore.expectedBytes - reusableBytes)
        if remaining > 0 {
            try FileManager.default.createDirectory(
                at: root, withIntermediateDirectories: true)
            let available = try root.resourceValues(
                forKeys: [.volumeAvailableCapacityForImportantUsageKey])
                .volumeAvailableCapacityForImportantUsage
            guard let available, available >= remaining else {
                throw WhisperDiskSpaceError(
                    requiredBytes: remaining, availableBytes: available)
            }
        }

        let source = URL(string:
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/"
            + WhisperModelStore.revision + "/ggml-base.bin")!
        var responseRetries = 0
        var connectionRetries = 0
        while true {
            try Task.checkCancellation()
            mailbox.begin(
                file: WhisperModelStore.fileName,
                completed: 0,
                total: WhisperModelStore.expectedBytes,
                fileTotal: WhisperModelStore.expectedBytes)
            let transfer = ModelFileTransfer(
                destination: WhisperModelStore.modelFile,
                digest: WhisperModelStore.sha256,
                expected: WhisperModelStore.expectedBytes,
                mailbox: mailbox)
            activeTransfer = transfer
            let response: HTTPResponseInfo
            do {
                let configuration = URLSessionConfiguration.ephemeral
                // The publisher redirects this 141 MB LFS object to a cold
                // object-store URL that can take longer than URLSession's
                // default minute to send the first byte. Once bytes arrive,
                // the delegate writes them to the resumable partial file.
                configuration.timeoutIntervalForRequest = 5 * 60
                configuration.timeoutIntervalForResource = 60 * 60
                response = try await transfer.run(
                    from: source, configuration: configuration)
            } catch {
                activeTransfer = nil
                try Task.checkCancellation()
                if transfer.reconnectRequested {
                    mailbox.reconnecting()
                    continue
                }
                guard Self.isTransient(error),
                      connectionRetries < DownloadRetryPolicy.maximumRetries else {
                    throw error
                }
                connectionRetries += 1
                mailbox.reconnecting()
                try await Task.sleep(
                    for: .seconds(Int64(1 << connectionRetries)))
                continue
            }
            activeTransfer = nil
            try Task.checkCancellation()
            if response.shouldRetryDownload {
                let retryNumber = min(
                    responseRetries + 1, DownloadRetryPolicy.maximumRetries)
                let delay = DownloadRetryPolicy.delay(
                    for: response, now: Date(), retryNumber: retryNumber,
                    jitter: Double.random(in: 0...1))
                let retryAt = Date().addingTimeInterval(delay)
                guard responseRetries < DownloadRetryPolicy.maximumRetries,
                      delay <= DownloadRetryPolicy.maximumDelay else {
                    throw response.retryExhaustedError(
                        source: source, retryAt: retryAt)
                }
                responseRetries += 1
                mailbox.waiting(
                    until: retryAt, host: source.host ?? "The server",
                    attempt: responseRetries)
                try await DownloadRetryPolicy.wait(until: retryAt)
                continue
            }
            guard response.isSuccess else {
                if (500...599).contains(response.statusCode) {
                    throw response.unavailableError(source: source)
                }
                throw DownloadServiceError.httpStatus(
                    source: source, status: response.statusCode)
            }
            guard WhisperModelStore.isComplete() else {
                throw URLError(.cannotDecodeContentData)
            }
            return WhisperModelStore.modelFile
        }
    }

    private static func isTransient(_ error: Error) -> Bool {
        guard let error = error as? URLError else { return false }
        return [
            .timedOut,
            .cannotFindHost,
            .cannotConnectToHost,
            .dnsLookupFailed,
            .networkConnectionLost,
            .notConnectedToInternet,
            .resourceUnavailable,
            .internationalRoamingOff,
            .callIsActive,
            .dataNotAllowed,
        ].contains(error.code)
    }
}
