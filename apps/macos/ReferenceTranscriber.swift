//
//  ReferenceTranscriber.swift
//  Qwen3 TTS Studio
//
//  Transcribes a voice-clone reference clip on-device with the Speech
//  framework. ICL cloning needs the reference transcript to align the
//  reference audio with words; auto-generating it means the user never has
//  to type it (an empty transcript makes clones come out as gibberish).
//

import AVFoundation
import Foundation
import Speech

enum ReferenceTranscriber {
    /// Speech recognizers expect mono; 16 kHz is the canonical rate.
    private static let recognitionSampleRate: Double = 16000

    static func transcribe(url: URL, locale: Locale) async throws -> String {
        guard await requestAuthorization() else {
            throw TTSError.transcriptionNotAuthorized
        }
        guard let recognizer = SFSpeechRecognizer(locale: locale),
              recognizer.isAvailable else {
            throw TTSError.transcriptionUnavailable
        }

        // Decode in-process — we hold the security scope, the recognition
        // daemon does not, so a file URL request comes back empty. Convert to
        // mono at the recognizer's rate; stereo/odd rates make the task fail.
        let buffer = try decodeMono(from: url)

        // Prefer on-device (stays local). Its model may not be provisioned,
        // which surfaces as an opaque error — fall back to the server model.
        if recognizer.supportsOnDeviceRecognition {
            do {
                return try await recognize(buffer, with: recognizer, onDevice: true)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                await log("On-device transcription failed (\(error.localizedDescription))"
                    + " — retrying with Apple's server recognizer")
            }
        }
        return try await recognize(buffer, with: recognizer, onDevice: false)
    }

    private static func log(_ message: String) async {
        await MainActor.run { LogStore.shared.log(message) }
    }

    private static func recognize(
        _ buffer: AVAudioPCMBuffer, with recognizer: SFSpeechRecognizer,
        onDevice: Bool
    ) async throws -> String {
        let request = SFSpeechAudioBufferRecognitionRequest()
        request.shouldReportPartialResults = false
        request.addsPunctuation = true
        request.requiresOnDeviceRecognition = onDevice

        let text: String = try await withCheckedThrowingContinuation { continuation in
            let once = ResumeOnce(continuation)
            recognizer.recognitionTask(with: request) { result, error in
                if let error {
                    once.resume(throwing: error)
                } else if let result, result.isFinal {
                    once.resume(returning: result.bestTranscription.formattedString)
                }
            }
            request.append(buffer)
            request.endAudio()
        }

        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw TTSError.transcriptionEmpty }
        return trimmed
    }

    /// Whole clip decoded to mono Float32 at `recognitionSampleRate`.
    private static func decodeMono(from url: URL) throws -> AVAudioPCMBuffer {
        let file = try AVAudioFile(forReading: url)
        let inFormat = file.processingFormat
        guard let read = AVAudioPCMBuffer(
            pcmFormat: inFormat,
            frameCapacity: AVAudioFrameCount(file.length)) else {
            throw TTSError.referenceAudioUnreadable
        }
        try file.read(into: read)

        guard let outFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: recognitionSampleRate,
            channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: inFormat, to: outFormat)
        else { throw TTSError.referenceAudioUnreadable }

        let ratio = recognitionSampleRate / inFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(read.frameLength) * ratio) + 4096
        guard let outBuffer = AVAudioPCMBuffer(
            pcmFormat: outFormat, frameCapacity: capacity) else {
            throw TTSError.referenceAudioUnreadable
        }

        // The input block is @Sendable but runs synchronously inside convert,
        // so nothing actually crosses a thread boundary here.
        nonisolated(unsafe) let inBuffer = read
        nonisolated(unsafe) var provided = false
        var convError: NSError?
        converter.convert(to: outBuffer, error: &convError) { _, inStatus in
            if provided {
                inStatus.pointee = .endOfStream
                return nil
            }
            provided = true
            inStatus.pointee = .haveData
            return inBuffer
        }
        if let convError { throw convError }
        guard outBuffer.frameLength > 0 else { throw TTSError.referenceAudioEmpty }
        return outBuffer
    }

    private static func requestAuthorization() async -> Bool {
        if SFSpeechRecognizer.authorizationStatus() == .authorized { return true }
        return await withCheckedContinuation { continuation in
            SFSpeechRecognizer.requestAuthorization { status in
                continuation.resume(returning: status == .authorized)
            }
        }
    }
}

/// Guards a checked continuation so the recognition callback — which fires
/// more than once — resumes it exactly one time.
private final class ResumeOnce: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false
    private let continuation: CheckedContinuation<String, Error>

    init(_ continuation: CheckedContinuation<String, Error>) {
        self.continuation = continuation
    }

    func resume(returning value: String) {
        lock.lock(); defer { lock.unlock() }
        guard !done else { return }
        done = true
        continuation.resume(returning: value)
    }

    func resume(throwing error: Error) {
        lock.lock(); defer { lock.unlock() }
        guard !done else { return }
        done = true
        continuation.resume(throwing: error)
    }
}
