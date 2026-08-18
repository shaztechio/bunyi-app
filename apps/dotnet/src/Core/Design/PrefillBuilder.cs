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

using System.Numerics.Tensors;

namespace Bunyi.Core.Design;

/// <summary>What a generation is being asked for.</summary>
/// <param name="Text">The words to speak.</param>
/// <param name="Instruction">The voice description, or null.</param>
/// <param name="Language">A name from <see cref="Languages" />, or "auto".</param>
public sealed record DesignRequest(string Text, string? Instruction, string Language = "auto");

/// <summary>
/// Builds the sequence the talker is primed with (spec §1, design mode).
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what makes a design generation different from a preset
/// one. The graphs are identical between the exports (see
/// <c>RESEARCH-ONNX.md</c>); what changes is that a speaker embedding is
/// replaced by a described voice, and the description is simply more text at
/// the front of the sequence.
/// </para>
/// <para>
/// Two streams run in parallel through one sequence of vectors: a text stream
/// and a codec stream, <b>added together</b> position by position. Where one has
/// something to say the other carries its padding token. Getting a pad wrong,
/// or adding where the reference concatenates, produces a sequence of exactly
/// the right length that means something else — so the shape of this is pinned
/// by tests rather than by reading.
/// </para>
/// </remarks>
public sealed class PrefillBuilder(
    DesignConfig config,
    TextProjection text,
    NpyArray codecEmbedding)
{
    private readonly DesignConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly TextProjection _text = text ?? throw new ArgumentNullException(nameof(text));

    private readonly NpyArray _codec =
        codecEmbedding ?? throw new ArgumentNullException(nameof(codecEmbedding));

    /// <summary>The width of one position.</summary>
    public int HiddenSize => _config.HiddenSize;

    /// <summary>
    /// The text stream's padding, which every generated frame carries.
    /// </summary>
    /// <remarks>
    /// Once the words are in the sequence the text stream has nothing left to
    /// say, so every frame from then on pairs its codes with this. Held rather
    /// than recomputed: it is the same projection every frame, and there can be
    /// thousands.
    /// </remarks>
    public float[] TrailingHidden => _trailingHidden ??= _text.Project(_config.TtsPadTokenId);

    private float[]? _trailingHidden;

    /// <summary>
    /// The codec tokens that open the sequence.
    /// </summary>
    /// <remarks>
    /// A named language opens a "thinking" span carrying that language's token;
    /// <c>auto</c> opens a "not thinking" one and names nothing, leaving the
    /// model to decide from the text. The two spans are different lengths,
    /// which is why this returns a list rather than filling a fixed one.
    /// </remarks>
    internal IReadOnlyList<int> CodecPrefix(string language)
    {
        if (!string.IsNullOrWhiteSpace(language) &&
            _config.LanguageIds.TryGetValue(language, out var id))
        {
            return [_config.CodecThinkId, _config.CodecThinkBosId, id, _config.CodecThinkEosId];
        }

        return [_config.CodecNoThinkId, _config.CodecThinkBosId, _config.CodecThinkEosId];
    }

    /// <summary>
    /// Builds the prefill sequence, one row per position.
    /// </summary>
    /// <param name="request">What to say, and how.</param>
    /// <param name="tokenizer">The export's tokenizer.</param>
    public float[][] Build(DesignRequest request, QwenTokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tokenizer);

        // The chat template the model was trained with. The role turn is opened
        // twice on purpose: once around the text, and once left open for the
        // answer the model is about to speak.
        var chat = tokenizer.Encode(
            $"<|im_start|>assistant\n{request.Text}<|im_end|>\n<|im_start|>assistant\n");

        if (chat.Count < 9)
        {
            throw new ArgumentException(
                "There is nothing to say: the text tokenized to nothing.", nameof(request));
        }

        var rows = new List<float[]>();

        var ttsPad = _text.Project(_config.TtsPadTokenId);
        var ttsBos = _text.Project(_config.TtsBosTokenId);
        var ttsEos = _text.Project(_config.TtsEosTokenId);
        var codecPad = _codec.Row(_config.CodecPadId);
        var codecBos = _codec.Row(_config.CodecBosId);

        // 1. The voice description, if there is one, ahead of everything. It is
        //    the whole of what design mode adds, and it goes before the role so
        //    the model reads it as instruction rather than as speech.
        if (!string.IsNullOrWhiteSpace(request.Instruction))
        {
            var instruction = tokenizer.Encode(
                $"<|im_start|>user\n{request.Instruction}<|im_end|>\n");

            foreach (var token in instruction) rows.Add(_text.Project(token));
        }

        // 2. The role prefix: <|im_start|>, "assistant", newline. Text only —
        //    the codec stream has not started yet.
        for (var i = 0; i < 3; i++) rows.Add(_text.Project(chat[i]));

        // 3. The codec prefix, each position pairing text padding with one
        //    codec token.
        foreach (var token in CodecPrefix(request.Language))
        {
            rows.Add(Add(ttsPad, _codec.Row(token)));
        }

        // 4. A speaker embedding would go here for a preset-voice export. A
        //    design export has no speakers — spk_id is empty — and the
        //    description in step 1 is what stands in for one.

        // 5. The turn: the text stream opens while the codec stream pads.
        rows.Add(Add(ttsBos, codecPad));

        // 6. The words. Every text token is paired with codec padding, because
        //    the codec stream produces nothing until generation starts.
        //
        //    chat[3..^5] drops the role prefix taken in step 2 and the five
        //    tokens that close and reopen the turn — <|im_end|>, newline,
        //    <|im_start|>, "assistant", newline — which the sequence expresses
        //    through its own tokens instead.
        for (var i = 3; i < chat.Count - 5; i++)
        {
            rows.Add(Add(_text.Project(chat[i]), codecPad));
        }

        rows.Add(Add(ttsEos, codecPad));

        // 7. The last position: text padding against the codec stream's start.
        //    This is the position the first frame is generated from.
        rows.Add(Add(ttsPad, codecBos));

        return [.. rows];
    }

    /// <summary>
    /// One position from the text stream and the codec stream together.
    /// </summary>
    /// <remarks>
    /// Added, not concatenated. Both are the model's hidden width, and the
    /// sequence stays that width however many streams are combined — which is
    /// how the two carry independent meaning through one set of vectors.
    /// </remarks>
    internal static float[] Add(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException(
                $"A text vector of {left.Length} cannot be added to a codec vector of "
                + $"{right.Length}. The embeddings folder does not match this model.");
        }

        var sum = new float[left.Length];
        TensorPrimitives.Add(left, right, sum);
        return sum;
    }
}
