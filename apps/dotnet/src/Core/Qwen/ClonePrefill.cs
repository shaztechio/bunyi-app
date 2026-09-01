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

/// <summary>What a clone is being asked for.</summary>
/// <param name="Text">The words to speak.</param>
/// <param name="ReferenceTranscript">What the reference clip says, word for word.</param>
/// <param name="Language">A name from <see cref="Languages" />, or "auto".</param>
public sealed record CloneRequest(string Text, string ReferenceTranscript, string Language = "auto");

/// <summary>
/// Builds the sequence the talker is primed with (spec §1, clone mode).
/// </summary>
/// <remarks>
/// <para>
/// In-context learning: the model is shown a clip and told what it says, then
/// asked to say something else. That is why §4 calls the transcript effectively
/// mandatory — it is the half of the example that makes the other half mean
/// anything. An empty one leaves the model a recording with no idea which sounds
/// were which words.
/// </para>
/// <para>
/// The layout is <see cref="DesignPrefill" />'s with three differences, and it is
/// worth seeing it that way rather than as a separate thing: the reference words
/// go in front of the words to speak, a speaker embedding fills the slot design
/// mode leaves empty, and the reference's own codes follow the codec stream's
/// opening token. Take the reference away and this reduces exactly to the design
/// layout — which is the strongest evidence available that both are right, given
/// what the reference script for this one looks like.
/// </para>
/// </remarks>
public sealed class ClonePrefill(
    QwenConfig config,
    TextProjection text,
    NpyArray codecEmbedding,
    NpyArray[] groupEmbeddings)
{
    private readonly QwenConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly TextProjection _text = text ?? throw new ArgumentNullException(nameof(text));

    private readonly NpyArray _codec =
        codecEmbedding ?? throw new ArgumentNullException(nameof(codecEmbedding));

    private readonly NpyArray[] _groups =
        groupEmbeddings ?? throw new ArgumentNullException(nameof(groupEmbeddings));

    /// <summary>The width of one position.</summary>
    public int HiddenSize => _config.HiddenSize;

    /// <summary>
    /// The text stream's padding, which every generated frame carries.
    /// </summary>
    public float[] TrailingHidden => _trailingHidden ??= _text.Project(_config.TtsPadTokenId);

    private float[]? _trailingHidden;

    /// <summary>
    /// The codec tokens that open the sequence, as in design mode.
    /// </summary>
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
    /// <param name="request">What to say, and what the reference says.</param>
    /// <param name="tokenizer">The export's tokenizer.</param>
    /// <param name="speaker">The speaker encoder's output for the clip.</param>
    /// <param name="referenceCodes">The clip's codes, one array of groups per frame.</param>
    public float[][] Build(
        CloneRequest request,
        QwenTokenizer tokenizer,
        ReadOnlySpan<float> speaker,
        IReadOnlyList<int[]> referenceCodes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(referenceCodes);

        if (speaker.Length != _config.HiddenSize)
        {
            throw new ArgumentException(
                $"The speaker encoder returned {speaker.Length} numbers where this model "
                + $"expects {_config.HiddenSize}. The encoder does not match the "
                + "speech model.",
                nameof(speaker));
        }

        if (referenceCodes.Count == 0)
        {
            throw new ArgumentException(
                "The reference recording produced no codes. It may be too short to use.",
                nameof(referenceCodes));
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceTranscript))
        {
            // Not a guess we are willing to make. Without it the model hears
            // sounds it cannot align to words, and returns something confident
            // and wrong rather than failing — so this fails instead.
            throw new ArgumentException(
                "A clone needs to know what the recording says. Type the transcript, "
                + "or let it be filled in automatically.",
                nameof(request));
        }

        var chat = tokenizer.Encode(
            $"<|im_start|>assistant\n{request.Text}<|im_end|>\n<|im_start|>assistant\n");

        if (chat.Count < 9)
        {
            throw new ArgumentException(
                "There is nothing to say: the text tokenized to nothing.", nameof(request));
        }

        // The reference turn is closed rather than left open: it is an example
        // that finished, not the thing being answered.
        var reference = tokenizer.Encode(
            $"<|im_start|>assistant\n{request.ReferenceTranscript}<|im_end|>\n");

        if (reference.Count < 6)
        {
            throw new ArgumentException(
                "The transcript tokenized to nothing. It should be what the recording says.",
                nameof(request));
        }

        var rows = new List<float[]>();

        var ttsPad = _text.Project(_config.TtsPadTokenId);
        var ttsBos = _text.Project(_config.TtsBosTokenId);
        var ttsEos = _text.Project(_config.TtsEosTokenId);
        var codecPad = _codec.Row(_config.CodecPadId);
        var codecBos = _codec.Row(_config.CodecBosId);

        // 1. The role prefix, text only.
        for (var i = 0; i < 3; i++) rows.Add(_text.Project(chat[i]));

        // 2. The codec prefix.
        foreach (var token in CodecPrefix(request.Language))
        {
            rows.Add(DesignPrefill.Add(ttsPad, _codec.Row(token)));
        }

        // 3. The speaker slot — the position design mode has nothing to put in.
        //    The encoder's output is the model's hidden width, so it sits in the
        //    codec stream directly rather than being projected into it.
        rows.Add(DesignPrefill.Add(ttsPad, speaker));

        // 4. The turn opens.
        rows.Add(DesignPrefill.Add(ttsBos, codecPad));

        // 5. The text stream, in full, before the codec stream says anything:
        //    what the reference says, then what to say. Both streams still run
        //    together, so every one of these positions carries codec padding.
        //
        //    reference[3..^2] drops the role prefix and the two tokens that
        //    close the turn; chat[3..^5] drops the role prefix and the five that
        //    close and reopen it.
        for (var i = 3; i < reference.Count - 2; i++)
        {
            rows.Add(DesignPrefill.Add(_text.Project(reference[i]), codecPad));
        }

        for (var i = 3; i < chat.Count - 5; i++)
        {
            rows.Add(DesignPrefill.Add(_text.Project(chat[i]), codecPad));
        }

        rows.Add(DesignPrefill.Add(ttsEos, codecPad));

        // 6. The codec stream: its opening token, then the reference's own
        //    frames. This is the example — the model has now been shown the
        //    words and the sound of them together, and the last position is
        //    where it starts answering.
        rows.Add(DesignPrefill.Add(ttsPad, codecBos));

        foreach (var frame in referenceCodes)
        {
            rows.Add(DesignPrefill.Add(ttsPad, FrameEmbedding(frame)));
        }

        return [.. rows];
    }

    /// <summary>
    /// One reference frame as a single vector.
    /// </summary>
    /// <remarks>
    /// A frame is sixteen codes from sixteen codebooks, and they are <b>summed</b>
    /// rather than laid side by side — the same trick the two streams use, one
    /// position holding several meanings at once. Each group has its own table,
    /// so a code from group 3 read out of group 4's table is a valid row and a
    /// different sound.
    /// </remarks>
    internal float[] FrameEmbedding(int[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Length != _config.CodeGroups)
        {
            throw new ArgumentException(
                $"A frame of {frame.Length} codes cannot be read by a model with "
                + $"{_config.CodeGroups} codebooks.",
                nameof(frame));
        }

        var sum = new float[_config.HiddenSize];
        _codec.Row(frame[0]).CopyTo(sum);

        for (var group = 1; group < frame.Length; group++)
        {
            var table = _groups[group - 1];
            var row = table.Row(frame[group]);
            for (var i = 0; i < sum.Length; i++) sum[i] += row[i];
        }

        return sum;
    }
}
