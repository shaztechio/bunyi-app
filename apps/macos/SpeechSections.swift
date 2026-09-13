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

/// A deterministic text-only range used to decide whether inference needs
/// bounded sections. It is not a prediction of the generated take.
struct SpeechDurationEstimate: Equatable {
    let lowerSeconds: Double
    let upperSeconds: Double

    var needsSections: Bool { upperSeconds > 20 }

    static func forText(_ text: String, language _: String = "auto") -> Self {
        var words = 0
        var scriptUnits = 0
        var minorPauses = 0
        var majorPauses = 0
        var inWord = false

        for scalar in text.unicodeScalars {
            let value = scalar.value
            let script = (0x3400...0x9fff).contains(value)
                || (0x20000...0x323af).contains(value)
                || (0x3040...0x30ff).contains(value)
                || (0xac00...0xd7af).contains(value)
            if script {
                scriptUnits += 1
                inWord = false
                continue
            }

            switch scalar.properties.generalCategory {
            case .uppercaseLetter, .lowercaseLetter, .titlecaseLetter,
                 .modifierLetter, .otherLetter, .decimalNumber,
                 .letterNumber, .otherNumber:
                if !inWord { words += 1 }
                inWord = true
            case .nonspacingMark, .spacingMark:
                break
            default:
                if value == 0x27 || value == 0x2019 { continue }
                inWord = false
                if [0x2e, 0x21, 0x3f, 0x3002, 0xff01, 0xff1f].contains(value) {
                    majorPauses += 1
                } else if [0x2c, 0x3b, 0x3a, 0x3001, 0xff0c, 0xff1b,
                           0xff1a].contains(value) {
                    minorPauses += 1
                }
            }
        }

        guard words + scriptUnits > 0 else { return Self(lowerSeconds: 0, upperSeconds: 0) }
        return Self(
            lowerSeconds: Double(words) / 3 + Double(scriptUnits) / 5
                + Double(majorPauses) * 0.15 + Double(minorPauses) * 0.05,
            upperSeconds: Double(words) / 2 + Double(scriptUnits) / 3
                + Double(majorPauses) * 0.4 + Double(minorPauses) * 0.2)
    }
}

/// Lossless sentence-aware boundaries for bounded long-text inference.
enum SpeechSections {
    static func split(_ text: String, maximumSeconds: Double = 20) -> [String] {
        precondition(maximumSeconds > 0)
        var result: [String] = []
        var remaining = text[...]
        while SpeechDurationEstimate.forText(String(remaining)).upperSeconds
                > maximumSeconds {
            let ends = remaining.indices.map { remaining.index(after: $0) }
            guard ends.count > 1 else { break }
            var low = 0
            var high = ends.count - 2
            var chosen = ends[0]
            while low <= high {
                let middle = (low + high) / 2
                let candidate = ends[middle]
                if SpeechDurationEstimate.forText(
                    String(remaining[..<candidate])).upperSeconds <= maximumSeconds {
                    chosen = candidate
                    low = middle + 1
                } else {
                    high = middle - 1
                }
            }
            let cut = boundary(in: remaining, before: chosen)
            result.append(String(remaining[..<cut]))
            remaining = remaining[cut...]
        }
        if !remaining.isEmpty { result.append(String(remaining)) }
        return result
    }

    static func bisect(_ text: String) -> [String] {
        let ends = text.indices.map { text.index(after: $0) }
        guard ends.count >= 2 else { return [text] }
        let raw = ends[max(0, ends.count / 2 - 1)]
        let cut = boundary(in: text[...], before: raw)
        let left = String(text[..<cut])
        let right = String(text[cut...])
        guard SpeechDurationEstimate.forText(left).upperSeconds > 0,
              SpeechDurationEstimate.forText(right).upperSeconds > 0
        else { return [text] }
        return [left, right]
    }

    /// Runs one original section through the bounded binary recovery tree.
    /// A false result means the caller's explicit limit bound; success accepts
    /// that exact text. Depths 0, 1 and 2 permit at most 1 + 2 + 4 attempts.
    @MainActor
    static func recover(
        _ text: String,
        depth: Int = 0,
        attempt: (String, Int) async throws -> Bool
    ) async throws {
        if try await attempt(text, depth) { return }
        let smaller = bisect(text)
        guard depth < 2, smaller.count == 2 else {
            throw SpeechSectionRecoveryError.exhausted
        }
        for part in smaller {
            try await recover(part, depth: depth + 1, attempt: attempt)
        }
    }

    private static func boundary(in text: Substring,
                                 before end: String.Index) -> String.Index {
        let candidates = Array(text[..<end].indices).reversed()
        for priority in 0..<3 {
            for index in candidates {
                let character = text[index]
                let after = text.index(after: index)
                let sentence = isSentenceEnd(character, at: index, in: text)
                let clause = ";:,，；：".contains(character)
                let whitespace = character.isWhitespace
                guard (priority == 0 && sentence)
                        || (priority == 1 && clause)
                        || (priority == 2 && whitespace) else { continue }
                var cut = after
                if priority < 2 {
                    while cut < end, "\"'”’)]}".contains(text[cut]) {
                        cut = text.index(after: cut)
                    }
                }
                while cut < end, text[cut].isWhitespace {
                    cut = text.index(after: cut)
                }
                if SpeechDurationEstimate.forText(
                    String(text[..<cut])).upperSeconds > 0 { return cut }
            }
        }
        return end
    }

    private static func isSentenceEnd(_ character: Character,
                                      at index: String.Index,
                                      in text: Substring) -> Bool {
        if "!?。！？\n".contains(character) { return true }
        guard character == ".", !abbreviation(in: text, dot: index) else {
            return false
        }
        var next = text.index(after: index)
        while next < text.endIndex, "\"'”’)]}".contains(text[next]) {
            next = text.index(after: next)
        }
        return next == text.endIndex || text[next].isWhitespace
    }

    private static func abbreviation(in text: Substring,
                                     dot: String.Index) -> Bool {
        var start = dot
        while start > text.startIndex {
            let previous = text.index(before: start)
            guard text[previous].isLetter else { break }
            start = previous
        }
        let word = text[start..<dot].lowercased()
        return word.count == 1
            || ["mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs"]
                .contains(word)
    }
}

enum SpeechSectionRecoveryError: Error {
    case exhausted
}

/// Applies the spec's fixed seam treatment while preserving raw samples for
/// one final clipping-protection pass.
enum SectionAudioJoiner {
    static func append(_ samples: [Float], to combined: inout [Float],
                       sampleRate: Int = 24_000) {
        guard !samples.isEmpty else { return }
        var part = samples
        let fade = min(Int(Double(sampleRate) * 0.005), part.count / 2)
        if fade > 0 {
            for index in 0..<fade {
                let gain = Float(index) / Float(fade)
                part[index] *= gain
                part[part.count - index - 1] *= gain
            }
        }
        if !combined.isEmpty {
            combined.append(contentsOf: repeatElement(
                0, count: Int(Double(sampleRate) * 0.120)))
        }
        combined.append(contentsOf: part)
    }
}
