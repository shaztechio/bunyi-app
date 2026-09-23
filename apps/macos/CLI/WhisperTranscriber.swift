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

import AVFoundation
import BunyiMLXCore
import Foundation
import whisper

/// Fully local CLI transcription. A standalone executable inherits TCC
/// responsibility from its launching terminal, and Apple Terminal has no
/// Speech usage string, so `SFSpeechRecognizer` cannot reliably prompt there.
/// whisper.cpp needs no privacy grant and never sends the recording away.
@MainActor
enum CLIWhisperTranscriber {
    static func transcribe(
        _ url: URL, language: String, trimReference: Bool,
        request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState, ownsLease: Bool = false,
        referenceSeconds: TimeInterval? = nil
    ) async throws -> String {
        let samples: [Float]
        do {
            let duration: TimeInterval?
            if trimReference {
                if let referenceSeconds { duration = referenceSeconds }
                else { duration = try await ReferenceClipPolicy.automaticWindow(url: url) }
            } else { duration = nil }
            samples = try decodeMono(from: url, maximumSeconds: duration)
        } catch {
            throw CLIError(
                "transcription_failed",
                "The audio could not be decoded: \(error.localizedDescription)",
                exitCode: 3)
        }
        guard !samples.isEmpty else {
            throw CLIError(
                "transcription_failed", "Nothing could be heard in that recording.",
                exitCode: 3)
        }
        if cancelled.value {
            throw CLIError("cancelled", "Transcription was cancelled.", exitCode: 5)
        }

        let lease: ModelOperationLease?
        do {
            lease = ownsLease ? nil : try ModelOperationLease(
                modelsRoot: ModelsLocation.current(), operation: "transcribe")
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy", "Another Bunyi process is using this models folder.",
                exitCode: 4)
        }
        defer { withExtendedLifetime(lease) {} }

        let model = try await ensureModel(
            request: request, output: output, cancelled: cancelled,
            ownsLease: true)
        output.event(CLIProtocol.event(request, type: "transcribing", [
            "detail": "Transcribing the audio locally with Whisper",
            "inputPath": url.path,
            "local": true,
        ]))

        let abort = WhisperAbortState(cancelled: cancelled)
        let worker = Task.detached(priority: .userInitiated) {
            try infer(
                samples: samples, modelPath: model.path,
                language: languageCode(language), abort: abort)
        }
        let monitor = Task {
            while !Task.isCancelled, !cancelled.value {
                try? await Task.sleep(for: .milliseconds(100))
            }
            if cancelled.value {
                abort.cancel()
                worker.cancel()
            }
        }
        defer { monitor.cancel() }

        do {
            return try await worker.value
        } catch is CancellationError {
            throw CLIError("cancelled", "Transcription was cancelled.", exitCode: 5)
        } catch let error as CLIError {
            throw error
        } catch {
            throw CLIError(
                "transcription_failed", error.localizedDescription, exitCode: 10)
        }
    }

    static func ensureModel(
        request: CLIRequest, output: CLIOutput,
        cancelled: CancellationState, aggregateBase: Int64 = 0,
        aggregateTotal: Int64? = nil, itemIndex: Int = 1,
        itemCount: Int = 1, ownsLease: Bool = false
    ) async throws -> URL {
        let resolvedTotal = aggregateTotal
            ?? aggregateBase + WhisperModelStore.expectedBytes
        let download = WhisperModelDownload()
        output.event(CLIProtocol.event(request, type: "checking", [
            "item": item(index: itemIndex, count: itemCount),
            "bytesCompleted": aggregateBase,
            "bytesTotal": resolvedTotal,
            "detail": "Checking the Whisper transcription model",
        ]))
        let operation = Task { @MainActor in
            try await download.run(ownsLease: ownsLease)
        }
        let monitor = Task { @MainActor in
            var previous = ""
            while !Task.isCancelled {
                if cancelled.value { operation.cancel() }
                let progress = download.snapshot()
                let message = progressMessage(
                    progress, request: request, aggregateBase: aggregateBase,
                    aggregateTotal: resolvedTotal, itemIndex: itemIndex,
                    itemCount: itemCount)
                let type = String(describing: message["type"] ?? "")
                let key = "\(type)-\(progress.file ?? "")-\(progress.available)"
                if key != previous {
                    output.event(message)
                    previous = key
                }
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
        defer { monitor.cancel() }

        do {
            let result = try await operation.value
            monitor.cancel()
            output.event(CLIProtocol.event(request, type: "checking", [
                "item": item(index: itemIndex, count: itemCount),
                "bytesCompleted": aggregateBase
                    + WhisperModelStore.expectedBytes,
                "bytesTotal": resolvedTotal,
                "detail": "Whisper transcription model is ready",
            ]))
            return result
        } catch is CancellationError {
            throw CLIError(
                "cancelled", "Transcription model download was cancelled.",
                exitCode: 5)
        } catch is BunyiBusyError {
            throw CLIError(
                "bunyi_busy", "Another Bunyi process is using this models folder.",
                exitCode: 4)
        } catch let error as WhisperDiskSpaceError {
            throw CLIError(
                "insufficient_disk_space", error.localizedDescription,
                exitCode: 3)
        } catch let error as DownloadServiceError {
            let code: String
            switch error {
            case .unavailable: code = "download_service_unavailable"
            case .rateLimited: code = "download_rate_limited"
            case .httpStatus: code = "download_failed"
            }
            throw CLIError(code, error.localizedDescription, exitCode: 10)
        } catch {
            throw CLIError(
                "download_failed",
                "The Whisper model could not be prepared: \(error.localizedDescription)",
                exitCode: 10)
        }
    }

    private static func item(index: Int, count: Int) -> CLIMessage {
        ["id": WhisperModelStore.id, "index": index, "count": count,
         "kind": "transcription"]
    }

    private static func progressMessage(
        _ progress: ModelDownloadProgress, request: CLIRequest,
        aggregateBase: Int64, aggregateTotal: Int64,
        itemIndex: Int, itemCount: Int
    ) -> CLIMessage {
        let type: String
        switch progress.phase {
        case .checking: type = "checking"
        case .sizing: type = "sizing"
        case .downloading, .reconnecting: type = "downloading"
        case .verifying: type = "verifying"
        case .waiting: type = "waiting"
        }
        var fields: CLIMessage = [
            "item": item(index: itemIndex, count: itemCount),
            "bytesCompleted": aggregateBase + progress.available,
            "bytesTotal": aggregateTotal,
            "rateBytesPerSecond": progress.rate > 0 ? progress.rate : NSNull(),
            "etaSeconds": progress.rate > 0
                ? Double(max(0, WhisperModelStore.expectedBytes - progress.available))
                    / progress.rate
                : NSNull(),
            "currentFile": progress.file ?? NSNull(),
            "detail": detail(progress),
        ]
        if progress.phase == .waiting {
            fields["retryAt"] = progress.retryAt.map(CLIProtocol.timestamp) ?? NSNull()
            fields["retryAfterSeconds"] = progress.retryAt.map {
                max(0, Int(ceil($0.timeIntervalSinceNow)))
            } ?? NSNull()
            fields["host"] = progress.retryHost ?? NSNull()
            fields["retryAttempt"] = progress.retryAttempt
        }
        return CLIProtocol.event(request, type: type, fields)
    }

    private static func detail(_ progress: ModelDownloadProgress) -> String {
        switch progress.phase {
        case .checking: "Checking the Whisper transcription model"
        case .sizing: "Calculating transcription model download size"
        case .downloading: "Downloading transcription model"
        case .verifying: "Checking transcription model file"
        case .reconnecting: "Reconnecting — keeping downloaded transcription bytes"
        case .waiting: "Waiting to retry transcription model download"
        }
    }

    nonisolated private static func decodeMono(
        from url: URL, maximumSeconds: TimeInterval?
    ) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let inputFormat = file.processingFormat
        let limitedFrames = maximumSeconds.map {
            min(file.length, AVAudioFramePosition($0 * inputFormat.sampleRate))
        } ?? file.length
        guard limitedFrames > 0 else { return [] }
        let capacity = AVAudioFrameCount(min(
            limitedFrames, AVAudioFramePosition(UInt32.max)))
        guard let input = AVAudioPCMBuffer(
            pcmFormat: inputFormat, frameCapacity: capacity) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        try file.read(into: input, frameCount: capacity)

        guard let outputFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: Double(WHISPER_SAMPLE_RATE), channels: 1,
            interleaved: false),
              let converter = AVAudioConverter(
                from: inputFormat, to: outputFormat) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let ratio = outputFormat.sampleRate / inputFormat.sampleRate
        let outputCapacity = AVAudioFrameCount(Double(input.frameLength) * ratio) + 4096
        guard let output = AVAudioPCMBuffer(
            pcmFormat: outputFormat, frameCapacity: outputCapacity) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        nonisolated(unsafe) let sendableInput = input
        nonisolated(unsafe) var provided = false
        var conversionError: NSError?
        converter.convert(to: output, error: &conversionError) { _, status in
            if provided {
                status.pointee = .endOfStream
                return nil
            }
            provided = true
            status.pointee = .haveData
            return sendableInput
        }
        if let conversionError { throw conversionError }
        guard output.frameLength > 0,
              let channel = output.floatChannelData?[0] else { return [] }
        return Array(UnsafeBufferPointer(
            start: channel, count: Int(output.frameLength)))
    }

    nonisolated private static func languageCode(_ language: String) -> String? {
        switch language.lowercased() {
        case "english": "en"
        case "chinese": "zh"
        case "japanese": "ja"
        case "korean": "ko"
        case "german": "de"
        case "french": "fr"
        case "russian": "ru"
        case "portuguese": "pt"
        case "spanish": "es"
        case "italian": "it"
        default: nil
        }
    }

    nonisolated private static func infer(
        samples: [Float], modelPath: String, language: String?,
        abort: WhisperAbortState
    ) throws -> String {
        // Keep routine model details out of CLI diagnostics. Lower-level ggml
        // allocator messages may still appear on stderr, which the JSON
        // contract permits; stdout remains exclusively Bunyi JSON.
        whisper_log_set({ _, _, _ in }, nil)
        var contextParameters = whisper_context_default_params()
        contextParameters.use_gpu = false
        guard let context = modelPath.withCString({
            whisper_init_from_file_with_params($0, contextParameters)
        }) else {
            throw CLIError(
                "transcription_failed", "The Whisper model could not be loaded.",
                exitCode: 10)
        }
        defer { whisper_free(context) }

        var parameters = whisper_full_default_params(WHISPER_SAMPLING_GREEDY)
        parameters.n_threads = Int32(max(
            1, min(8, ProcessInfo.processInfo.activeProcessorCount)))
        parameters.translate = false
        parameters.no_context = true
        parameters.print_progress = false
        parameters.print_realtime = false
        parameters.print_timestamps = false
        parameters.print_special = false
        // Passing "auto" asks Whisper to detect and then transcribe. Its
        // detect_language flag performs detection only and returns no text.
        parameters.detect_language = false
        parameters.abort_callback = { pointer in
            guard let pointer else { return false }
            return Unmanaged<WhisperAbortState>
                .fromOpaque(pointer).takeUnretainedValue().isCancelled
        }
        parameters.abort_callback_user_data = Unmanaged.passUnretained(abort).toOpaque()

        let execute: (UnsafePointer<CChar>?) -> Int32 = { languagePointer in
            parameters.language = languagePointer
            return samples.withUnsafeBufferPointer { buffer in
                whisper_full(
                    context, parameters, buffer.baseAddress,
                    Int32(buffer.count))
            }
        }
        let result = (language ?? "auto").withCString { execute($0) }
        if abort.isCancelled || Task.isCancelled {
            throw CancellationError()
        }
        guard result == 0 else {
            throw CLIError(
                "transcription_failed", "Whisper could not transcribe the audio.",
                exitCode: 10)
        }

        var text = ""
        for index in 0..<whisper_full_n_segments(context) {
            if let segment = whisper_full_get_segment_text(context, index) {
                text += String(cString: segment)
            }
        }
        let tidy = text.split(whereSeparator: \Character.isWhitespace)
            .joined(separator: " ")
        guard !tidy.isEmpty else {
            throw CLIError(
                "transcription_failed",
                "Nothing could be heard in that recording. Try a clearer clip, or type the transcript yourself.",
                exitCode: 3)
        }
        return tidy
    }
}

private final class WhisperAbortState: @unchecked Sendable {
    private let source: CancellationState
    private let lock = NSLock()
    private var requested = false

    init(cancelled: CancellationState) {
        source = cancelled
    }

    var isCancelled: Bool {
        source.value || lock.withLock { requested }
    }

    func cancel() {
        lock.withLock { requested = true }
    }
}
