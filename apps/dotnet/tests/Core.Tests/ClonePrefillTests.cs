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

using Bunyi.Core.Engine;
using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The in-context sequence a clone is primed with (spec §1, §4).
/// </summary>
/// <remarks>
/// <para>
/// Built on synthetic tables rather than the real 3.86 GB export, because what
/// is being checked is the <em>shape</em> of the sequence — which positions
/// exist, in what order, carrying which streams. Every table here is small and
/// distinct so a row read from the wrong one is obvious.
/// </para>
/// <para>
/// The reference implementation for this pipeline is not a reliable oracle: it
/// builds the prefill three times over, resets its working list twice, and
/// leaves the abandoned attempts in the file with comments like "let me redo
/// this more carefully". So the load-bearing test here is not any single
/// assertion against it, but that taking the reference away reduces this to the
/// design layout — which was validated against a reference that <em>was</em>
/// clean.
/// </para>
/// </remarks>
public sealed class ClonePrefillTests : IDisposable
{
    private const int Hidden = 8;
    private const int Groups = 4;
    private const int CodecVocab = 64;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _open = [];

    public ClonePrefillTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in _open) d.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- The layout ----

    [Fact]
    public void The_sequence_is_laid_out_in_the_order_the_model_expects()
    {
        var rows = Build(
            text: "Hello",
            transcript: "Some words",
            frames: 3,
            language: "english");

        // role 3 | codec prefix 4 | speaker 1 | turn 1 | text N | eos 1
        //   | codec bos 1 | reference frames 3
        var textTokens = Tokens("Some words") + Tokens("Hello");
        Assert.Equal(3 + 4 + 1 + 1 + textTokens + 1 + 1 + 3, rows.Length);
    }

    [Fact]
    public void Every_reference_frame_adds_exactly_one_position()
    {
        // Frames are 12 Hz, so a ten-second clip is 120 of these. If they were
        // ever concatenated rather than summed, the sequence would grow by a
        // multiple of this and the model would read nonsense of the right size.
        var shorter = Build("Hello", "Some words", frames: 3);
        var longer = Build("Hello", "Some words", frames: 9);

        Assert.Equal(6, longer.Length - shorter.Length);
    }

    [Fact]
    public void Without_a_reference_it_is_the_design_layout()
    {
        // The claim this whole file rests on. Design mode was checked against a
        // clean reference implementation; if clone reduces to it exactly, then
        // clone's own reference being a mess matters much less.
        var model = Model();

        var clone = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);
        var design = new DesignPrefill(model.Config, model.Text, model.Codec);

        var cloneRows = clone.Build(
            new CloneRequest("Hello there", ReferenceTranscript: "x", Language: "english"),
            model.Tokenizer,
            new float[Hidden],                 // a speaker embedding of nothing
            [Frame(0)]);

        var designRows = design.Build(
            new DesignRequest("Hello there", Instruction: null, Language: "english"),
            model.Tokenizer);

        // Clone carries two positions design does not: the speaker slot, and the
        // one reference frame. Everything else lines up position for position.
        var refWords = Tokens("x");
        Assert.Equal(designRows.Length + 2 + refWords, cloneRows.Length);

        // The role prefix and codec prefix are identical, and so is the turn.
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(designRows[i], cloneRows[i]);
        }
    }

    [Fact]
    public void The_speaker_sits_between_the_codec_prefix_and_the_turn()
    {
        // Design mode leaves this position empty and says so in a comment. It is
        // where a preset export would put its speaker, and where a clone puts
        // the voice it heard.
        var model = Model();
        var speaker = new float[Hidden];
        Array.Fill(speaker, 7.0f);

        var rows = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups)
            .Build(
                new CloneRequest("Hello", "Some words", "english"),
                model.Tokenizer,
                speaker,
                [Frame(0)]);

        // Row 7 is role(3) + codec prefix(4). Its codec half is the speaker, so
        // subtracting the text padding leaves exactly what was handed in.
        var ttsPad = model.Text.Project(model.Config.TtsPadTokenId);
        for (var i = 0; i < Hidden; i++)
        {
            Assert.Equal(7.0f, rows[7][i] - ttsPad[i], precision: 4);
        }
    }

    [Fact]
    public void The_reference_words_come_before_the_words_to_speak()
    {
        // The example has to be complete before the question is asked. Reversed,
        // the model reads the clip as an answer to the text.
        var model = Model();

        var rows = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups)
            .Build(
                new CloneRequest("AAAA", "BBBB", "english"),
                model.Tokenizer,
                new float[Hidden],
                [Frame(0)]);

        var codecPad = model.Codec.Row(model.Config.CodecPadId);
        var firstText = 3 + 4 + 1 + 1;

        // The first text position is the transcript's first token, not the
        // text's.
        var transcriptFirst = model.Tokenizer.Encode(
            "<|im_start|>assistant\nBBBB<|im_end|>\n")[3];

        var expected = DesignPrefill.Add(model.Text.Project(transcriptFirst), codecPad);
        Assert.Equal(expected, rows[firstText]);
    }

    // ---- Frames ----

    [Fact]
    public void A_frame_is_the_sum_of_one_code_from_every_codebook()
    {
        // Summed, not concatenated: the width never changes however many
        // codebooks there are.
        var model = Model();
        var prefill = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);

        var embedding = prefill.FrameEmbedding([1, 2, 3, 4]);

        Assert.Equal(Hidden, embedding.Length);

        var expected = new float[Hidden];
        var head = model.Codec.Row(1);
        for (var i = 0; i < Hidden; i++) expected[i] = head[i];
        for (var g = 0; g < Groups - 1; g++)
        {
            var row = model.Groups[g].Row(g + 2);
            for (var i = 0; i < Hidden; i++) expected[i] += row[i];
        }

        Assert.Equal(expected, embedding);
    }

    [Fact]
    public void Each_group_is_read_from_its_own_table()
    {
        // The failure this guards is quiet: every table has the same shape, so
        // reading group 3 out of group 2's table returns a real row and a
        // different sound.
        var model = Model();
        var prefill = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);

        // Same code in every group. If one table stood in for another, these
        // would come out equal.
        var same = prefill.FrameEmbedding([5, 5, 5, 5]);
        var shifted = prefill.FrameEmbedding([5, 5, 5, 6]);

        Assert.NotEqual(same, shifted);
    }

    [Fact]
    public void A_frame_of_the_wrong_width_is_refused()
    {
        var model = Model();
        var prefill = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);

        var error = Assert.Throws<ArgumentException>(() => prefill.FrameEmbedding([1, 2, 3]));
        Assert.Contains("codebooks", error.Message);
    }

    // ---- What it refuses ----

    [Fact]
    public void A_blank_transcript_is_refused_rather_than_guessed()
    {
        // §4 calls the transcript effectively mandatory. Without it the model
        // still produces audio — confident, fluent and not the requested words —
        // so a clear refusal beats a plausible result.
        var model = Model();
        var prefill = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);

        var error = Assert.Throws<ArgumentException>(() => prefill.Build(
            new CloneRequest("Hello", ReferenceTranscript: "   "),
            model.Tokenizer,
            new float[Hidden],
            [Frame(0)]));

        Assert.Contains("what the recording says", error.Message);
    }

    [Fact]
    public void A_reference_that_produced_no_codes_is_refused()
    {
        var model = Model();
        var prefill = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);

        var error = Assert.Throws<ArgumentException>(() => prefill.Build(
            new CloneRequest("Hello", "Some words"),
            model.Tokenizer,
            new float[Hidden],
            []));

        Assert.Contains("too short", error.Message);
    }

    [Fact]
    public void A_speaker_embedding_of_the_wrong_width_names_the_mismatch()
    {
        // Two models that do not belong together. The message should say that
        // rather than let a length error surface from somewhere deeper.
        var model = Model();
        var prefill = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);

        var error = Assert.Throws<ArgumentException>(() => prefill.Build(
            new CloneRequest("Hello", "Some words"),
            model.Tokenizer,
            new float[Hidden + 1],
            [Frame(0)]));

        Assert.Contains("does not match the talker", error.Message);
    }

    // ---- Language ----

    [Fact]
    public void A_named_language_opens_a_thinking_span_and_auto_does_not()
    {
        // Same rule as design mode: four positions when the language is named,
        // three when it is left to the model.
        var model = Model();
        var prefill = new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups);

        Assert.Equal(4, prefill.CodecPrefix("english").Count);
        Assert.Equal(3, prefill.CodecPrefix("auto").Count);
        Assert.Equal(3, prefill.CodecPrefix("klingon").Count);
    }

    // ---- Fixtures ----

    /// <summary>How many positions a phrase contributes to the sequence.</summary>
    /// <remarks>
    /// Counted through the tokenizer rather than by hand: the five subtracted
    /// are the role prefix and the two that close the turn, which the sequence
    /// expresses through its own tokens.
    /// </remarks>
    private int Tokens(string words) =>
        Model().Tokenizer.Encode(ChatTurn(words)).Count - 5;

    private static string ChatTurn(string words) =>
        "<|im_start|>assistant\n" + words + "<|im_end|>\n";

    private static int[] Frame(int seed) => [seed % 8, (seed + 1) % 8, (seed + 2) % 8, (seed + 3) % 8];

    private float[][] Build(string text, string transcript, int frames, string language = "auto")
    {
        var model = Model();
        var codes = Enumerable.Range(0, frames).Select(Frame).ToList();

        return new ClonePrefill(model.Config, model.Text, model.Codec, model.Groups)
            .Build(new CloneRequest(text, transcript, language), model.Tokenizer, new float[Hidden], codes);
    }

    private Fixture? _model;

    private Fixture Model() => _model ??= BuildModel();

    private sealed record Fixture(
        QwenConfig Config,
        TextProjection Text,
        NpyArray Codec,
        NpyArray[] Groups,
        QwenTokenizer Tokenizer);

    /// <summary>
    /// A model small enough to reason about, with every table distinct.
    /// </summary>
    /// <remarks>
    /// Every token id here is under 256 — including the special ones, which are
    /// really six figures — so the embedding table is 256 rows rather than
    /// 151,674. The sequence layout does not care what the numbers are, only
    /// that each one reaches its own row.
    /// </remarks>
    private Fixture BuildModel()
    {
        const int Rows = 256;

        var textValues = new float[Rows * Hidden];
        for (var i = 0; i < textValues.Length; i++) textValues[i] = (i % 97) * 0.01f;

        var text = NpyArray.Open(NpyFile.WriteTo(
            Path.Combine(_root, "text_embedding.npy"), textValues, [Rows, Hidden]));
        _open.Add(text);

        var codecValues = new float[CodecVocab * Hidden];
        for (var i = 0; i < codecValues.Length; i++) codecValues[i] = 100f + (i % 89);

        var codec = NpyArray.Open(NpyFile.WriteTo(
            Path.Combine(_root, "codec.npy"), codecValues, [CodecVocab, Hidden]));
        _open.Add(codec);

        var groups = new NpyArray[Groups - 1];
        for (var g = 0; g < groups.Length; g++)
        {
            var values = new float[CodecVocab * Hidden];

            // Each table offset by its own group, so a row read from the wrong
            // one is a different number rather than a coincidence.
            for (var i = 0; i < values.Length; i++) values[i] = ((g + 1) * 1000f) + (i % 71);

            groups[g] = NpyArray.Open(NpyFile.WriteTo(
                Path.Combine(_root, $"group_{g}.npy"), values, [CodecVocab, Hidden]));
            _open.Add(groups[g]);
        }

        // An identity projection: what comes out is the embedding row itself, so
        // an assertion about a position can name the row it should hold.
        var identity = new float[Hidden * Hidden];
        for (var i = 0; i < Hidden; i++) identity[(i * Hidden) + i] = 1f;

        var projection = new TextProjection(
            text, identity, new float[Hidden], identity, new float[Hidden]);

        var config = new QwenConfig
        {
            HiddenSize = Hidden,
            Layers = 2,
            KvHeads = 1,
            HeadDim = 4,
            VocabSize = 128,
            CodeGroups = Groups,
            CodePredictorLayers = 1,
            CodePredictorKvHeads = 1,
            CodePredictorHeadDim = 4,
            CodePredictorVocabSize = CodecVocab,
            TtsPadTokenId = 210,
            TtsBosTokenId = 211,
            TtsEosTokenId = 212,
            CodecEosTokenId = 50,
            CodecPadId = 48,
            CodecBosId = 49,
            CodecThinkId = 54,
            CodecNoThinkId = 55,
            CodecThinkBosId = 56,
            CodecThinkEosId = 57,
            LanguageIds = new Dictionary<string, int> { ["english"] = 40 },
            SpeakerIds = new Dictionary<string, int>(),
            SampleRate = 24_000,
            Sampling = SamplingOptions.Default,
            MaxNewTokens = 100,
        };

        return new Fixture(config, projection, codec, groups, Tokenizer());
    }

    /// <summary>
    /// A byte-level tokenizer over printable ASCII, with the chat specials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every character is its own token, which makes the token count of a phrase
    /// obvious — that is what the layout assertions count.
    /// </para>
    /// <para>
    /// The one exception is <c>assistant</c>, which is built up by merges. It
    /// has to be a single token: the sequence takes the first three positions as
    /// the role prefix, and the real vocabulary spells that word in one. Listing
    /// it in the vocabulary is not enough — byte-level BPE starts from single
    /// characters and only ever combines pairs it has been taught, so without
    /// the merges the word stays nine tokens and every later slice is off by
    /// eight.
    /// </para>
    /// </remarks>
    private static QwenTokenizer Tokenizer()
    {
        var vocabulary = new Dictionary<string, int>();
        var next = 0;

        // The byte-level alphabet for printable ASCII, plus the two characters
        // a space and a newline become.
        for (var c = '!'; c <= '~'; c++) vocabulary[c.ToString()] = next++;
        vocabulary["Ġ"] = next++;   // space
        vocabulary["Ċ"] = next++;   // newline

        // Each prefix of the word, and the merge that reaches it.
        const string Role = "assistant";
        var merges = new List<(string, string)>();
        for (var length = 2; length <= Role.Length; length++)
        {
            vocabulary[Role[..length]] = next++;
            merges.Add((Role[..(length - 1)], Role[length - 1].ToString()));
        }

        return QwenTokenizer.FromParts(
            vocabulary,
            merges,
            new Dictionary<string, int>
            {
                ["<|im_start|>"] = 250,
                ["<|im_end|>"] = 251,
            });
    }
}
