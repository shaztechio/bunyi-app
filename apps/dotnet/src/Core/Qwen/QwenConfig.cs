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

namespace Bunyi.Core.Qwen;

/// <summary>
/// The numbers an export publishes about itself.
/// </summary>
/// <remarks>
/// <para>
/// Read rather than assumed. Every one of these differs, or could differ,
/// between the exports: the design model is twice the width of the preset one,
/// and the token ids are the model's own vocabulary rather than anything
/// standard. A pipeline with these baked in works until the day it does not,
/// and the failure is silent — a wrong pad id is a real token, so the model
/// speaks something rather than complaining.
/// </para>
/// <para>
/// The wavekat exports keep these flat at the top level
/// (<c>talker_hidden_size</c>); elbruno's nests them under a <c>talker</c>
/// object in <c>embeddings/config.json</c>. Both spellings are read, because
/// the graphs are otherwise identical and one pipeline should drive both.
/// </para>
/// </remarks>
public sealed record QwenConfig
{
    /// <summary>The talker's hidden width — 1024 at 0.6B, 2048 at 1.7B.</summary>
    public required int HiddenSize { get; init; }

    /// <summary>Talker layers, which is how many KV pairs prefill returns.</summary>
    public required int Layers { get; init; }

    /// <summary>Talker key/value heads.</summary>
    public required int KvHeads { get; init; }

    /// <summary>Size of one attention head.</summary>
    public required int HeadDim { get; init; }

    /// <summary>How many tokens the talker can emit.</summary>
    public required int VocabSize { get; init; }

    /// <summary>Codebooks per frame — 16, of which the talker emits the first.</summary>
    public required int CodeGroups { get; init; }

    /// <summary>Code-predictor layers.</summary>
    public required int CodePredictorLayers { get; init; }

    /// <summary>Code-predictor key/value heads.</summary>
    public required int CodePredictorKvHeads { get; init; }

    /// <summary>Code-predictor head size.</summary>
    public required int CodePredictorHeadDim { get; init; }

    /// <summary>How many codes each of the later codebooks can hold.</summary>
    public required int CodePredictorVocabSize { get; init; }

    /// <summary>Padding in the text stream.</summary>
    public required int TtsPadTokenId { get; init; }

    /// <summary>Start of the text stream.</summary>
    public required int TtsBosTokenId { get; init; }

    /// <summary>End of the text stream.</summary>
    public required int TtsEosTokenId { get; init; }

    /// <summary>The token that ends generation.</summary>
    public required int CodecEosTokenId { get; init; }

    /// <summary>Padding in the codec stream.</summary>
    public required int CodecPadId { get; init; }

    /// <summary>Start of the codec stream.</summary>
    public required int CodecBosId { get; init; }

    /// <summary>Opens the reasoning span, when a language is named.</summary>
    public required int CodecThinkId { get; init; }

    /// <summary>Replaces <see cref="CodecThinkId" /> when no language is named.</summary>
    public required int CodecNoThinkId { get; init; }

    /// <summary>Opens the reasoning span.</summary>
    public required int CodecThinkBosId { get; init; }

    /// <summary>Closes the reasoning span.</summary>
    public required int CodecThinkEosId { get; init; }

    /// <summary>Codec token per language name, lower-cased.</summary>
    public required IReadOnlyDictionary<string, int> LanguageIds { get; init; }

    /// <summary>Named speakers, empty for a voice-design export.</summary>
    public required IReadOnlyDictionary<string, int> SpeakerIds { get; init; }

    /// <summary>Output rate, which spec §2 requires to be 24 kHz.</summary>
    public required int SampleRate { get; init; }

    /// <summary>The export's own sampling defaults.</summary>
    public required SamplingOptions Sampling { get; init; }

    /// <summary>The cap on frames, beyond which generation stops.</summary>
    public required int MaxNewTokens { get; init; }

    /// <summary>Whether this export designs a voice from a description.</summary>
    /// <remarks>
    /// Taken from the speaker list being empty rather than from a name: an
    /// export with no speakers cannot offer a speaker picker whatever it calls
    /// itself, and one with speakers is a preset-voice model however it is
    /// labelled.
    /// </remarks>
    public bool IsVoiceDesign => SpeakerIds.Count == 0;

    /// <summary>Reads an export's <c>config.json</c>.</summary>
    public static QwenConfig Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return Parse(document.RootElement, path);
    }

    /// <summary>Reads a config from parsed JSON.</summary>
    internal static QwenConfig Parse(JsonElement root, string source)
    {
        var generate = root.TryGetProperty("generate_config", out var g) ? g : default;

        return new QwenConfig
        {
            HiddenSize = Int(root, source, "talker_hidden_size", "talker", "hidden_size"),
            Layers = Int(root, source, "talker_num_layers", "talker", "num_hidden_layers"),
            KvHeads = Int(root, source, "talker_num_kv_heads", "talker", "num_key_value_heads"),
            HeadDim = Int(root, source, "talker_head_dim", "talker", "head_dim"),
            VocabSize = Int(root, source, "talker_vocab_size", "talker", "vocab_size"),
            CodeGroups = Int(root, source, "talker_num_code_groups", "talker", "num_code_groups"),

            CodePredictorLayers =
                Int(root, source, "cp_num_layers", "code_predictor", "num_hidden_layers"),
            CodePredictorKvHeads =
                Int(root, source, "cp_num_kv_heads", "code_predictor", "num_key_value_heads"),
            CodePredictorHeadDim =
                Int(root, source, "cp_head_dim", "code_predictor", "head_dim"),
            CodePredictorVocabSize =
                Int(root, source, "cp_vocab_size", "code_predictor", "vocab_size"),

            TtsPadTokenId = Int(root, source, "tts_pad_token_id"),
            TtsBosTokenId = Int(root, source, "tts_bos_token_id"),
            TtsEosTokenId = Int(root, source, "tts_eos_token_id"),

            CodecEosTokenId = Int(root, source, "codec_eos_token_id"),
            CodecPadId = Int(root, source, "codec_pad_id"),
            CodecBosId = Int(root, source, "codec_bos_id"),
            CodecThinkId = Int(root, source, "codec_think_id"),
            CodecNoThinkId = Int(root, source, "codec_nothink_id"),
            CodecThinkBosId = Int(root, source, "codec_think_bos_id"),
            CodecThinkEosId = Int(root, source, "codec_think_eos_id"),

            LanguageIds = Map(root, "codec_language_id"),
            SpeakerIds = Map(root, "spk_id"),

            SampleRate = root.TryGetProperty("sample_rate", out var rate) ? rate.GetInt32() : 24_000,

            Sampling = new SamplingOptions(
                Temperature: Float(generate, "temperature", 0.9f),
                TopK: (int)Float(generate, "top_k", 50f),
                RepetitionPenalty: Float(generate, "repetition_penalty", 1.05f)),

            MaxNewTokens = (int)Float(generate, "max_new_tokens", 8192f),
        };
    }

    /// <summary>
    /// Reads an integer, trying the flat name and then the nested one.
    /// </summary>
    /// <remarks>
    /// Missing is an error rather than a default. A default here is a guess
    /// about the model's own vocabulary, and a wrong token id produces speech
    /// rather than a complaint.
    /// </remarks>
    private static int Int(JsonElement root, string source, string flat, string? group = null, string? nested = null)
    {
        if (root.TryGetProperty(flat, out var direct) && direct.ValueKind == JsonValueKind.Number)
        {
            return direct.GetInt32();
        }

        if (group is not null && nested is not null &&
            root.TryGetProperty(group, out var section) &&
            section.TryGetProperty(nested, out var value) &&
            value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt32();
        }

        var also = group is null ? string.Empty : $" (or {group}.{nested})";
        throw new InvalidDataException($"{source} has no '{flat}'{also}.");
    }

    private static IReadOnlyDictionary<string, int> Map(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, int>();
        }

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Number)
            {
                map[entry.Name] = entry.Value.GetInt32();
            }
        }

        return map;
    }

    private static float Float(JsonElement parent, string name, float fallback) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : fallback;
}
