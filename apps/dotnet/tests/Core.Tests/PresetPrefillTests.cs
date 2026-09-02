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

using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The sequence preset voice primes the talker with (spec §1).
/// </summary>
/// <remarks>
/// <para>
/// The claim under test is the one <see cref="PresetPrefill"/> is built on:
/// preset voice is the design layout plus exactly one row, the speaker's row of
/// the codec table, in the slot design mode leaves empty. Design mode was checked
/// against the export's own reference to one part in 10^8; if preset reduces to
/// it with one row inserted, that check carries over.
/// </para>
/// <para>
/// The fixture is the same shape as <see cref="ClonePrefillTests"/>' — a model
/// small enough to reason about, with an identity projection so an assertion
/// about a position can name the row it should hold.
/// </para>
/// </remarks>
public sealed class PresetPrefillTests : IDisposable
{
    private const int Hidden = 8;
    private const int CodecVocab = 64;
    private const int Ryan = 60;
    private const int Serena = 61;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly List<IDisposable> _open = [];

    public PresetPrefillTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in _open) d.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("english")]
    public void It_is_the_design_layout_with_the_speaker_row_inserted(string language)
    {
        var model = Model();
        var preset = new PresetPrefill(model.Config, model.Text, model.Codec);
        var design = new DesignPrefill(model.Config, model.Text, model.Codec);

        var presetRows = preset.Build(new PresetRequest("Hello there", "ryan", null, language), model.Tokenizer);
        var designRows = design.Build(new DesignRequest("Hello there", null, language), model.Tokenizer);

        // One more row, and only one.
        Assert.Equal(designRows.Length + 1, presetRows.Length);

        // The slot: after the role prefix and the codec prefix, before the turn
        // opens. Three positions of role, then three or four of codec prefix.
        var slot = 3 + design.CodecPrefix(language).Count;

        for (var i = 0; i < slot; i++) Assert.Equal(designRows[i], presetRows[i]);

        // The row itself is text padding against the speaker's codec row —
        // the same pairing every codec-prefix position uses.
        var expected = DesignPrefill.Add(model.Text.Project(model.Config.TtsPadTokenId), model.Codec.Row(Ryan));
        Assert.Equal(expected, presetRows[slot]);

        for (var i = slot; i < designRows.Length; i++) Assert.Equal(designRows[i], presetRows[i + 1]);
    }

    [Fact]
    public void Each_speaker_is_a_different_row()
    {
        var model = Model();
        var preset = new PresetPrefill(model.Config, model.Text, model.Codec);

        var ryan = preset.Build(new PresetRequest("Hello", "ryan"), model.Tokenizer);
        var serena = preset.Build(new PresetRequest("Hello", "serena"), model.Tokenizer);

        Assert.Equal(ryan.Length, serena.Length);

        // Exactly one position differs, and it is the speaker slot.
        var differing = Enumerable.Range(0, ryan.Length).Where(i => !ryan[i].SequenceEqual(serena[i])).ToList();
        Assert.Equal([6], differing);
    }

    [Fact]
    public void The_style_instruction_goes_in_front_as_the_description_does()
    {
        // #104's question, answered structurally: the instruction is text
        // conditioning ahead of the role, the rows design mode builds for its
        // description. Nothing about the speaker row moves.
        var model = Model();
        var preset = new PresetPrefill(model.Config, model.Text, model.Codec);

        var plain = preset.Build(new PresetRequest("Hello", "ryan"), model.Tokenizer);
        var styled = preset.Build(new PresetRequest("Hello", "ryan", "Whisper"), model.Tokenizer);

        var instruction = model.Tokenizer.Encode("<|im_start|>user\nWhisper<|im_end|>\n").Count;
        Assert.Equal(plain.Length + instruction, styled.Length);

        for (var i = 0; i < plain.Length; i++) Assert.Equal(plain[i], styled[i + instruction]);
    }

    [Fact]
    public void Names_are_matched_without_regard_to_case()
    {
        // The ids file spells them lower-case and the window shows them
        // capitalised; both must reach the same row.
        var model = Model();
        var preset = new PresetPrefill(model.Config, model.Text, model.Codec);

        Assert.Equal(Ryan, preset.SpeakerId("Ryan"));
        Assert.Equal(Ryan, preset.SpeakerId("RYAN "));
    }

    [Fact]
    public void An_unknown_speaker_is_refused_and_the_alternatives_named()
    {
        // Not a fallback to some other voice: a wrong voice is not a degraded
        // result, it is a different one.
        var model = Model();
        var preset = new PresetPrefill(model.Config, model.Text, model.Codec);

        var error = Assert.Throws<ArgumentException>(
            () => preset.Build(new PresetRequest("Hello", "vivian"), model.Tokenizer));

        Assert.Contains("vivian", error.Message, StringComparison.Ordinal);
        Assert.Contains("ryan", error.Message, StringComparison.Ordinal);
        Assert.Contains("serena", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_speaker_beyond_the_table_is_a_broken_export_not_a_crash()
    {
        var model = Model();
        var config = model.Config with
        {
            SpeakerIds = new Dictionary<string, int> { ["ghost"] = CodecVocab + 5 },
        };
        var preset = new PresetPrefill(config, model.Text, model.Codec);

        var error = Assert.Throws<InvalidDataException>(() => preset.SpeakerId("ghost"));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_export_with_no_speakers_cannot_be_a_preset_voice()
    {
        var model = Model();
        var config = model.Config with { SpeakerIds = new Dictionary<string, int>() };

        Assert.Throws<ArgumentException>(() => new PresetPrefill(config, model.Text, model.Codec));
    }

    [Fact]
    public void The_speakers_are_offered_in_the_exports_own_order()
    {
        var model = Model();
        var preset = new PresetPrefill(model.Config, model.Text, model.Codec);

        Assert.Equal(["ryan", "serena"], preset.Speakers);
    }

    // ---- The fixture ----

    private Fixture? _model;

    private Fixture Model() => _model ??= BuildModel();

    private sealed record Fixture(
        QwenConfig Config,
        TextProjection Text,
        NpyArray Codec,
        QwenTokenizer Tokenizer);

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
            CodeGroups = 4,
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
            SpeakerIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["ryan"] = Ryan,
                ["serena"] = Serena,
            },
            SampleRate = 24_000,
            Sampling = SamplingOptions.Default,
            MaxNewTokens = 100,
        };

        return new Fixture(config, projection, codec, Tokenizer());
    }

    /// <summary>A byte-level tokenizer over printable ASCII, with the chat specials.</summary>
    private static QwenTokenizer Tokenizer()
    {
        var vocabulary = new Dictionary<string, int>();
        var next = 0;

        for (var c = '!'; c <= '~'; c++) vocabulary[c.ToString()] = next++;
        vocabulary["Ġ"] = next++;   // space
        vocabulary["Ċ"] = next++;   // newline

        // "assistant" must be one token: the role prefix is three positions.
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
