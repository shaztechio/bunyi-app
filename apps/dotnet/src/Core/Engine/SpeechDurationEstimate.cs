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

using System.Globalization;
using System.Text;

namespace Bunyi.Core.Engine;

/// <summary>A text-only speech duration range, never an inference-time prediction.</summary>
public sealed record SpeechDurationEstimate(double LowerSeconds, double UpperSeconds)
{
    public bool ShouldStream => UpperSeconds > 20.0;

    /// <summary>
    /// Deterministic heuristic: space-delimited words at 120–180 per minute;
    /// Han, kana, and Hangul units at 3–5 per second. Pauses add a small allowance.
    /// Language is accepted for API parity; script detection handles mixed text.
    /// </summary>
    public static SpeechDurationEstimate ForText(string text, string language = "auto")
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = 0;
        var scriptUnits = 0;
        var minorPauses = 0;
        var majorPauses = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var script = value is >= 0x3400 and <= 0x9fff
                or >= 0x20000 and <= 0x323af
                or >= 0x3040 and <= 0x30ff
                or >= 0xac00 and <= 0xd7af;
            if (script)
            {
                scriptUnits++;
                inWord = false;
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                if (!inWord) words++;
                inWord = true;
            }
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark || value is '\'' or 0x2019)
            {
                // Apostrophes and combining marks do not split a spoken word.
            }
            else
            {
                inWord = false;
                if (value is '.' or '!' or '?' or 0x3002 or 0xff01 or 0xff1f) majorPauses++;
                else if (value is ',' or ';' or ':' or 0x3001 or 0xff0c or 0xff1b or 0xff1a) minorPauses++;
            }
        }
        if (words + scriptUnits == 0) return new(0, 0);
        return new(words / 3.0 + scriptUnits / 5.0 + majorPauses * 0.15 + minorPauses * 0.05,
            words / 2.0 + scriptUnits / 3.0 + majorPauses * 0.4 + minorPauses * 0.2);
    }
}