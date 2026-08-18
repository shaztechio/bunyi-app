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
public sealed record ModelLayout(string Id, IReadOnlyList<ModelFile> Files)
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
        ]);
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
