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

using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;

namespace Bunyi.Core.Qwen;

/// <summary>
/// What the engine needs of a preset-voice pipeline.
/// </summary>
/// <remarks>
/// An interface for the same reason the other two modes have one: the adapter
/// above it can be tested without the 5.9 GB export.
/// </remarks>
public interface IPresetPipeline : IDisposable
{
    /// <summary>The export's own sampling defaults.</summary>
    SamplingOptions DefaultSampling { get; }

    /// <summary>The speakers the export offers, in its own order.</summary>
    IReadOnlyList<string> Speakers { get; }

    /// <summary>Speaks the request in the named voice.</summary>
    SpeechResult Generate(
        PresetRequest request,
        SamplingOptions? options = null,
        IProgress<int>? progress = null,
        int? maxFrames = null,
        CancellationToken ct = default);
}

/// <summary>
/// Drives the preset-voice graphs (spec §1, preset voice).
/// </summary>
/// <remarks>
/// <para>
/// The same four graphs as design mode, driven by the same <see cref="TalkerLoop"/>:
/// RESEARCH-ONNX.md establishes that the two exports match name for name and
/// differ only in hidden width, which the loop reads from config. What differs
/// is input preparation — a speaker's codec row in place of a described voice
/// — and that lives in <see cref="PresetPrefill"/>.
/// </para>
/// <para>
/// Preset voice ran on a third-party library until #178. WHY-NOT-ELBRUNO.md
/// says why it was replaced and RESEARCH-ONNX.md has the measurements: about
/// half the memory, the same speed, a style instruction that reaches the
/// model, and progress reported frame by frame.
/// </para>
/// <para>
/// Two things about this export's layout that the design one does not share:
/// the config lives at <c>embeddings/config.json</c> with the speakers in
/// <c>embeddings/speaker_ids.json</c> beside it, and the graphs sit at the
/// root rather than under a precision folder.
/// </para>
/// </remarks>
public sealed class PresetPipeline : IPresetPipeline
{
    private readonly QwenConfig _config;
    private readonly PresetPrefill _prefill;
    private readonly QwenTokenizer _tokenizer;
    private readonly NpyArray _talkerCodec;
    private readonly NpyArray[] _groupCodec;
    private readonly TalkerLoop _talker;
    private readonly ILogSink _log;

    private bool _disposed;

    /// <summary>Opens the sessions and maps the tables.</summary>
    /// <param name="folder">The export's root: graphs at the top, <c>embeddings/</c> and <c>tokenizer/</c> beneath.</param>
    public PresetPipeline(
        string folder,
        ILogSink log,
        TokenSampler? sampler = null,
        ExecutionProviderChoice? provider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _log = log ?? throw new ArgumentNullException(nameof(log));

        var embeddings = Path.Combine(folder, "embeddings");

        _config = QwenConfig.Load(
            Path.Combine(embeddings, "config.json"),
            Path.Combine(embeddings, "speaker_ids.json"));
        // This export's tokenizer folder is a bare vocab and merges; the chat
        // specials live in its config. Without them the sequence is garbage of
        // the right length — measured as 153 greedy frames for a three-second
        // sentence, against 34 tokens of context where the layout predicts 21.
        _tokenizer = QwenTokenizer.Load(Path.Combine(folder, "tokenizer"), _config.ChatSpecials);

        NpyArray Open(string name) => NpyArray.Open(Path.Combine(embeddings, $"{name}.npy"));

        _talkerCodec = Open("talker_codec_embedding");

        _groupCodec = new NpyArray[_config.CodeGroups - 1];
        for (var g = 0; g < _groupCodec.Length; g++) _groupCodec[g] = Open($"cp_codec_embedding_{g}");

        using (var fc1W = Open("text_projection_fc1_weight"))
        using (var fc1B = Open("text_projection_fc1_bias"))
        using (var fc2W = Open("text_projection_fc2_weight"))
        using (var fc2B = Open("text_projection_fc2_bias"))
        {
            var projection = new TextProjection(
                Open("text_embedding"),
                fc1W.ToArray(), fc1B.ToArray(), fc2W.ToArray(), fc2B.ToArray());

            _prefill = new PresetPrefill(_config, projection, _talkerCodec);
        }

        _talker = new TalkerLoop(
            _config,
            folder,
            _talkerCodec,
            _groupCodec,
            sampler ?? new TokenSampler(),
            _log,
            provider);
    }

    /// <inheritdoc />
    public SamplingOptions DefaultSampling => _config.Sampling;

    /// <inheritdoc />
    public IReadOnlyList<string> Speakers => _prefill.Speakers;

    /// <inheritdoc />
    public SpeechResult Generate(
        PresetRequest request,
        SamplingOptions? options = null,
        IProgress<int>? progress = null,
        int? maxFrames = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _talker.Generate(
            _prefill.Build(request, _tokenizer),
            _prefill.TrailingHidden,
            options ?? _config.Sampling,
            maxFrames ?? TalkerLoop.FrameBudget(request.Text, _config.MaxNewTokens),
            "Preset voice",
            progress,
            vocoderContext: null,
            ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _talker.Dispose();

        _talkerCodec.Dispose();
        foreach (var table in _groupCodec) table.Dispose();
    }
}
