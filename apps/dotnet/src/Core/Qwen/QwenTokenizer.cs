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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bunyi.Core.Qwen;

/// <summary>
/// Qwen2 byte-level BPE, as the exports ship it.
/// </summary>
/// <remarks>
/// <para>
/// Written here rather than taken from a package because the packaged option
/// cannot do it. <c>Microsoft.ML.Tokenizers</c> was measured against
/// HuggingFace's own tokenizer on this export's files: digits and most text
/// agree, but <b>special tokens are not recognised at all</b> —
/// <c>&lt;|im_start|&gt;</c> comes back as seven tokens of literal punctuation —
/// and runs of spaces and newlines split differently. Its
/// <c>CodeGenTokenizer</c> has no public constructor that accepts special
/// tokens, so there is no way to register them.
/// </para>
/// <para>
/// The chat template is nothing but special tokens, so that alone settles it.
/// The whitespace differences matter too: text with a double space after a full
/// stop, or a blank line between paragraphs, is entirely ordinary in something
/// being read aloud, and tokenizing it differently from the model's training
/// means embeddings the model was never trained on.
/// </para>
/// <para>
/// Every rule here is pinned against ids taken from HuggingFace's tokenizer on
/// the real files. That is the only definition of correct: the model was trained
/// against those ids and nothing else.
/// </para>
/// </remarks>
public sealed partial class QwenTokenizer
{
    /// <summary>
    /// How Qwen2 cuts text up before merging.
    /// </summary>
    /// <remarks>
    /// From <c>tokenizer.json</c>, unchanged. It differs from GPT-2's in ways
    /// that matter: <c>\p{N}</c> takes <b>one</b> digit at a time, so 123 is
    /// three tokens; <c>\s*[\r\n]+</c> keeps a run of newlines together with the
    /// space before it; and <c>\s+(?!\S)</c> takes trailing whitespace only at
    /// the end of the text.
    /// </remarks>
    internal const string SplitPattern =
        @"(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+";

    [GeneratedRegex(SplitPattern, RegexOptions.Compiled)]
    private static partial Regex Splitter();

    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<(string Left, string Right), int> _ranks;
    private readonly Dictionary<string, int> _specials;
    private readonly Regex? _specialSplitter;

    /// <summary>Tokens the vocabulary knows, including the special ones.</summary>
    public int Count => _vocabulary.Count + _specials.Count;

    private QwenTokenizer(
        Dictionary<string, int> vocabulary,
        Dictionary<(string, string), int> ranks,
        Dictionary<string, int> specials)
    {
        _vocabulary = vocabulary;
        _ranks = ranks;
        _specials = specials;

        if (specials.Count > 0)
        {
            // Longest first, so <|im_start|> is never matched as a shorter
            // token that happens to be a prefix of it.
            var alternatives = specials.Keys
                .OrderByDescending(s => s.Length)
                .Select(Regex.Escape);

            _specialSplitter = new Regex($"({string.Join('|', alternatives)})", RegexOptions.Compiled);
        }
    }

    /// <summary>Loads a tokenizer from an export's <c>tokenizer/</c> folder.</summary>
    /// <remarks>
    /// <c>vocab.json</c> and <c>merges.txt</c> carry the base vocabulary;
    /// <c>added_tokens.json</c> the special ones. When the last is missing the
    /// specials are taken from <c>tokenizer.json</c>, which every export ships.
    /// </remarks>
    public static QwenTokenizer Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var vocabulary = ReadVocabulary(Path.Combine(folder, "vocab.json"));
        var ranks = ReadMerges(Path.Combine(folder, "merges.txt"));
        var specials = ReadSpecials(folder);

        return new QwenTokenizer(vocabulary, ranks, specials);
    }

    /// <summary>Builds one directly, for tests.</summary>
    internal static QwenTokenizer FromParts(
        Dictionary<string, int> vocabulary,
        IEnumerable<(string Left, string Right)> merges,
        Dictionary<string, int>? specials = null)
    {
        var ranks = new Dictionary<(string, string), int>();
        var rank = 0;
        foreach (var pair in merges) ranks.TryAdd(pair, rank++);

        return new QwenTokenizer(vocabulary, ranks, specials ?? []);
    }

    private static Dictionary<string, int> ReadVocabulary(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
               ?? throw new InvalidDataException($"{path} is empty.");
    }

    private static Dictionary<(string, string), int> ReadMerges(string path)
    {
        var ranks = new Dictionary<(string, string), int>();
        var rank = 0;

        foreach (var line in File.ReadLines(path))
        {
            // The first line is a version comment in every file that has one.
            if (line.Length == 0 || line.StartsWith("#version", StringComparison.Ordinal)) continue;

            var space = line.IndexOf(' ');
            if (space <= 0 || space == line.Length - 1) continue;

            // TryAdd, not Add: the first rank wins, and a duplicate later in the
            // file must not overwrite it with a worse one.
            ranks.TryAdd((line[..space], line[(space + 1)..]), rank++);
        }

        return ranks;
    }

    private static Dictionary<string, int> ReadSpecials(string folder)
    {
        var added = Path.Combine(folder, "added_tokens.json");
        if (File.Exists(added))
        {
            using var stream = File.OpenRead(added);
            return JsonSerializer.Deserialize<Dictionary<string, int>>(stream) ?? [];
        }

        var full = Path.Combine(folder, "tokenizer.json");
        if (!File.Exists(full)) return [];

        using var document = JsonDocument.Parse(File.ReadAllBytes(full));
        if (!document.RootElement.TryGetProperty("added_tokens", out var tokens)) return [];

        var specials = new Dictionary<string, int>();
        foreach (var token in tokens.EnumerateArray())
        {
            if (token.TryGetProperty("content", out var content) &&
                token.TryGetProperty("id", out var id))
            {
                specials[content.GetString()!] = id.GetInt32();
            }
        }

        return specials;
    }

    /// <summary>Turns text into token ids.</summary>
    public IReadOnlyList<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var ids = new List<int>();
        if (text.Length == 0) return ids;

        // NFC first, matching the export's normalizer: an accent written as a
        // combining mark must become the composed character the vocabulary has,
        // or it tokenizes as two unrelated pieces.
        text = text.IsNormalized(NormalizationForm.FormC)
            ? text
            : text.Normalize(NormalizationForm.FormC);

        if (_specialSplitter is null)
        {
            EncodeOrdinary(text, ids);
            return ids;
        }

        // Split on the special tokens and keep them: they are emitted as
        // themselves, and the text between them is tokenized normally.
        foreach (var part in _specialSplitter.Split(text))
        {
            if (part.Length == 0) continue;

            if (_specials.TryGetValue(part, out var special)) ids.Add(special);
            else EncodeOrdinary(part, ids);
        }

        return ids;
    }

    private void EncodeOrdinary(string text, List<int> ids)
    {
        foreach (var piece in Splitter().EnumerateMatches(text))
        {
            var span = text.AsSpan(piece.Index, piece.Length);
            var mapped = ByteLevel.Encode(span);

            foreach (var token in Merge(mapped))
            {
                if (_vocabulary.TryGetValue(token, out var id)) ids.Add(id);
                else throw new InvalidDataException(
                    $"The vocabulary has no token '{token}'. "
                    + "The tokenizer folder does not match this model.");
            }
        }
    }

    /// <summary>
    /// Merges a byte-level string into vocabulary tokens.
    /// </summary>
    /// <remarks>
    /// The standard BPE loop: repeatedly join the adjacent pair with the lowest
    /// rank until no pair is known. Lowest, not first — the rank is the order the
    /// merges were learned in, and taking them out of order gives a different
    /// and wrong split.
    /// </remarks>
    internal List<string> Merge(string mapped)
    {
        var parts = new List<string>(mapped.Length);
        foreach (var rune in mapped.EnumerateRunes()) parts.Add(rune.ToString());

        if (parts.Count < 2) return parts;

        while (true)
        {
            var best = int.MaxValue;
            var at = -1;

            for (var i = 0; i < parts.Count - 1; i++)
            {
                if (_ranks.TryGetValue((parts[i], parts[i + 1]), out var rank) && rank < best)
                {
                    best = rank;
                    at = i;
                }
            }

            if (at < 0) break;

            parts[at] += parts[at + 1];
            parts.RemoveAt(at + 1);
        }

        return parts;
    }
}

/// <summary>
/// The byte-to-character mapping byte-level BPE is built on.
/// </summary>
/// <remarks>
/// GPT-2's <c>bytes_to_unicode</c>, which every byte-level BPE vocabulary since
/// has used. Its purpose is that every one of the 256 byte values becomes a
/// printable character with no whitespace among them, so a BPE vocabulary can be
/// stored as ordinary JSON text — which is why <c>vocab.json</c> is full of
/// <c>Ġ</c> where a space belongs.
/// </remarks>
internal static class ByteLevel
{
    private static readonly char[] Map = Build();

    private static char[] Build()
    {
        var map = new char[256];
        var taken = new bool[256];
        var next = 256;

        // The printable ranges keep their own character; everything else is
        // moved above 256, in byte order.
        void Keep(int from, int to)
        {
            for (var b = from; b <= to; b++)
            {
                map[b] = (char)b;
                taken[b] = true;
            }
        }

        Keep('!', '~');
        Keep(0xA1, 0xAC);
        Keep(0xAE, 0xFF);

        for (var b = 0; b < 256; b++)
        {
            if (!taken[b]) map[b] = (char)next++;
        }

        return map;
    }

    /// <summary>Maps text's UTF-8 bytes to the vocabulary's character set.</summary>
    public static string Encode(ReadOnlySpan<char> text)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var bytes = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(text, bytes);

        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes) builder.Append(Map[b]);
        return builder.ToString();
    }

    /// <summary>The character one byte maps to.</summary>
    public static char Of(byte value) => Map[value];
}
