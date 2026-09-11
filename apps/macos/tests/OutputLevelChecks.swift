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
struct OutputLevelChecks {
    static func main() throws {
        let wave: [Float] = [0, 0.5, 1, 1.5, 2, -2, -1]
        let prepared = try OutputLevel.prepare(wave)
        let expected: [Float] = [0, 0.245, 0.49, 0.735, 0.98, -0.98, -0.49]
        for (actual, target) in zip(prepared.samples, expected) {
            precondition(abs(actual - target) < 0.000001)
        }
        precondition(wave[4] == 2)
        let unchangedClips: [[Float]] = [[], [0, 0], [-1, -0.25, 0, 0.5, 1]]
        for unchanged in unchangedClips {
            let result = try OutputLevel.prepare(unchanged)
            precondition(result.samples == unchanged && result.gain == 1)
        }
        let extreme = try OutputLevel.prepare([Float.greatestFiniteMagnitude, -Float.greatestFiniteMagnitude])
        precondition(extreme.samples == [0.98, -0.98])
        let invalidSamples: [Float] = [.nan, .infinity, -.infinity]
        for invalid in invalidSamples {
            do {
                _ = try OutputLevel.prepare([0, invalid])
                preconditionFailure("Invalid audio must fail before saving")
            } catch OutputLevel.InvalidAudio.nonFiniteSample {
                // Expected; do not save a successful but invalid output.
            }
        }
        print("Output level checks passed")
    }
}
