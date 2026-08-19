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

namespace Bunyi.Core;

/// <summary>
/// Turns a model's own identifier into something to show a person.
/// </summary>
/// <remarks>
/// <para>
/// Speakers and languages are identifiers the model publishes —
/// <c>uncle_fu</c>, <c>english</c> — and they are what gets sent to it and
/// written into a clip's metadata. They were also being shown verbatim, so the
/// pickers read <c>auto</c> and <c>english</c>, and the speaker list changed
/// from <c>Ryan</c> to <c>ryan</c> the moment a model loaded and replaced the
/// built-in list with its own.
/// </para>
/// <para>
/// <b>Only the display changes.</b> The identifier is still what the model is
/// given and what is recorded, because it is the model's word and not ours to
/// tidy. This is the one rule for turning one into the other, in Core rather
/// than in the window, because History shows the stored identifier too and the
/// two must not disagree.
/// </para>
/// </remarks>
public static class DisplayName
{
    /// <summary>The presentable form of an identifier.</summary>
    /// <remarks>
    /// Underscores become spaces and each word is capitalised, which covers
    /// every identifier these models publish. Anything already capitalised is
    /// left as it is, so the built-in list and a loaded model's list look the
    /// same — which is the point.
    /// </remarks>
    public static string For(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return string.Empty;

        var words = identifier.Trim().Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return identifier.Trim();

        return string.Join(' ', words.Select(Capitalise));
    }

    private static string Capitalise(string word) =>
        word.Length switch
        {
            0 => word,

            // Single letters and initialisms are left alone: lowercasing the
            // tail of something already capitalised would turn a name the model
            // spelled deliberately into a different one.
            1 => word.ToUpper(CultureInfo.InvariantCulture),
            _ => char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..],
        };
}
