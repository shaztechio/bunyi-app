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
import Foundation
import whisper

public final class WhisperTranscriptionControl: @unchecked Sendable {
    private let lock = NSLock()
    private var requested = false

    public init() {}

    fileprivate var isCancelled: Bool {
        lock.withLock { requested }
    }

    public func cancel() {
        lock.withLock { requested = true }
    }
}

public enum LocalWhisperTranscriptionError: LocalizedError {
    case emptyAudio
    case modelLoadFailed
    case recognitionFailed
    case emptyTranscript

    public var errorDescription: String? {
        switch self {
        case .emptyAudio:
            "Nothing could be heard in that recording."
        case .modelLoadFailed:
            "The local transcription model could not be loaded."
        case .recognitionFailed:
            "The local transcription model could not transcribe the recording."
        case .emptyTranscript:
            "Nothing could be heard in that recording. Try a clearer clip."
        }
    }
}

/// Multilingual, fully local transcription shared by the desktop app's Auto
/// path and the command-line runtime's Whisper implementation.
public enum LocalWhisperTranscriber {
    public static func transcribe(
        _ url: URL,
        modelURL: URL,
        language: String,
        maximumSeconds: TimeInterval? = nil,
        control: WhisperTranscriptionControl = WhisperTranscriptionControl()
    ) async throws -> String {
        let worker = Task.detached(priority: .userInitiated) {
            let samples = try decodeMono(from: url, maximumSeconds: maximumSeconds)
            guard !samples.isEmpty else {
                throw LocalWhisperTranscriptionError.emptyAudio
            }
            return try infer(
                samples: samples,
                modelPath: modelURL.path,
                language: languageCode(language),
                control: control)
        }
        return try await withTaskCancellationHandler {
            try await worker.value
        } onCancel: {
            control.cancel()
            worker.cancel()
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
        let outputCapacity = AVAudioFrameCount(
            Double(input.frameLength) * ratio) + 4096
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

    nonisolated private static func languageCode(_ language: String) -> String {
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
        default: "auto"
        }
    }

    nonisolated private static func infer(
        samples: [Float], modelPath: String, language: String,
        control: WhisperTranscriptionControl
    ) throws -> String {
        whisper_log_set({ _, _, _ in }, nil)
        var contextParameters = whisper_context_default_params()
        contextParameters.use_gpu = false
        guard let context = modelPath.withCString({
            whisper_init_from_file_with_params($0, contextParameters)
        }) else {
            throw LocalWhisperTranscriptionError.modelLoadFailed
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
        // "auto" asks Whisper to detect and then transcribe. The separate
        // detect_language flag performs detection only and returns no text.
        parameters.detect_language = false
        parameters.abort_callback = { pointer in
            guard let pointer else { return false }
            return Unmanaged<WhisperTranscriptionControl>
                .fromOpaque(pointer).takeUnretainedValue().isCancelled
        }
        parameters.abort_callback_user_data = Unmanaged
            .passUnretained(control).toOpaque()

        let result = language.withCString { languagePointer in
            parameters.language = languagePointer
            return samples.withUnsafeBufferPointer { buffer in
                whisper_full(
                    context, parameters, buffer.baseAddress,
                    Int32(buffer.count))
            }
        }
        if control.isCancelled || Task.isCancelled {
            throw CancellationError()
        }
        guard result == 0 else {
            throw LocalWhisperTranscriptionError.recognitionFailed
        }

        var text = ""
        for index in 0..<whisper_full_n_segments(context) {
            if let segment = whisper_full_get_segment_text(context, index) {
                text += String(cString: segment)
            }
        }
        let tidy = text
            .replacingOccurrences(of: "[BLANK_AUDIO]", with: "")
            .split(whereSeparator: \Character.isWhitespace)
            .joined(separator: " ")
        guard !tidy.isEmpty else {
            throw LocalWhisperTranscriptionError.emptyTranscript
        }
        return tidy
    }
}
