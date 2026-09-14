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

/// Coalesces the engine's frame-by-frame status into sentences a screen reader
/// has time to finish. Phase changes are always useful; only progress within
/// generation is time-limited.
struct AccessibilityAnnouncementPacer {
    enum Phase: Equatable {
        case idle
        case checking
        case loading
        case transcribing
        case generating
        case finalizing
        case stopping
        case error
    }

    static let generationInterval: TimeInterval = 10

    private var lastPhase: Phase?
    private var lastMessage: String?
    private var lastAnnouncementAt: Date?

    mutating func announcement(phase: Phase, message: String,
                               at now: Date = Date()) -> String? {
        let phaseChanged = phase != lastPhase
        let errorChanged = phase == .error && message != lastMessage
        let generationIntervalElapsed = phase == .generating
            && lastAnnouncementAt.map {
                now.timeIntervalSince($0) >= Self.generationInterval
            } ?? true

        lastPhase = phase
        lastMessage = message

        guard phaseChanged || errorChanged || generationIntervalElapsed else {
            return nil
        }
        lastAnnouncementAt = now
        return message
    }
}
