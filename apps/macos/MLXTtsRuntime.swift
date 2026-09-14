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
import MLX
import Qwen3TTS

/// Public, Sendable facts about the model currently held by the MLX runtime.
public struct MLXTtsModelInfo: Sendable {
    public let sampleRate: Int
    public let supportedSpeakers: [String]
}

/// A complete inference attempt, converted to ordinary Swift values before it
/// crosses the runtime actor boundary.
public struct MLXTtsTake: Sendable {
    public let samples: [Float]
    public let frames: Int
    public let reachedEndOfSpeech: Bool
}

/// Memory-release measurements used by the desktop log and CLI diagnostics.
public struct MLXMemoryRelease: Sendable {
    public let freedBytes: Int
    public let activeBytes: Int
    public let elapsedSeconds: TimeInterval
}

/// Cooperative cancellation shared with swift-qwen3-tts' synchronous loops.
public final class MLXTtsGenerationControl: @unchecked Sendable {
    private let lock = NSLock()
    private var running = true

    public init() {}

    public func cancel() { lock.withLock { running = false } }
    public func shouldContinue() -> Bool { lock.withLock { running } }
}

private final class MLXTtsTerminationReceipt: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Qwen3TTSTermination?

    func set(_ value: Qwen3TTSTermination) {
        lock.withLock { stored = value }
    }

    var value: Qwen3TTSTermination? { lock.withLock { stored } }
}

private final class MLXTtsFrameCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var stored = 0

    func increment() -> Int {
        lock.withLock {
            stored += 1
            return stored
        }
    }

    var value: Int { lock.withLock { stored } }
}

public enum MLXTtsRuntimeError: LocalizedError {
    case busy

    public var errorDescription: String? {
        "The MLX runtime is already generating speech."
    }
}

/// The only owner of the non-Sendable Qwen model.
///
/// Model loading and inference enter through this actor, so the app and CLI
/// cannot touch one MLX model concurrently. Results leave as `[Float]` and
/// other Sendable values; the Qwen model and MLX arrays never escape.
public actor MLXTtsRuntime {
    private var model: Qwen3TTSModel?
    private var loadedRepo: String?
    private var activeGeneration = false

    public init() {}

    public func prepare(repoID: String, directory: URL) async throws
        -> MLXTtsModelInfo {
        guard !activeGeneration else { throw MLXTtsRuntimeError.busy }
        if let model, loadedRepo == repoID {
            return MLXTtsModelInfo(
                sampleRate: model.sampleRate,
                supportedSpeakers: model.supportedSpeakers)
        }
        model = nil
        loadedRepo = nil
        MLX.GPU.clearCache()
        let loaded = try await Qwen3TTSModel.fromPretrained(directory.path)
        model = loaded
        loadedRepo = repoID
        return MLXTtsModelInfo(
            sampleRate: loaded.sampleRate,
            supportedSpeakers: loaded.supportedSpeakers)
    }

    /// Drops the resident model and returns whether there was one to release.
    @discardableResult
    public func unload() -> Bool {
        guard !activeGeneration else { return false }
        let hadModel = model != nil || loadedRepo != nil
        model = nil
        loadedRepo = nil
        MLX.GPU.clearCache()
        return hadModel
    }

    public func releaseWorkingMemory() -> MLXMemoryRelease {
        guard !activeGeneration else {
            return MLXMemoryRelease(
                freedBytes: 0, activeBytes: MLX.GPU.activeMemory,
                elapsedSeconds: 0)
        }
        let cachedBefore = MLX.GPU.cacheMemory
        let start = Date()
        MLX.GPU.clearCache()
        return MLXMemoryRelease(
            freedBytes: cachedBefore - MLX.GPU.cacheMemory,
            activeBytes: MLX.GPU.activeMemory,
            elapsedSeconds: Date().timeIntervalSince(start))
    }

    public func generateBounded(
        mode: TTSMode,
        text: String,
        speaker: String?,
        instruct: String?,
        language: String,
        referenceSamples: [Float]?,
        referenceText: String?,
        maximumFrames: Int?,
        control: MLXTtsGenerationControl,
        onFrame: @escaping @Sendable (Int) -> Void
    ) async throws -> MLXTtsTake {
        guard !activeGeneration else { throw MLXTtsRuntimeError.busy }
        guard let model else { throw TTSError.noAudio }
        activeGeneration = true
        defer { activeGeneration = false }
        let frames = MLXTtsFrameCounter()
        let termination = MLXTtsTerminationReceipt()
        let wav: MLXArray = try await withTaskCancellationHandler {
            let onToken: (Int) -> Void = { _ in
                onFrame(frames.increment())
            }
            let onTermination: @Sendable (Qwen3TTSTermination) -> Void = {
                termination.set($0)
            }
            switch mode {
            case .presetVoice:
                guard let speaker else { throw TTSError.noAudio }
                return try model.generateCustomVoice(
                    text: text, speaker: speaker, language: language,
                    instruct: instruct, maxTokens: maximumFrames,
                    useTextLengthLimit: false,
                    shouldContinue: control.shouldContinue,
                    onTermination: onTermination, onToken: onToken)
            case .voiceDesign:
                return try model.generateVoiceDesign(
                    text: text, language: language, instruct: instruct,
                    maxTokens: maximumFrames, useTextLengthLimit: false,
                    shouldContinue: control.shouldContinue,
                    onTermination: onTermination, onToken: onToken)
            case .voiceClone:
                guard let referenceSamples, let referenceText else {
                    throw TTSError.missingReference
                }
                return try model.generateVoiceClone(
                    text: text, referenceAudio: MLXArray(referenceSamples),
                    referenceText: referenceText, language: language,
                    maxTokens: maximumFrames, useTextLengthLimit: false,
                    shouldContinue: control.shouldContinue,
                    onTermination: onTermination, onToken: onToken)
            }
        } onCancel: {
            control.cancel()
        }
        try Task.checkCancellation()
        return MLXTtsTake(
            samples: wav.asArray(Float.self), frames: frames.value,
            reachedEndOfSpeech: termination.value == .endOfSpeech)
    }

    public func generateStreaming(
        mode: TTSMode,
        text: String,
        speaker: String?,
        instruct: String?,
        language: String,
        control: MLXTtsGenerationControl,
        onFrame: @escaping @Sendable (Int) -> Void
    ) async throws -> MLXTtsTake {
        guard !activeGeneration else { throw MLXTtsRuntimeError.busy }
        guard let model else { throw TTSError.noAudio }
        activeGeneration = true
        defer { activeGeneration = false }
        let stream = model.generateStream(
            text: text,
            speaker: mode == .presetVoice ? speaker : nil,
            instruct: instruct,
            language: language,
            maxTokens: nil,
            useTextLengthLimit: false,
            shouldContinue: control.shouldContinue)
        var final: MLXArray?
        var frames = 0
        var termination: Qwen3TTSTermination?
        var cancelled = false
        do {
            for try await event in stream {
                if Task.isCancelled {
                    cancelled = true
                    control.cancel()
                    continue
                }
                switch event {
                case .token:
                    frames += 1
                    onFrame(frames)
                case .info(let info):
                    termination = info.termination
                case .audio(let wav):
                    final = wav
                }
            }
        } catch {
            control.cancel()
            throw error
        }
        if cancelled || Task.isCancelled { throw CancellationError() }
        guard let final else { throw TTSError.noAudio }
        return MLXTtsTake(
            samples: final.asArray(Float.self), frames: frames,
            reachedEndOfSpeech: termination == .endOfSpeech)
    }
}
