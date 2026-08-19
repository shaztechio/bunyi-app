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

namespace Bunyi.Core.Models;

/// <summary>
/// The files one ONNX export ships, and which of them are required.
/// </summary>
/// <remarks>
/// <para>
/// Per-export rather than one global list, because published exports genuinely
/// differ: the preset-voice export keeps its config at
/// <c>embeddings/config.json</c> with no top-level one, while both wavekat
/// exports keep every weight under a precision subfolder. A single pattern
/// cannot describe both, which is why /spec/DATA-FORMATS.md defines
/// completeness against a per-export required-file list.
/// </para>
/// <para>
/// <b>Variant scoping is not an optimisation.</b> One published VoiceDesign
/// export is 18.55 GB in total and 4.27 GB in its <c>int4</c> subtree.
/// Downloading the repository because the layout was assumed flat would fetch
/// 14 GB that is never loaded.
/// </para>
/// </remarks>
public sealed record ModelLayout(
    string Id,
    IReadOnlyList<ModelFile> Files,
    long ApproxDownloadBytes = 0)
{
    /// <summary>The files a 404 must fail the whole download for (spec §3c).</summary>
    public IEnumerable<ModelFile> RequiredFiles => Files.Where(f => f.Required);

    /// <summary>
    /// Every <c>.onnx</c> that this export ships external data for, paired with
    /// the sibling that must accompany it.
    /// </summary>
    public IEnumerable<(string Graph, string Data)> ExternalDataPairs =>
        Files.Select(f => f.RelativePath)
             .Where(p => p.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase))
             .Select(data => (Graph: data[..^5], Data: data))
             .Where(pair => Files.Any(f =>
                 string.Equals(f.RelativePath, pair.Graph, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The preset-voice export: <c>elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX</c>.
    /// </summary>
    /// <remarks>
    /// Verified against the published repository, not assumed. Note there is no
    /// top-level <c>config.json</c> — this export carries it as
    /// <c>embeddings/config.json</c>, which is precisely why the MLX
    /// completeness rule could not be reused.
    /// </remarks>
    /// <remarks>
    /// The size is the published total, measured from the repository listing
    /// rather than estimated. Doctor uses it to answer "is there room for this"
    /// before the download starts, which is the whole point of asking early.
    /// </remarks>
    public static ModelLayout PresetVoice { get; } = new(
        "elbruno-customvoice-0.6b",
        [
            new ModelFile("embeddings/config.json", Required: true),
            new ModelFile("embeddings/speaker_ids.json", Required: true),
            new ModelFile("talker_prefill.onnx", Required: true),
            new ModelFile("talker_prefill.onnx.data", Required: true),
            new ModelFile("talker_decode.onnx", Required: true),
            new ModelFile("talker_decode.onnx.data", Required: true),
            new ModelFile("code_predictor.onnx", Required: true),
            new ModelFile("vocoder.onnx", Required: true),
            new ModelFile("vocoder.onnx.data", Required: true),
            new ModelFile("tokenizer/vocab.json", Required: true),
            new ModelFile("tokenizer/merges.txt", Required: true),
        ],
        ApproxDownloadBytes: 5_880_000_000);

    /// <summary>
    /// The voice-design export: <c>wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verified against the published repository by downloading it, not
    /// assumed. Two things differ from the preset-voice export and both matter.
    /// </para>
    /// <para>
    /// <b>The graphs live under a precision subfolder.</b> Only <c>int4/</c> is
    /// fetched; <c>fp32/</c> is another 12.70 GB of the same model and would
    /// quadruple the download to use none of it. A layout that assumed a flat
    /// export would pull 18.55 GB to use 4.27 GB.
    /// </para>
    /// <para>
    /// <b>The embeddings are files rather than graph initialisers.</b> Fifteen
    /// codebook tables, a text embedding of 1.24 GB, and the projection weights
    /// — all required, because the pipeline reads them directly. The
    /// preset-voice export carries its equivalents inside the graphs, which is
    /// why its list is so much shorter.
    /// </para>
    /// <para>
    /// 5.85 GB in total, measured from the repository listing. Almost exactly
    /// the preset export's 5.88 GB: <c>int4</c> more than pays for 2.8x the
    /// parameters, and RESEARCH-ONNX.md records that it is the cheaper of the
    /// two to hold in memory as well.
    /// </para>
    /// </remarks>
    public static ModelLayout VoiceDesign { get; } = new(
        "wavekat-voicedesign-1.7b-int4",
        [
            new ModelFile("config.json", Required: true),

            new ModelFile("int4/talker_prefill.onnx", Required: true),
            new ModelFile("int4/talker_prefill.onnx.data", Required: true),
            new ModelFile("int4/talker_decode.onnx", Required: true),
            new ModelFile("int4/talker_decode.onnx.data", Required: true),
            new ModelFile("int4/code_predictor.onnx", Required: true),
            new ModelFile("int4/code_predictor.onnx.data", Required: true),
            new ModelFile("int4/vocoder.onnx", Required: true),
            new ModelFile("int4/vocoder.onnx.data", Required: true),

            new ModelFile("embeddings/text_embedding.npy", Required: true),
            new ModelFile("embeddings/talker_codec_embedding.npy", Required: true),
            new ModelFile("embeddings/text_projection_fc1_weight.npy", Required: true),
            new ModelFile("embeddings/text_projection_fc1_bias.npy", Required: true),
            new ModelFile("embeddings/text_projection_fc2_weight.npy", Required: true),
            new ModelFile("embeddings/text_projection_fc2_bias.npy", Required: true),
            new ModelFile("embeddings/small_to_mtp_projection_weight.npy", Required: true),
            new ModelFile("embeddings/small_to_mtp_projection_bias.npy", Required: true),

            new ModelFile("embeddings/cp_codec_embedding_0.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_1.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_2.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_3.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_4.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_5.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_6.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_7.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_8.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_9.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_10.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_11.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_12.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_13.npy", Required: true),
            new ModelFile("embeddings/cp_codec_embedding_14.npy", Required: true),

            new ModelFile("tokenizer/tokenizer.json", Required: true),
            new ModelFile("tokenizer/vocab.json", Required: true),
            new ModelFile("tokenizer/merges.txt", Required: true),
            new ModelFile("tokenizer/added_tokens.json"),
        ],
        ApproxDownloadBytes: 5_850_000_000);

    /// <summary>
    /// The Whisper model that transcribes a reference clip (spec §4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>base</c>, not <c>base.en</c>: §1 offers ten languages and the
    /// English-only build would transcribe the other nine into nonsense that
    /// looks like English. 141 MB against tiny's 74 or small's 465 — tiny
    /// mishears enough to be worse than useless for a clone, where the
    /// transcript has to match the recording word for word.
    /// </para>
    /// <para>
    /// Fetched through the same downloader as everything else, so §3b's
    /// progress, resume and checksums apply to it too. It is a second download
    /// on top of a mode's own model, which is why nothing fetches it until a
    /// transcript is actually wanted.
    /// </para>
    /// </remarks>
    public static ModelLayout Whisper { get; } = new(
        "whisper-base",
        [new ModelFile("ggml-base.bin", Required: true)],
        ApproxDownloadBytes: 148_000_000);

    /// <summary>
    /// Where <see cref="Whisper"/> comes from.
    /// </summary>
    /// <remarks>
    /// Not a §3a per-mode source: that setting exists so a user can point a
    /// <em>mode</em> at their own export or a mirror, and transcription is not a
    /// mode. whisper.cpp's own repository is the origin every other distribution
    /// of these files copies from.
    /// </remarks>
    public const string WhisperSource = "ggerganov/whisper.cpp";

    /// <summary>Whether a mode has an export to download at all.</summary>
    /// <remarks>
    /// Asked before <see cref="For" />, so a caller that can cope with an
    /// unimplemented mode — Doctor, which is asked about whatever tab is on
    /// screen — does not have to catch an exception to find out.
    /// </remarks>
    public static bool Exists(TtsMode mode) =>
        mode is TtsMode.PresetVoice or TtsMode.VoiceDesign;

    /// <summary>The export a mode uses.</summary>
    /// <remarks>
    /// Clone has none yet, and answering with the preset-voice layout would
    /// have the app download 5.88 GB for a mode that cannot run — so it says so
    /// instead.
    /// </remarks>
    public static ModelLayout For(TtsMode mode) => mode switch
    {
        TtsMode.PresetVoice => PresetVoice,
        TtsMode.VoiceDesign => VoiceDesign,
        _ => throw new NotSupportedException(
            $"{mode.DisplayName()} is not implemented yet, so it has no model to download."),
    };
}

/// <summary>Why a model folder is not usable yet.</summary>
/// <param name="IsComplete">Whether it may be loaded without going to the network.</param>
/// <param name="Missing">Required entries that are absent or empty.</param>
/// <param name="Partial">Interrupted downloads, and graphs missing external data.</param>
public sealed record ModelCompleteness(
    bool IsComplete,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Partial)
{
    /// <summary>A short reason, for the log.</summary>
    public string Describe()
    {
        if (IsComplete) return "complete";
        var parts = new List<string>();
        if (Missing.Count > 0) parts.Add($"{Missing.Count} missing ({string.Join(", ", Missing.Take(3))})");
        if (Partial.Count > 0) parts.Add($"{Partial.Count} incomplete ({string.Join(", ", Partial.Take(3))})");
        return string.Join("; ", parts);
    }
}
