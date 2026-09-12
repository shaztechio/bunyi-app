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

namespace Bunyi.Core.Engine;

/// <summary>Lossless, language-aware boundaries for bounded long-text inference.</summary>
internal static class SpeechSections
{
    public static IReadOnlyList<string> Split(string text, double maximumSeconds = 20)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        var result = new List<string>();
        var remaining = text;
        while (SpeechDurationEstimate.ForText(remaining).UpperSeconds > maximumSeconds)
        {
            var elements = StringInfo.ParseCombiningCharacters(remaining);
            var low = 1;
            var high = elements.Length - 1;
            var end = elements.Length > 1 ? elements[1] : remaining.Length;
            while (low <= high)
            {
                var middle = (low + high) / 2;
                if (SpeechDurationEstimate.ForText(remaining[..elements[middle]]).UpperSeconds <= maximumSeconds)
                { end = elements[middle]; low = middle + 1; }
                else high = middle - 1;
            }
            var boundary = Boundary(remaining, end);
            result.Add(remaining[..boundary]);
            remaining = remaining[boundary..];
        }
        if (remaining.Length > 0) result.Add(remaining);
        return result;
    }

    public static IReadOnlyList<string> Bisect(string text)
    {
        var elements = StringInfo.ParseCombiningCharacters(text);
        if (elements.Length < 2) return [text];
        var end = Boundary(text, elements[elements.Length / 2]);
        var left = text[..end];
        var right = text[end..];
        return SpeechDurationEstimate.ForText(left).UpperSeconds > 0 &&
               SpeechDurationEstimate.ForText(right).UpperSeconds > 0 ? [left, right] : [text];
    }

    private static int Boundary(string text, int end)
    {
        // Prefer a complete sentence, then a clause, then a word. Decimal dots
        // and common abbreviations are not sentence boundaries. The fallback
        // index always starts a Unicode text element, never half a surrogate.
        for (var priority = 0; priority < 3; priority++)
        {
            for (var i = end - 1; i > 0; i--)
            {
                var c = text[i];
                var next = i + 1;
                while (next < end && "\"'”’)]}".Contains(text[next])) next++;
                var sentence = c is '!' or '?' or '。' or '！' or '？' or '\n' ||
                    c == '.' && (next == text.Length || char.IsWhiteSpace(text[next])) && !Abbreviation(text, i);
                if (priority == 0 && sentence || priority == 1 && c is ';' or ':' or ',' or '，' or '；' or '：' ||
                    priority == 2 && char.IsWhiteSpace(c))
                {
                    var at = priority == 0 ? next : i + 1;
                    while (at < end && char.IsWhiteSpace(text[at])) at++;
                    if (SpeechDurationEstimate.ForText(text[..at]).UpperSeconds > 0) return at;
                }
            }
        }
        return end;
    }

    private static bool Abbreviation(string text, int dot)
    {
        var start = dot - 1;
        while (start >= 0 && char.IsLetter(text[start])) start--;
        var word = text[(start + 1)..dot];
        return word.Length == 1 || word.ToLowerInvariant() is "mr" or "mrs" or "ms" or "dr" or "prof" or "sr" or "jr" or "st" or "vs";
    }
}
