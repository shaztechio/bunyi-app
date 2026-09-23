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

/// A clone transcript and its audio must describe the same short utterance.
/// Automatic transcription samples a bounded leading window; carrying the
/// same limit into inference prevents a partial transcript/full-audio mismatch.
public enum ReferenceClipPolicy {
    public static let automaticTranscriptSeconds: TimeInterval = 10

    /// A provided transcript is assumed to cover the full clip unless its
    /// saved recipe records a narrower window. A missing transcript always
    /// uses the same bounded window as automatic transcription.
    public static func audioMaximumSeconds(
        hasProvidedTranscript: Bool,
        savedTranscriptAudioSeconds: TimeInterval?
    ) -> TimeInterval? {
        if !hasProvidedTranscript { return automaticTranscriptSeconds }
        guard let savedTranscriptAudioSeconds,
              savedTranscriptAudioSeconds.isFinite,
              savedTranscriptAudioSeconds > 0 else { return nil }
        return savedTranscriptAudioSeconds
    }
}


public enum ReferencePauseError: LocalizedError {
    case noPause
    public var errorDescription: String? {
        "No clear pause was found in the first 10 seconds. Choose a shorter reference recording that ends at a natural pause."
    }
}

extension ReferenceClipPolicy {
    /// Energy-based pause selection; a quiet boundary, not a sentence detector.
    public static func automaticEnd(samples: [Float], sampleRate: Int,
                                    sourceExceedsLimit: Bool) throws -> TimeInterval {
        precondition(sampleRate > 0)
        guard sourceExceedsLimit else { return Double(samples.count) / Double(sampleRate) }
        let window = max(1, sampleRate / 100)
        let count = min(samples.count / window, 1000)
        var levels: [Double] = []
        for frame in 0..<count {
            let values = samples[(frame * window)..<((frame + 1) * window)]
            levels.append(sqrt(values.reduce(0) { $0 + Double($1) * Double($1) } / Double(window)))
        }
        let threshold = min(0.01, max(0.0001, (levels.max() ?? 0) * 0.03))
        var quietStart: Int?
        var heardSpeech = false
        var selected: Int?
        for (index, rms) in levels.enumerated() {
            if rms <= threshold {
                if quietStart == nil { quietStart = index }
                if let start = quietStart, start >= 200, heardSpeech, index - start + 1 >= 15 {
                    selected = start + 10
                }
            } else {
                heardSpeech = true
                quietStart = nil
            }
        }
        guard let selected else { throw ReferencePauseError.noPause }
        return Double(selected * window) / Double(sampleRate)
    }

    /// Read only the bounded analysis window, preserving the file's time base.
    public static func automaticWindow(url: URL) async throws -> TimeInterval {
        try await Task.detached(priority: .userInitiated) {
            try Task.checkCancellation()
            let file = try AVAudioFile(forReading: url)
            let format = file.processingFormat
            let limit = AVAudioFramePosition(automaticTranscriptSeconds * format.sampleRate)
            let count = AVAudioFrameCount(min(file.length, limit))
            guard count > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: count) else {
                throw ReferencePauseError.noPause
            }
            try file.read(into: buffer, frameCount: count)
            guard let channels = buffer.floatChannelData else { throw ReferencePauseError.noPause }
            var mono = [Float](repeating: 0, count: Int(buffer.frameLength))
            for channel in 0..<Int(format.channelCount) {
                for frame in mono.indices {
                    mono[frame] += channels[channel][frame * buffer.stride] / Float(format.channelCount)
                }
            }
            return try automaticEnd(samples: mono, sampleRate: Int(format.sampleRate),
                                    sourceExceedsLimit: file.length > limit)
        }.value
    }
}
