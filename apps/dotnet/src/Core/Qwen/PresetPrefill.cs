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

namespace Bunyi.Core.Qwen;

/// <summary>What a preset-voice generation is being asked for.</summary>
/// <param name="Text">The words to speak.</param>
/// <param name="Speaker">A name from the export's speaker list.</param>
/// <param name="Instruction">The style instruction, or null.</param>
/// <param name="Language">A name from <see cref="Languages" />, or "auto".</param>
public sealed record PresetRequest(
    string Text,
    string Speaker,
    string? Instruction = null,
    string Language = "auto");

/// <summary>
/// Builds the sequence the talker is primed with (spec §1, preset voice).
/// </summary>
/// <remarks>
/// <para>
/// The design layout with one more row: the chosen speaker's row of the codec
/// embedding table, in the slot <see cref="DesignPrefill"/> leaves empty. That
/// is the whole of what a preset voice is to this model — <c>speaker_ids.json</c>
/// maps each name to a row, and the row is a learned embedding of that voice
/// sitting in the codec stream where clone mode puts the encoder's output.
/// </para>
/// <para>
/// The style instruction goes where the design description goes, and reaches
/// the model. The library this replaced dropped it for the 0.6B export by a
/// per-variant flag; a graph that consumes <c>inputs_embeds</c> cannot refuse
/// text conditioning, and macOS feeds it. See RESEARCH-ONNX.md.
/// </para>
/// </remarks>
public sealed class PresetPrefill
{
    private readonly QwenConfig _config;
    private readonly DesignPrefill _layout;
    private readonly NpyArray _codec;

    public PresetPrefill(QwenConfig config, TextProjection text, NpyArray codecEmbedding)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _codec = codecEmbedding ?? throw new ArgumentNullException(nameof(codecEmbedding));
        _layout = new DesignPrefill(config, text, codecEmbedding);

        if (_config.SpeakerIds.Count == 0)
        {
            throw new ArgumentException(
                "This export names no speakers, so it cannot offer a preset voice.",
                nameof(config));
        }
    }

    /// <summary>The width of one position.</summary>
    public int HiddenSize => _config.HiddenSize;

    /// <summary>The text stream's padding, which every generated frame carries.</summary>
    public float[] TrailingHidden => _layout.TrailingHidden;

    /// <summary>The names the export offers, in the order it lists them.</summary>
    public IReadOnlyList<string> Speakers => [.. _config.SpeakerIds.Keys];

    /// <summary>
    /// The codec-table row for a speaker name.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, because the ids file spells them lower-case and the
    /// window shows them capitalised. An unknown name is an error that names
    /// the alternatives rather than a fallback to some other voice: a wrong
    /// voice is not a degraded result, it is a different one.
    /// </remarks>
    internal int SpeakerId(string speaker)
    {
        if (!string.IsNullOrWhiteSpace(speaker) &&
            _config.SpeakerIds.TryGetValue(speaker.Trim(), out var id))
        {
            if (id < 0 || id >= _codec.Rows)
            {
                throw new InvalidDataException(
                    $"Speaker '{speaker}' is row {id} of a codec table with {_codec.Rows} rows. "
                    + "The speaker list does not match the embeddings.");
            }

            return id;
        }

        throw new ArgumentException(
            $"There is no speaker called '{speaker}'. This model offers: "
            + string.Join(", ", _config.SpeakerIds.Keys) + ".",
            nameof(speaker));
    }

    /// <summary>Builds the prefill sequence, one row per position.</summary>
    /// <param name="request">What to say, who says it, and how.</param>
    /// <param name="tokenizer">The export's tokenizer.</param>
    public float[][] Build(PresetRequest request, QwenTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _layout.Build(
            request.Text,
            request.Instruction,
            request.Language,
            tokenizer,
            _codec.Row(SpeakerId(request.Speaker)));
    }
}
