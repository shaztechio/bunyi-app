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

using System.Text.Json;
using Bunyi.Core.Qwen;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The sequence a design generation is primed with (spec §1).
/// </summary>
/// <remarks>
/// Checked against the reference script's own construction, run over the same
/// files. Row counts, exact values at both ends, and a sum over everything —
/// the sum because a port that builds the right number of rows in the wrong
/// order, or pairs the wrong pad with the wrong position, gets the shape right
/// and the content wrong.
/// </remarks>
public class PrefillTests
{
    private sealed record Truth(
        string text, string? instruct, string language,
        int rows, int width,
        int[] codec_prefix,
        double[] first_row_head, double[] last_row_head,
        double[] row_sums, double total_sum);

    private static Dictionary<string, Truth> Cases { get; } =
        JsonSerializer.Deserialize<Dictionary<string, Truth>>(
            File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "prefill-truth.json")))!;

    private static string? Root
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("BUNYI_DESIGN_MODEL")
                ?? @"C:\bs\dm\models\models\wavekat\Qwen3-TTS-1.7B-VoiceDesign-ONNX";

            return File.Exists(Path.Combine(root, "config.json")) ? root : null;
        }
    }

    /// <summary>Everything a build needs, loaded once.</summary>
    private sealed class Model
    {
        public required QwenConfig Config { get; init; }
        public required DesignPrefill Builder { get; init; }
        public required QwenTokenizer Tokenizer { get; init; }
    }

    private static readonly Lazy<Model> Loaded = new(() =>
    {
        var root = Root!;
        var embeddings = Path.Combine(root, "embeddings");

        NpyArray Open(string name) => NpyArray.Open(Path.Combine(embeddings, $"{name}.npy"));

        var config = QwenConfig.Load(Path.Combine(root, "config.json"));

        using var fc1W = Open("text_projection_fc1_weight");
        using var fc1B = Open("text_projection_fc1_bias");
        using var fc2W = Open("text_projection_fc2_weight");
        using var fc2B = Open("text_projection_fc2_bias");

        var projection = new TextProjection(
            Open("text_embedding"),
            fc1W.ToArray(), fc1B.ToArray(), fc2W.ToArray(), fc2B.ToArray());

        return new Model
        {
            Config = config,
            Builder = new DesignPrefill(config, projection, Open("talker_codec_embedding")),
            Tokenizer = QwenTokenizer.Load(Path.Combine(root, "tokenizer")),
        };
    }, isThreadSafe: true);

    private static Model Real()
    {
        Skip.If(Root is null,
            "The 5.85 GB voice-design export is not on this machine. "
            + "Set BUNYI_DESIGN_MODEL to its folder to run these.");

        return Loaded.Value;
    }

    [SkippableTheory]
    [InlineData("english_instruct")]
    [InlineData("auto_language")]
    [InlineData("no_instruct")]
    [InlineData("chinese")]
    public void The_sequence_is_the_length_the_reference_builds(string name)
    {
        var expected = Cases[name];
        var model = Real();

        var rows = model.Builder.Build(
            new DesignRequest(expected.text, expected.instruct, expected.language),
            model.Tokenizer);

        Assert.Equal(expected.rows, rows.Length);
        Assert.All(rows, r => Assert.Equal(expected.width, r.Length));
    }

    [SkippableTheory]
    [InlineData("english_instruct")]
    [InlineData("auto_language")]
    [InlineData("no_instruct")]
    [InlineData("chinese")]
    public void The_sequence_holds_the_values_the_reference_builds(string name)
    {
        // The sum catches what the row count cannot: rows in the wrong order,
        // or a pad added at the wrong position.
        var expected = Cases[name];
        var model = Real();

        var rows = model.Builder.Build(
            new DesignRequest(expected.text, expected.instruct, expected.language),
            model.Tokenizer);

        var total = rows.Sum(r => (double)r.Sum());

        Assert.Equal(expected.total_sum, total, 1);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(expected.first_row_head[i], rows[0][i], 4);
            Assert.Equal(expected.last_row_head[i], rows[^1][i], 4);
        }
    }

    [SkippableTheory]
    [InlineData("english_instruct")]
    [InlineData("auto_language")]
    [InlineData("no_instruct")]
    [InlineData("chinese")]
    public void Every_early_row_matches_position_for_position(string name)
    {
        // Row by row through the part where the streams first combine, which is
        // where a mis-paired pad would hide.
        var expected = Cases[name];
        var model = Real();

        var rows = model.Builder.Build(
            new DesignRequest(expected.text, expected.instruct, expected.language),
            model.Tokenizer);

        for (var i = 0; i < expected.row_sums.Length; i++)
        {
            Assert.Equal(expected.row_sums[i], rows[i].Sum(), 2);
        }
    }

    [SkippableTheory]
    [InlineData("english_instruct")]
    [InlineData("auto_language")]
    [InlineData("chinese")]
    public void A_named_language_opens_a_thinking_span_and_auto_does_not(string name)
    {
        var expected = Cases[name];
        var model = Real();

        Assert.Equal(expected.codec_prefix, model.Builder.CodecPrefix(expected.language));
    }

    [SkippableFact]
    public void An_unknown_language_falls_back_to_letting_the_model_decide()
    {
        // §1 offers ten languages plus auto; anything else — including a name
        // the export does not carry — must behave as auto rather than fail.
        var model = Real();

        var rows = model.Builder.Build(
            new DesignRequest("Hello world", null, "klingon"), model.Tokenizer);

        var asAuto = model.Builder.Build(
            new DesignRequest("Hello world", null, "auto"), model.Tokenizer);

        Assert.Equal(asAuto.Length, rows.Length);
    }

    [SkippableFact]
    public void The_description_makes_the_sequence_longer_and_nothing_else_does()
    {
        // The whole of what design mode adds. Same text, same language: the
        // difference is exactly the description's tokens.
        var model = Real();

        var without = model.Builder.Build(
            new DesignRequest("Hello world", null, "english"), model.Tokenizer);

        var with = model.Builder.Build(
            new DesignRequest("Hello world", "A warm female voice", "english"), model.Tokenizer);

        var described = model.Tokenizer.Encode(
            "<|im_start|>user\nA warm female voice<|im_end|>\n").Count;

        Assert.Equal(without.Length + described, with.Length);
    }

    [SkippableFact]
    public void Text_that_tokenizes_to_nothing_is_refused()
    {
        var model = Real();

        Assert.Throws<ArgumentException>(
            () => model.Builder.Build(new DesignRequest(string.Empty, null), model.Tokenizer));
    }

    // ---- The config, which needs no model on disk ----

    [Fact]
    public void A_flat_config_and_a_nested_one_read_the_same()
    {
        // wavekat keeps these at the top level; elbruno nests them. The graphs
        // are identical, so one pipeline should drive both.
        const string flat = """
            {"talker_hidden_size":2048,"talker_num_layers":28,"talker_num_kv_heads":8,
             "talker_head_dim":128,"talker_vocab_size":3072,"talker_num_code_groups":16,
             "cp_num_layers":5,"cp_num_kv_heads":8,"cp_head_dim":128,"cp_vocab_size":2048,
             "tts_pad_token_id":151671,"tts_bos_token_id":151672,"tts_eos_token_id":151673,
             "codec_eos_token_id":2150,"codec_pad_id":2148,"codec_bos_id":2149,
             "codec_think_id":2154,"codec_nothink_id":2155,
             "codec_think_bos_id":2156,"codec_think_eos_id":2157}
            """;

        const string nested = """
            {"talker":{"hidden_size":2048,"num_hidden_layers":28,"num_key_value_heads":8,
              "head_dim":128,"vocab_size":3072,"num_code_groups":16},
             "code_predictor":{"num_hidden_layers":5,"num_key_value_heads":8,"head_dim":128,
               "vocab_size":2048},
             "tts_pad_token_id":151671,"tts_bos_token_id":151672,"tts_eos_token_id":151673,
             "codec_eos_token_id":2150,"codec_pad_id":2148,"codec_bos_id":2149,
             "codec_think_id":2154,"codec_nothink_id":2155,
             "codec_think_bos_id":2156,"codec_think_eos_id":2157}
            """;

        using var a = JsonDocument.Parse(flat);
        using var b = JsonDocument.Parse(nested);

        var one = QwenConfig.Parse(a.RootElement, "flat");
        var two = QwenConfig.Parse(b.RootElement, "nested");

        // Neither names the chat specials, so neither offers any: the
        // tokenizer's own files are expected to.
        Assert.Null(one.ChatSpecials);
        Assert.Null(two.ChatSpecials);

        Assert.Equal(2048, one.HiddenSize);
        Assert.Equal(one.HiddenSize, two.HiddenSize);
        Assert.Equal(one.Layers, two.Layers);
        Assert.Equal(one.CodePredictorLayers, two.CodePredictorLayers);
    }

    [Fact]
    public void A_missing_number_is_an_error_rather_than_a_guess()
    {
        // A defaulted token id is a guess about the model's own vocabulary, and
        // a wrong one is a real token — so the model speaks rather than
        // complains.
        using var document = JsonDocument.Parse("""{"talker_hidden_size":2048}""");

        var error = Assert.Throws<InvalidDataException>(
            () => QwenConfig.Parse(document.RootElement, "partial.json"));

        Assert.Contains("partial.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preset_exports_spelling_is_read_too()
    {
        // elbruno's embeddings/config.json nests the text-side ids under "tts",
        // the codec-side ones under "talker", spells the language map
        // "language_ids", and keeps its speakers in a separate file. One parser
        // reads both exports, or one pipeline could not drive both.
        const string preset = """
            {"tts":{"tts_bos_token_id":151672,"tts_eos_token_id":151673,"tts_pad_token_id":151671,
                    "im_start_token_id":151644,"im_end_token_id":151645},
             "language_ids":{"english":2050,"chinese":2055},
             "talker":{"hidden_size":1024,"text_hidden_size":2048,"vocab_size":3072,
               "num_hidden_layers":28,"num_key_value_heads":8,"head_dim":128,"num_code_groups":16,
               "codec_eos_token_id":2150,"codec_think_id":2154,"codec_nothink_id":2155,
               "codec_think_bos_id":2156,"codec_think_eos_id":2157,"codec_pad_id":2148,"codec_bos_id":2149},
             "code_predictor":{"hidden_size":1024,"vocab_size":2048,"num_hidden_layers":5,
               "num_key_value_heads":8,"head_dim":128}}
            """;

        using var document = JsonDocument.Parse(preset);
        var config = QwenConfig.Parse(document.RootElement, "preset");

        Assert.Equal(1024, config.HiddenSize);
        Assert.Equal(151671, config.TtsPadTokenId);
        Assert.Equal(2150, config.CodecEosTokenId);
        Assert.Equal(2149, config.CodecBosId);
        Assert.Equal(2050, config.LanguageIds["english"]);
        Assert.Equal(24_000, config.SampleRate);
        // The chat specials, which this export's tokenizer files do not carry.
        Assert.Equal(151644, config.ChatSpecials!["<|im_start|>"]);
        Assert.Equal(151645, config.ChatSpecials["<|im_end|>"]);
        // No generate_config: the same defaults the previous pipeline used.
        Assert.Equal(0.9f, config.Sampling.Temperature);
        Assert.Equal(50, config.Sampling.TopK);
    }

    [Fact]
    public void Speakers_can_come_from_a_file_beside_the_config()
    {
        var root = Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "config.json");
            File.WriteAllText(config, """
                {"talker_hidden_size":1024,"talker_num_layers":28,"talker_num_kv_heads":8,
                 "talker_head_dim":128,"talker_vocab_size":3072,"talker_num_code_groups":16,
                 "cp_num_layers":5,"cp_num_kv_heads":8,"cp_head_dim":128,"cp_vocab_size":2048,
                 "tts_pad_token_id":151671,"tts_bos_token_id":151672,"tts_eos_token_id":151673,
                 "codec_eos_token_id":2150,"codec_pad_id":2148,"codec_bos_id":2149,
                 "codec_think_id":2154,"codec_nothink_id":2155,
                 "codec_think_bos_id":2156,"codec_think_eos_id":2157}
                """);
            var speakers = Path.Combine(root, "speaker_ids.json");
            File.WriteAllText(speakers, """{"serena": 3066, "ryan": 3061}""");

            var without = QwenConfig.Load(config);
            var with = QwenConfig.Load(config, speakers);

            Assert.True(without.IsVoiceDesign);
            Assert.False(with.IsVoiceDesign);
            // In the file's own order, which is the order a picker shows them.
            Assert.Equal(["serena", "ryan"], with.SpeakerIds.Keys);
            Assert.Equal(3061, with.SpeakerIds["RYAN"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public void The_real_config_says_it_designs_voices_rather_than_picking_them()
    {
        Skip.If(Root is null, "The voice-design export is not on this machine.");

        var config = QwenConfig.Load(Path.Combine(Root!, "config.json"));

        Assert.True(config.IsVoiceDesign);
        Assert.Empty(config.SpeakerIds);
        Assert.Equal(2048, config.HiddenSize);
        Assert.Equal(16, config.CodeGroups);
        Assert.Equal(24_000, config.SampleRate);
    }

    [SkippableFact]
    public void The_real_config_offers_exactly_the_languages_the_spec_does()
    {
        // §1 pins ten. The preset export also carries two dialects the spec
        // does not offer; this one must not quietly differ either way.
        Skip.If(Root is null, "The voice-design export is not on this machine.");

        var config = QwenConfig.Load(Path.Combine(Root!, "config.json"));

        Assert.Equal(
            Languages.All.Where(l => l != "auto").Order(),
            config.LanguageIds.Keys.Order());
    }

    [SkippableFact]
    public void The_sampling_defaults_come_from_the_export()
    {
        Skip.If(Root is null, "The voice-design export is not on this machine.");

        var config = QwenConfig.Load(Path.Combine(Root!, "config.json"));

        Assert.Equal(0.9f, config.Sampling.Temperature);
        Assert.Equal(50, config.Sampling.TopK);
        Assert.Equal(1.05f, config.Sampling.RepetitionPenalty);
        Assert.Equal(8192, config.MaxNewTokens);
    }
}
