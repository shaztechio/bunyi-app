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
enum AccessibilityAnnouncementPacerChecks {
    static func main() {
        let start = Date(timeIntervalSince1970: 1_000)
        var pacer = AccessibilityAnnouncementPacer()

        expect(pacer.announcement(phase: .checking,
                                  message: "Checking the voice model.",
                                  at: start),
               "Checking the voice model.")
        expect(pacer.announcement(phase: .checking,
                                  message: "Checking the voice model.",
                                  at: start.addingTimeInterval(30)), nil)

        expect(pacer.announcement(phase: .generating,
                                  message: "Creating your speech.",
                                  at: start.addingTimeInterval(31)),
               "Creating your speech.")
        expect(pacer.announcement(phase: .generating,
                                  message: "Creating your speech, 12 frames.",
                                  at: start.addingTimeInterval(40.9)), nil)
        expect(pacer.announcement(phase: .generating,
                                  message: "Creating your speech, 25 frames.",
                                  at: start.addingTimeInterval(41)),
               "Creating your speech, 25 frames.")

        expect(pacer.announcement(phase: .finalizing,
                                  message: "Preparing your audio file.",
                                  at: start.addingTimeInterval(41.1)),
               "Preparing your audio file.")
        expect(pacer.announcement(phase: .idle,
                                  message: "Your audio is ready.",
                                  at: start.addingTimeInterval(41.2)),
               "Your audio is ready.")

        expect(pacer.announcement(phase: .error,
                                  message: "Generation failed. First error.",
                                  at: start.addingTimeInterval(42)),
               "Generation failed. First error.")
        expect(pacer.announcement(phase: .error,
                                  message: "Generation failed. Second error.",
                                  at: start.addingTimeInterval(42.1)),
               "Generation failed. Second error.")
        expect(pacer.announcement(phase: .error,
                                  message: "Generation failed. Second error.",
                                  at: start.addingTimeInterval(60)), nil)

        print("Accessibility announcement pacing checks passed")
    }

    private static func expect(_ actual: String?, _ expected: String?,
                               file: StaticString = #file,
                               line: UInt = #line) {
        guard actual == expected else {
            fatalError("Expected \(String(describing: expected)), got "
                       + "\(String(describing: actual))", file: file, line: line)
        }
    }
}
