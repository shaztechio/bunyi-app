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
using Bunyi.Core.Design;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Qwen2 byte-level BPE (spec §1, design mode).
/// </summary>
/// <remarks>
/// <para>
/// Correct means "the ids HuggingFace produces", because the model was trained
/// against those and nothing else. Every case here carries ids taken from
/// HuggingFace's tokenizer on this export's own files, kept in
/// <c>Fixtures/qwen-tokenizer-truth.json</c>.
/// </para>
/// <para>
/// The cases needing the real vocabulary skip when the export is absent; the
/// rules themselves are pinned separately against a tiny hand-built vocabulary
/// that runs everywhere.
/// </para>
/// </remarks>
public class QwenTokenizerTests
{
    private sealed record Case(string text, int[] ids);

    private static Dictionary<string, Case> Truth { get; } =
        JsonSerializer.Deserialize<Dictionary<string, Case>>(
            File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "qwen-tokenizer-truth.json")))!;

    private static string? Folder
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("BUNYI_DESIGN_MODEL")
                ?? @"C:\bs\dm\models\models\wavekat\Qwen3-TTS-1.7B-VoiceDesign-ONNX";

            var folder = Path.Combine(root, "tokenizer");
            return File.Exists(Path.Combine(folder, "vocab.json")) ? folder : null;
        }
    }

    /// <summary>
    /// The real tokenizer, loaded once.
    /// </summary>
    /// <remarks>
    /// Lazy rather than per-test: reading 151,643 vocabulary entries and
    /// 151,387 merges is not free, and thirty-four theory cases would each pay
    /// for it.
    /// </remarks>
    private static readonly Lazy<QwenTokenizer> Loaded =
        new(() => QwenTokenizer.Load(Folder!), isThreadSafe: true);

    private static QwenTokenizer Real()
    {
        Skip.If(Folder is null,
            "The voice-design export's tokenizer is not on this machine. "
            + "Set BUNYI_DESIGN_MODEL to its folder to run these.");

        return Loaded.Value;
    }

    // ---- Against HuggingFace, on the real files ----

    [SkippableTheory]
    [InlineData("empty")]
    [InlineData("plain")]
    [InlineData("sentence")]
    [InlineData("leading_space")]
    [InlineData("trailing_space")]
    [InlineData("two_spaces")]
    [InlineData("many_spaces")]
    [InlineData("tab")]
    [InlineData("newline")]
    [InlineData("blank_line")]
    [InlineData("crlf")]
    [InlineData("numbers")]
    [InlineData("decimal")]
    [InlineData("contraction")]
    [InlineData("caps_contraction")]
    [InlineData("punct_run")]
    [InlineData("chinese")]
    [InlineData("japanese")]
    [InlineData("korean")]
    [InlineData("emoji")]
    [InlineData("accents")]
    [InlineData("mixed")]
    [InlineData("im_start")]
    [InlineData("im_end")]
    [InlineData("endoftext")]
    [InlineData("chat_full")]
    [InlineData("instruct_full")]
    [InlineData("special_in_text")]
    [InlineData("tts_special")]
    [InlineData("nearly_special")]
    [InlineData("long")]
    [InlineData("url")]
    [InlineData("quotes")]
    [InlineData("nfc")]
    public void Matches_what_HuggingFace_produces(string name)
    {
        var expected = Truth[name];
        var tokenizer = Real();

        Assert.Equal(expected.ids, tokenizer.Encode(expected.text));
    }

    [SkippableFact]
    public void The_chat_template_the_pipeline_builds_tokenizes_exactly()
    {
        // The sequence the reference script constructs, and the one every design
        // generation starts from. Its first three tokens are sliced off as the
        // role prefix and its last five discarded, so the count matters as much
        // as the ids.
        var expected = Truth["chat_full"];
        var ids = Real().Encode(expected.text);

        Assert.Equal(expected.ids, ids);
        Assert.Equal([151_644, 77_091, 198], ids.Take(3));
    }

    [SkippableFact]
    public void The_whole_vocabulary_is_loaded()
    {
        // 151,643 base tokens plus 33 added ones.
        Assert.Equal(151_676, Real().Count);
    }

    // ---- The rules, on a vocabulary small enough to reason about ----

    /// <summary>
    /// A toy byte-level vocabulary: the letters used below, plus two merges.
    /// </summary>
    private static QwenTokenizer Toy()
    {
        var vocabulary = new Dictionary<string, int>();
        var next = 0;

        foreach (var token in new[] { "a", "b", "c", "ab", "abc", "Ġ", "Ġa", "1", "2" })
        {
            vocabulary[token] = next++;
        }

        return QwenTokenizer.FromParts(
            vocabulary,
            [("a", "b"), ("ab", "c"), ("Ġ", "a")],
            new Dictionary<string, int> { ["<|special|>"] = 900 });
    }

    [Fact]
    public void Merges_are_applied_in_rank_order_not_left_to_right()
    {
        // "abc" merges a+b first because that pair was learned first, then
        // ab+c. Taking pairs left to right in a different order gives a
        // different, wrong split.
        Assert.Equal(["abc"], Toy().Merge("abc"));
    }

    [Fact]
    public void A_string_with_no_known_pair_stays_apart()
    {
        Assert.Equal(["c", "b"], Toy().Merge("cb"));
    }

    [Fact]
    public void A_single_character_needs_no_merging()
    {
        Assert.Equal(["a"], Toy().Merge("a"));
    }

    [Fact]
    public void A_special_token_is_one_id_rather_than_its_letters()
    {
        Assert.Equal([900], Toy().Encode("<|special|>"));
    }

    [Fact]
    public void Text_around_a_special_token_is_tokenized_normally()
    {
        var ids = Toy().Encode("ab<|special|>c");

        Assert.Equal([3, 900, 2], ids);
    }

    [Fact]
    public void Empty_text_gives_no_tokens()
    {
        Assert.Empty(Toy().Encode(string.Empty));
    }

    [Fact]
    public void A_token_the_vocabulary_lacks_is_an_error_rather_than_silence()
    {
        // Dropping it would shorten the sequence and shift everything after it,
        // which the model reads as different words rather than as missing ones.
        var error = Assert.Throws<InvalidDataException>(() => Toy().Encode("z"));

        Assert.Contains("does not match this model", error.Message, StringComparison.Ordinal);
    }

    // ---- The byte mapping ----

    [Fact]
    public void A_space_maps_to_the_character_the_vocabulary_spells_it_with()
    {
        // Every byte-level vocabulary writes a space as U+0120. A vocab.json
        // full of "Ġ" is the visible evidence of this mapping.
        Assert.Equal('\u0120', ByteLevel.Of((byte)' '));
    }

    [Fact]
    public void Printable_ASCII_maps_to_itself()
    {
        Assert.Equal('a', ByteLevel.Of((byte)'a'));
        Assert.Equal('~', ByteLevel.Of((byte)'~'));
        Assert.Equal('!', ByteLevel.Of((byte)'!'));
    }

    [Fact]
    public void Control_bytes_move_above_the_printable_range()
    {
        // The point of the mapping: no byte becomes whitespace or a control
        // character, so the vocabulary can live in a JSON file.
        for (var b = 0; b < 256; b++)
        {
            var mapped = ByteLevel.Of((byte)b);
            Assert.False(char.IsWhiteSpace(mapped), $"byte {b} mapped to whitespace");
            Assert.False(char.IsControl(mapped), $"byte {b} mapped to a control character");
        }
    }

    [Fact]
    public void Every_byte_maps_somewhere_different()
    {
        var seen = new HashSet<char>();

        for (var b = 0; b < 256; b++)
        {
            Assert.True(seen.Add(ByteLevel.Of((byte)b)), $"byte {b} collides with an earlier one");
        }
    }

    [Fact]
    public void Multi_byte_characters_map_one_character_per_byte()
    {
        // A pound sign is two bytes in UTF-8, so it maps to two characters —
        // which is how a byte-level vocabulary can spell anything at all.
        Assert.Equal(2, ByteLevel.Encode("£").Length);
        Assert.Equal(4, ByteLevel.Encode("👋").Length);
    }
}
