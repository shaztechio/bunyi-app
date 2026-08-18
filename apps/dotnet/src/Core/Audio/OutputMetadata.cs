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

using System.Text.Json.Serialization;

namespace Bunyi.Core.Audio;

/// <summary>
/// What produced a generated WAV, carried inside the file itself.
/// </summary>
/// <remarks>
/// The filename records only the mode and a timestamp, so everything that
/// actually determined the audio — the text, the voice, the model — was lost
/// the moment the file left the app. Embedding it means a WAV found months
/// later, or sent to someone else, still says how to make it again.
/// </remarks>
public sealed record OutputMetadata
{
    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("language")]
    public required string Language { get; init; }

    // The three modes choose a voice in three different ways, and the macOS UI
    // reuses one text field for two of them — "Style" in preset voice, "Voice"
    // in voice design. Storing both under one key would leave a reader unable
    // to tell a delivery instruction from a voice description, so each gets its
    // own and only the one belonging to the mode is filled.

    /// <summary>Preset voice: the speaker chosen from the model's list.</summary>
    [JsonPropertyName("speaker")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Speaker { get; init; }

    /// <summary>Preset voice: the optional delivery instruction.</summary>
    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; init; }

    /// <summary>Voice design: the description the voice was built from.</summary>
    [JsonPropertyName("voiceDescription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VoiceDescription { get; init; }

    /// <summary>Voice clone: the transcript of the reference clip.</summary>
    [JsonPropertyName("referenceTranscript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferenceTranscript { get; init; }

    [JsonPropertyName("modelRepo")]
    public required string ModelRepo { get; init; }

    [JsonPropertyName("appVersion")]
    public required string AppVersion { get; init; }

    [JsonPropertyName("created")]
    public required DateTimeOffset Created { get; init; }

    /// <summary>
    /// Shown as the title in players that read RIFF INFO.
    /// </summary>
    /// <remarks>
    /// Truncated, because a prompt can be paragraphs long and a title field is
    /// one line. Matches the macOS rule exactly so the two apps tag a file the
    /// same way: first line, 60 characters, then an ellipsis.
    /// </remarks>
    public string Title()
    {
        var trimmed = Text.Trim();
        if (trimmed.Length == 0) return Mode;

        var firstLine = trimmed.Split('\n')[0].TrimEnd('\r');
        return firstLine.Length <= 60 ? firstLine : firstLine[..59] + "…";
    }

    /// <summary>
    /// The voice, however this mode chose one — for display, and for IART.
    /// </summary>
    /// <remarks>
    /// For a clone the reference transcript is the only thing identifying which
    /// voice it was, so it stands in rather than a generic label.
    /// </remarks>
    public string? VoiceSummary()
    {
        if (!string.IsNullOrEmpty(Speaker)) return Speaker;
        if (!string.IsNullOrEmpty(VoiceDescription)) return VoiceDescription;
        if (!string.IsNullOrEmpty(ReferenceTranscript)) return $"Clone of “{ReferenceTranscript}”";
        return null;
    }
}
