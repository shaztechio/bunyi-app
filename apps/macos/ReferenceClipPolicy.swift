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
