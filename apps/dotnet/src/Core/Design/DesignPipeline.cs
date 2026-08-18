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
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Bunyi.Core.Design;

/// <summary>What one generation produced.</summary>
/// <param name="Samples">24 kHz mono, in the range −1 to 1.</param>
/// <param name="Codes">
/// The codec frames, sixteen codes each, at 12 Hz.
/// </param>
/// <remarks>
/// The codes are kept rather than discarded once the vocoder has run. They are
/// what the reference implementation can be compared against exactly — audio can
/// only be compared approximately — and they are cheap: sixteen integers a
/// frame, against a thousand samples.
/// </remarks>
public sealed record DesignResult(float[] Samples, IReadOnlyList<int[]> Codes)
{
    /// <summary>How many frames were produced.</summary>
    public int Frames => Codes.Count;

    /// <summary>How long the audio runs.</summary>
    public TimeSpan Duration(int sampleRate) =>
        TimeSpan.FromSeconds((double)Samples.Length / sampleRate);
}

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
public sealed class DesignPipeline : IDisposable
{
    private readonly DesignConfig _config;
    private readonly PrefillBuilder _prefill;
    private readonly QwenTokenizer _tokenizer;
    private readonly NpyArray _talkerCodec;
    private readonly NpyArray[] _groupCodec;
    private readonly TokenSampler _sampler;
    private readonly ILogSink _log;

    private readonly InferenceSession _prefillSession;
    private readonly InferenceSession _decodeSession;
    private readonly InferenceSession _codePredictorSession;
    private readonly InferenceSession _vocoderSession;

    private bool _disposed;

    /// <summary>Opens the sessions and maps the tables.</summary>
    /// <param name="folder">The export's root, holding <c>config.json</c>.</param>
    /// <param name="variant">The precision subfolder, normally <c>int4</c>.</param>
    public DesignPipeline(
        string folder,
        string variant,
        ILogSink log,
        TokenSampler? sampler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _sampler = sampler ?? new TokenSampler();

        _config = DesignConfig.Load(Path.Combine(folder, "config.json"));
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

            _prefill = new PrefillBuilder(_config, projection, _talkerCodec);
        }

        var graphs = Path.Combine(folder, variant);

        // The arena is left off on the decode session. It was measured at 0.5 GB
        // over 267 frames for about 9% wall-clock, and every step allocates a
        // cache one token longer than the last, so there is nothing for an arena
        // to reuse. RESEARCH-ONNX.md has the figures.
        var decodeOptions = new SessionOptions { EnableCpuMemArena = false };

        _prefillSession = new InferenceSession(Path.Combine(graphs, "talker_prefill.onnx"));
        _decodeSession = new InferenceSession(Path.Combine(graphs, "talker_decode.onnx"), decodeOptions);
        _codePredictorSession = new InferenceSession(Path.Combine(graphs, "code_predictor.onnx"));

        // The vocoder gets a CPU session whatever the others use: its graph
        // fails on every GPU provider tried, on the same node.
        _vocoderSession = new InferenceSession(Path.Combine(graphs, "vocoder.onnx"));
    }

    /// <summary>The export's own sampling defaults.</summary>
    public SamplingOptions DefaultSampling => _config.Sampling;

    /// <summary>Speaks the request.</summary>
    /// <param name="request">Text, description and language.</param>
    /// <param name="options">Sampling, or the export's defaults when null.</param>
    /// <param name="progress">Frames produced so far.</param>
    /// <param name="maxFrames">A cap, or the export's own when null.</param>
    /// <param name="ct">Cancellation, checked once a frame.</param>
    public DesignResult Generate(
        DesignRequest request,
        SamplingOptions? options = null,
        IProgress<int>? progress = null,
        int? maxFrames = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sampling = options ?? _config.Sampling;
        var cap = maxFrames ?? _config.MaxNewTokens;

        var rows = _prefill.Build(request, _tokenizer);
        _log.Log($"Voice design: {rows.Length} tokens of context.");

        var (logits, hidden, pastKeys, pastValues) = RunPrefill(rows);

        var frames = new List<int[]>();
        var produced = new List<int>();
        var position = rows.Length;

        // Control tokens are suppressed for the whole run: the last 1024 of the
        // vocabulary, except the one that ends generation.
        var suppressFrom = _config.VocabSize - 1024;

        while (frames.Count < cap)
        {
            ct.ThrowIfCancellationRequested();

            var step = new float[_config.VocabSize];
            Array.Copy(logits, logits.Length - _config.VocabSize, step, 0, _config.VocabSize);

            // The last 1024 are control tokens and none may be spoken — except
            // the one that ends generation, which is inside that range and has
            // to survive it.
            for (var t = suppressFrom; t < _config.VocabSize; t++)
            {
                if (t != _config.CodecEosTokenId) step[t] = float.NegativeInfinity;
            }

            // Two frames minimum. A clip that stops immediately is silence, and
            // the reference guards it the same way.
            if (frames.Count < 2) step[_config.CodecEosTokenId] = float.NegativeInfinity;

            var first = _sampler.Sample(step, sampling, produced);
            if (first == _config.CodecEosTokenId) break;

            produced.Add(first);

            var frame = PredictFrame(first, hidden, sampling);
            frames.Add(frame);
            progress?.Report(frames.Count);

            var next = NextInput(frame);
            (logits, hidden, pastKeys, pastValues) =
                RunDecode(next, position, pastKeys, pastValues);

            position++;
        }

        if (frames.Count == 0)
        {
            throw new InvalidOperationException(
                "The model produced no audio for that text. Try different words.");
        }

        _log.Log($"Voice design: {frames.Count} frames.");
        return new DesignResult(RunVocoder(frames), frames);
    }

    private (float[] Logits, float[] Hidden, float[] Keys, float[] Values) RunPrefill(float[][] rows)
    {
        var width = _config.HiddenSize;
        var embeds = new DenseTensor<float>([1, rows.Length, width]);
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].AsSpan().CopyTo(embeds.Buffer.Span.Slice(i * width, width));
        }

        var mask = new DenseTensor<long>([1, rows.Length]);
        mask.Buffer.Span.Fill(1);

        // Three rows of positions, all the same: the graph takes a 3-D rotary
        // position and this model uses one value repeated.
        var positions = new DenseTensor<long>([3, 1, rows.Length]);
        for (var axis = 0; axis < 3; axis++)
        {
            for (var i = 0; i < rows.Length; i++)
            {
                positions.Buffer.Span[axis * rows.Length + i] = i;
            }
        }

        using var results = _prefillSession.Run([
            NamedOnnxValue.CreateFromTensor("inputs_embeds", embeds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("position_ids", positions),
        ]);

        var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());

        ReadOnlyMemory<float>? Layer(string prefix, int layer) =>
            byName.TryGetValue($"{prefix}_{layer}", out var values) ? values : null;

        return (
            byName["logits"],
            byName["hidden_states"],
            KvCache.Stack(l => Layer("present_key", l), _config.Layers),
            KvCache.Stack(l => Layer("present_value", l), _config.Layers));
    }

    private (float[] Logits, float[] Hidden, float[] Keys, float[] Values) RunDecode(
        float[] input, int position, float[] pastKeys, float[] pastValues)
    {
        var width = _config.HiddenSize;
        var past = pastKeys.Length / (_config.Layers * _config.KvHeads * _config.HeadDim);

        var embeds = new DenseTensor<float>(input, [1, 1, width]);

        var mask = new DenseTensor<long>([1, position + 1]);
        mask.Buffer.Span.Fill(1);

        var positions = new DenseTensor<long>([3, 1, 1]);
        positions.Buffer.Span.Fill(position);

        var keys = new DenseTensor<float>(
            pastKeys, KvCache.Shape(_config.Layers, _config.KvHeads, past, _config.HeadDim));
        var values = new DenseTensor<float>(
            pastValues, KvCache.Shape(_config.Layers, _config.KvHeads, past, _config.HeadDim));

        using var results = _decodeSession.Run([
            NamedOnnxValue.CreateFromTensor("inputs_embeds", embeds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("position_ids", positions),
            NamedOnnxValue.CreateFromTensor("past_keys", keys),
            NamedOnnxValue.CreateFromTensor("past_values", values),
        ]);

        var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());

        return (byName["logits"], byName["hidden_states"],
                byName["present_keys"], byName["present_values"]);
    }

    /// <summary>
    /// Fills in the fifteen codebooks the talker does not emit.
    /// </summary>
    /// <remarks>
    /// The predictor is fed the talker's last hidden state and the first code,
    /// then each group's own embedding in turn. Its cache starts empty every
    /// frame — it predicts within a frame, not across them.
    /// </remarks>
    private int[] PredictFrame(int first, float[] hidden, SamplingOptions sampling)
    {
        var width = _config.HiddenSize;
        var frame = new int[_config.CodeGroups];
        frame[0] = first;

        var talkerHidden = hidden.AsSpan(hidden.Length - width, width).ToArray();
        var firstEmbed = _talkerCodec.Row(first);

        var input = new float[2 * width];
        talkerHidden.CopyTo(input, 0);
        firstEmbed.CopyTo(input, width);

        var rows = 2;
        float[] keys = [];
        float[] values = [];

        for (var group = 0; group < _config.CodeGroups - 1; group++)
        {
            var past = keys.Length == 0
                ? 0
                : keys.Length / (_config.CodePredictorLayers
                    * _config.CodePredictorKvHeads * _config.CodePredictorHeadDim);

            var shape = KvCache.Shape(
                _config.CodePredictorLayers, _config.CodePredictorKvHeads,
                past, _config.CodePredictorHeadDim);

            using var results = _codePredictorSession.Run([
                NamedOnnxValue.CreateFromTensor("inputs_embeds",
                    new DenseTensor<float>(input, [1, rows, width])),
                NamedOnnxValue.CreateFromTensor("generation_steps",
                    new DenseTensor<long>(new long[] { group }, [1])),
                NamedOnnxValue.CreateFromTensor("past_keys",
                    new DenseTensor<float>(keys, shape)),
                NamedOnnxValue.CreateFromTensor("past_values",
                    new DenseTensor<float>(values, shape)),
            ]);

            var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());
            var logits = byName["logits"];

            var step = logits.AsSpan(logits.Length - _config.CodePredictorVocabSize).ToArray();
            frame[group + 1] = _sampler.Sample(step, sampling);

            keys = byName["present_keys"];
            values = byName["present_values"];

            // After the first call the predictor takes one row: the embedding
            // of the code just chosen, from that group's own table.
            input = _groupCodec[group].Row(frame[group + 1]);
            rows = 1;
        }

        return frame;
    }

    /// <summary>
    /// The talker's input for the next frame.
    /// </summary>
    /// <remarks>
    /// Every codebook's embedding for this frame, summed, plus the text stream's
    /// padding — the text is finished, so it pads from here on.
    /// </remarks>
    private float[] NextInput(int[] frame)
    {
        var next = _talkerCodec.Row(frame[0]);

        for (var group = 0; group < _config.CodeGroups - 1; group++)
        {
            var embed = _groupCodec[group].Row(frame[group + 1]);
            for (var i = 0; i < next.Length; i++) next[i] += embed[i];
        }

        var pad = _prefill.TrailingHidden;
        for (var i = 0; i < next.Length; i++) next[i] += pad[i];

        return next;
    }

    private float[] RunVocoder(List<int[]> frames)
    {
        var groups = _config.CodeGroups;
        var codes = new DenseTensor<long>([1, groups, frames.Count]);

        // Transposed on the way in: the frames are gathered per frame, and the
        // vocoder wants them per codebook.
        for (var frame = 0; frame < frames.Count; frame++)
        {
            for (var group = 0; group < groups; group++)
            {
                codes.Buffer.Span[group * frames.Count + frame] = frames[frame][group];
            }
        }

        using var results = _vocoderSession.Run(
            [NamedOnnxValue.CreateFromTensor("codes", codes)]);

        return results.First().AsTensor<float>().ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _prefillSession.Dispose();
        _decodeSession.Dispose();
        _codePredictorSession.Dispose();
        _vocoderSession.Dispose();

        _talkerCodec.Dispose();
        foreach (var table in _groupCodec) table.Dispose();
    }
}
