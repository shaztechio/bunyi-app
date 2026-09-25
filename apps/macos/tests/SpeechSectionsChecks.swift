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
struct SpeechSectionsChecks {
    @MainActor
    static func main() async throws {
        precondition(!SpeechDurationEstimate.forText(
            String(repeating: "word ", count: 40)).needsSections)
        precondition(SpeechDurationEstimate.forText(
            String(repeating: "word ", count: 41)).needsSections)
        precondition(SpeechDurationEstimate.forText(" ... ")
            == SpeechDurationEstimate(lowerSeconds: 0, upperSeconds: 0))

        for separator in ["\n", "\r\n", "\r", "\n \t\n"] {
            let first = " \n First paragraph" + separator + "  "
            let second = "Second paragraph\n \n"
            let paragraphs = SpeechSections.split(first + second)
            precondition(paragraphs == [first, second])
            precondition(SpeechDurationEstimate.requiresSections(first + second))
            var audio: [Float] = []
            for _ in paragraphs {
                SectionAudioJoiner.append(Array(repeating: 1, count: 480),
                                          to: &audio, gapMilliseconds: 750)
            }
            precondition(audio.count == 480 * 2 + 18_000)
            precondition(audio[480..<18_480].allSatisfy { $0 == 0 })
            precondition(audio[120] > 0 && audio[18_600] > 0)
        }
        for blank in ["", " \r\n\t\n "] {
            precondition(SpeechSections.split(blank).isEmpty)
            precondition(!SpeechDurationEstimate.requiresSections(blank))
        }
        let single = "\n Only one paragraph \r\n\n"
        precondition(SpeechSections.split(single) == [single])
        precondition(!SpeechDurationEstimate.requiresSections(single))

        let sentence = "Dr. Rivera counted one, two, and three. Then she stopped. "
        let text = String(repeating: sentence, count: 12)
        let sections = SpeechSections.split(text)
        precondition(sections.count > 1)
        precondition(sections.joined() == text)
        precondition(sections.allSatisfy {
            SpeechDurationEstimate.forText($0).upperSeconds <= 20
        })
        precondition(sections[0].contains("Dr."), "Abbreviation was split as a sentence")
        let withParagraph = text + "\r\nA short final paragraph"
        let boundedParagraphs = SpeechSections.split(withParagraph)
        precondition(boundedParagraphs.joined() == withParagraph)
        precondition(boundedParagraphs.last == "A short final paragraph")
        precondition(boundedParagraphs.allSatisfy {
            SpeechDurationEstimate.forText($0).upperSeconds <= 20
        })
        let quoted = SpeechSections.split(
            "Dr. Smith paid 3.14 dollars. “Really?” she asked. " + sentence,
            maximumSeconds: 6)
        precondition(quoted[0] == "Dr. Smith paid 3.14 dollars. “Really?” ")

        let oversized = String(repeating: "extraordinary ", count: 100)
        let words = SpeechSections.split(oversized)
        precondition(words.count > 1 && words.joined() == oversized)

        let scripts = String(repeating: "你好，世界。こんにちは世界。안녕하세요 세계。", count: 12)
        let scriptSections = SpeechSections.split(scripts)
        precondition(scriptSections.count > 1 && scriptSections.joined() == scripts)

        let graphemes = String(repeating: "family 👨‍👩‍👧‍👦 speaks clearly, ", count: 30)
        precondition(SpeechSections.split(graphemes).joined() == graphemes)
        let halves = SpeechSections.bisect(text)
        precondition(halves.count == 2 && halves.joined() == text)

        var joined: [Float] = []
        SectionAudioJoiner.append(Array(repeating: 1, count: 480), to: &joined)
        SectionAudioJoiner.append(Array(repeating: 1, count: 480), to: &joined)
        precondition(joined.count == 480 + 7_200 + 480)
        precondition(joined[0] == 0 && joined[479] == 0)
        precondition(joined[480..<7_680].allSatisfy { $0 == 0 })
        precondition(joined[7_680] == 0 && joined.last == 0)

        // Exactly N-1 gaps, including custom/disabled values and clamping.
        for (milliseconds, expected) in [(0, 0), (1, 24), (750, 18000),
                                          (-1, 0), (5001, 120000)] {
            var audio: [Float] = []
            for _ in 0..<3 {
                SectionAudioJoiner.append(Array(repeating: 1, count: 480),
                    to: &audio, gapMilliseconds: milliseconds)
            }
            precondition(audio.count == 3 * 480 + 2 * expected)
            precondition(audio[480..<(480 + expected)].allSatisfy { $0 == 0 })
            precondition(audio[120] == 1 && audio[audio.count - 121] == 1)
        }

        var recovered: [String] = []
        var attempts = 0
        try await SpeechSections.recover(text) { candidate, depth in
            attempts += 1
            if depth == 0 { return false }
            recovered.append(candidate)
            return true
        }
        precondition(attempts == 3 && recovered.joined() == text)

        attempts = 0
        var leaves = 0
        do {
            try await SpeechSections.recover(text) { _, depth in
                attempts += 1
                guard depth == 2 else { return false }
                leaves += 1
                return leaves < 4
            }
            preconditionFailure("An exhausted section recovery succeeded")
        } catch SpeechSectionRecoveryError.exhausted {}
        precondition(attempts == 7)

        print("Speech section checks passed: lossless boundaries, bounded recovery, estimates, bisection, fades and pauses.")
    }
}
