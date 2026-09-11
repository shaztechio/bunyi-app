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

/// Prevent conversion clipping without flattening peaks or amplifying quiet audio.
enum OutputLevel {
    struct Prepared {
        let samples: [Float]
        let gain: Double
    }

    enum InvalidAudio: LocalizedError {
        case nonFiniteSample

        var errorDescription: String? {
            "The model produced invalid audio. Please generate again."
        }
    }

    static func prepare(_ samples: [Float]) throws -> Prepared {
        var peak = 0.0
        for sample in samples {
            guard sample.isFinite else { throw InvalidAudio.nonFiniteSample }
            peak = max(peak, abs(Double(sample)))
        }
        guard peak > 1 else { return Prepared(samples: samples, gain: 1) }
        let gain = 0.98 / peak
        return Prepared(samples: samples.map { Float(Double($0) * gain) }, gain: gain)
    }
}
