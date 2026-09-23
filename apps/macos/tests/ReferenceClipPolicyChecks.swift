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

@main
enum ReferenceClipPolicyChecks {
    static func main() throws {
        precondition(ReferenceClipPolicy.audioMaximumSeconds(
            hasProvidedTranscript: false,
            savedTranscriptAudioSeconds: nil) == 10)
        precondition(ReferenceClipPolicy.audioMaximumSeconds(
            hasProvidedTranscript: true,
            savedTranscriptAudioSeconds: nil) == nil)
        precondition(ReferenceClipPolicy.audioMaximumSeconds(
            hasProvidedTranscript: true,
            savedTranscriptAudioSeconds: 10) == 10)
        precondition(ReferenceClipPolicy.audioMaximumSeconds(
            hasProvidedTranscript: true,
            savedTranscriptAudioSeconds: .nan) == nil)
        precondition(ReferenceClipPolicy.audioMaximumSeconds(
            hasProvidedTranscript: true,
            savedTranscriptAudioSeconds: -1) == nil)
        for rate in [16000, 24000, 48000] {
            var samples = [Float](repeating: 0.2, count: rate * 12)
            for i in (rate * 4)..<(rate * 4 + rate / 5) { samples[i] = 0.00001 }
            for i in (rate * 8)..<(rate * 8 + rate / 5) { samples[i] = 0.00001 }
            let end = try ReferenceClipPolicy.automaticEnd(samples: samples,
                sampleRate: rate, sourceExceedsLimit: true)
            precondition(abs(end - 8.1) < 0.00001)
            // A 100 ms hesitation is not enough; choose the earlier sustained pause.
            for i in (rate * 8)..<(rate * 8 + rate / 5) { samples[i] = 0.2 }
            for i in (rate * 8)..<(rate * 8 + rate / 10) { samples[i] = 0 }
            let earlier = try ReferenceClipPolicy.automaticEnd(samples: samples,
                sampleRate: rate, sourceExceedsLimit: true)
            precondition(abs(earlier - 4.1) < 0.00001)
            let short = try ReferenceClipPolicy.automaticEnd(samples: Array(samples.prefix(rate * 3)),
                sampleRate: rate, sourceExceedsLimit: false)
            precondition(short == 3)
            for value: Float in [0, 0.2] {
                do {
                    _ = try ReferenceClipPolicy.automaticEnd(
                        samples: Array(repeating: value, count: rate * 12),
                        sampleRate: rate, sourceExceedsLimit: true)
                    preconditionFailure("No safe pause should have been found")
                } catch ReferencePauseError.noPause {}
            }
        }
        print("Reference clip checks passed: automatic, full and saved-window alignment.")
    }
}
