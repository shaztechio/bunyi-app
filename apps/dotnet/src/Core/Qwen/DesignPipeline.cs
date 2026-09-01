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
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Bunyi.Core.Qwen;

/// <summary>
/// Drives the voice-design graphs (spec §1, design mode).
/// </summary>
/// <remarks>
/// <para>
/// Ported from the export's own <c>generate_onnx.py</c>. The graphs are
/// identical to the preset-voice export's apart from hidden width (see
/// RESEARCH-ONNX.md), so this drives both in principle; only design mode uses it
/// today.
/// </para>
/// <para>
/// Four sessions, all alive at once because each is needed every frame: prefill
/// once, then per frame a code predictor run for each of fifteen remaining
/// codebooks and one decode step, then the vocoder over everything at the end.
/// </para>
/// </remarks>
/// <summary>
/// What the engine needs of a voice-design pipeline.
/// </summary>
/// <remarks>
/// An interface so the adapter above the pipeline can be tested without the
/// 5.85 GB export: everything it does - converting samples, refusing when no
/// model is loaded, releasing one pipeline before opening another - is worth
/// pinning, and none of it needs a model to be wrong.
/// </remarks>
public interface IDesignPipeline : IDisposable
{
    /// <summary>The export's own sampling defaults.</summary>
    SamplingOptions DefaultSampling { get; }

    /// <summary>Speaks the request.</summary>
    SpeechResult Generate(
        DesignRequest request,
        SamplingOptions? options = null,
        IProgress<int>? progress = null,
        int? maxFrames = null,
        CancellationToken ct = default);
}

public sealed class DesignPipeline : IDesignPipeline
{
    private readonly QwenConfig _config;
    private readonly DesignPrefill _prefill;
    private readonly QwenTokenizer _tokenizer;
    private readonly NpyArray _talkerCodec;
    private readonly NpyArray[] _groupCodec;
    private readonly TalkerLoop _talker;
    private readonly ILogSink _log;

    private bool _disposed;

    /// <summary>Opens the sessions and maps the tables.</summary>
    /// <param name="folder">The export's root, holding <c>config.json</c>.</param>
    /// <param name="variant">The precision subfolder, normally <c>int4</c>.</param>
    public DesignPipeline(
        string folder,
        string variant,
        ILogSink log,
        TokenSampler? sampler = null,
        ExecutionProviderChoice? provider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _log = log ?? throw new ArgumentNullException(nameof(log));

        _config = QwenConfig.Load(Path.Combine(folder, "config.json"));
        _tokenizer = QwenTokenizer.Load(Path.Combine(folder, "tokenizer"));

        var embeddings = Path.Combine(folder, "embeddings");
        NpyArray Open(string name) => NpyArray.Open(Path.Combine(embeddings, $"{name}.npy"));

        _talkerCodec = Open("talker_codec_embedding");

        // One table per codebook after the first: the code predictor emits
        // fifteen more, each from its own vocabulary.
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

            _prefill = new DesignPrefill(_config, projection, _talkerCodec);
        }

        _talker = new TalkerLoop(
            _config,
            Path.Combine(folder, variant),
            _talkerCodec,
            _groupCodec,
            sampler ?? new TokenSampler(),
            _log,
            provider);
    }

    /// <summary>The export's own sampling defaults.</summary>
    public SamplingOptions DefaultSampling => _config.Sampling;

    /// <summary>Speaks the request.</summary>
    /// <param name="request">Text, description and language.</param>
    /// <param name="options">Sampling, or the export's defaults when null.</param>
    /// <param name="progress">Frames produced so far.</param>
    /// <param name="maxFrames">A cap, or the export's own when null.</param>
    /// <param name="ct">Cancellation, checked once a frame.</param>
    public SpeechResult Generate(
        DesignRequest request,
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
            "Voice design",
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
