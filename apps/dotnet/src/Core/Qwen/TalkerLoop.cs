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

namespace Bunyi.Core.Qwen;

/// <summary>Audio and the codes it was made from.</summary>
/// <param name="Samples">24 kHz mono, as the vocoder returned it.</param>
/// <param name="Codes">One array of codebook entries per frame.</param>
public sealed record SpeechResult(float[] Samples, IReadOnlyList<int[]> Codes)
{
    /// <summary>Frames produced, at 12 Hz.</summary>
    public int Frames => Codes.Count;

    /// <summary>How long the audio runs.</summary>
    public TimeSpan Duration(int sampleRate) =>
        TimeSpan.FromSeconds(Samples.Length / (double)sampleRate);
}

/// <summary>
/// Turns a primed sequence into speech: prefill, decode, vocode.
/// </summary>
/// <remarks>
/// <para>
/// Everything after the sequence is built is the same whichever mode asked for
/// it. Design mode and clone mode differ only in what they put in front of the
/// model — one describes a voice in words, the other shows it a recording — and
/// from the first generated frame onwards the two are indistinguishable. So this
/// is written once and takes the sequence as an argument.
/// </para>
/// <para>
/// Four graphs: prefill reads the whole sequence at once, decode advances one
/// frame at a time, the code predictor fills in the fifteen codebooks the talker
/// does not emit, and the vocoder turns the lot into samples.
/// </para>
/// </remarks>
public sealed class TalkerLoop : IDisposable
{
    private readonly QwenConfig _config;
    private readonly NpyArray _talkerCodec;
    private readonly NpyArray[] _groupCodec;
    private readonly TokenSampler _sampler;
    private readonly ILogSink _log;

    private readonly InferenceSession _prefillSession;
    private readonly InferenceSession _decodeSession;
    private readonly InferenceSession _codePredictorSession;
    private readonly InferenceSession _vocoderSession;

    private bool _disposed;

    /// <param name="graphs">The folder holding the four <c>.onnx</c> files.</param>
    /// <param name="talkerCodec">The first codebook's table. Not disposed here.</param>
    /// <param name="groupCodec">The other fifteen. Not disposed here.</param>
    public TalkerLoop(
        QwenConfig config,
        string graphs,
        NpyArray talkerCodec,
        NpyArray[] groupCodec,
        TokenSampler sampler,
        ILogSink log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _talkerCodec = talkerCodec ?? throw new ArgumentNullException(nameof(talkerCodec));
        _groupCodec = groupCodec ?? throw new ArgumentNullException(nameof(groupCodec));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        ArgumentException.ThrowIfNullOrWhiteSpace(graphs);

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

    /// <summary>Speaks a primed sequence.</summary>
    /// <param name="rows">The prefill sequence, one row per position.</param>
    /// <param name="trailingHidden">
    /// The text stream's padding, added to every generated frame. The text is
    /// finished by the time generation starts, so it pads from here on.
    /// </param>
    /// <param name="what">How this run is named in the log.</param>
    /// <param name="vocoderContext">
    /// Frames to vocode ahead of the generated ones and then cut away. Clone
    /// mode passes the reference recording's own codes: the vocoder carries
    /// state across frames, so starting it cold on the first generated frame
    /// makes the opening of a clone worse than the rest of it. Design mode has
    /// nothing to pass and starts cold, which is what its reference does too.
    /// </param>
    public SpeechResult Generate(
        float[][] rows,
        float[] trailingHidden,
        SamplingOptions sampling,
        int cap,
        string what,
        IProgress<int>? progress = null,
        IReadOnlyList<int[]>? vocoderContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(trailingHidden);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _log.Log($"{what}: {rows.Length} tokens of context.");

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

            var next = NextInput(frame, trailingHidden);
            (logits, hidden, pastKeys, pastValues) =
                RunDecode(next, position, pastKeys, pastValues);

            position++;
        }

        if (frames.Count == 0)
        {
            throw new InvalidOperationException(
                "The model produced no audio for that text. Try different words.");
        }

        _log.Log($"{what}: {frames.Count} frames.");
        return new SpeechResult(RunVocoder(frames, vocoderContext), frames);
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
                positions.Buffer.Span[(axis * rows.Length) + i] = i;
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
    /// padding.
    /// </remarks>
    private float[] NextInput(int[] frame, float[] trailingHidden)
    {
        var next = _talkerCodec.Row(frame[0]);

        for (var group = 0; group < _config.CodeGroups - 1; group++)
        {
            var embed = _groupCodec[group].Row(frame[group + 1]);
            for (var i = 0; i < next.Length; i++) next[i] += embed[i];
        }

        for (var i = 0; i < next.Length; i++) next[i] += trailingHidden[i];

        return next;
    }

    private float[] RunVocoder(List<int[]> frames, IReadOnlyList<int[]>? context)
    {
        var groups = _config.CodeGroups;
        var lead = context?.Count ?? 0;
        var total = lead + frames.Count;

        var codes = new DenseTensor<long>([1, groups, total]);

        // Transposed on the way in: the frames are gathered per frame, and the
        // vocoder wants them per codebook.
        void Put(int at, int[] frame)
        {
            for (var group = 0; group < groups; group++)
            {
                codes.Buffer.Span[(group * total) + at] = frame[group];
            }
        }

        for (var i = 0; i < lead; i++) Put(i, context![i]);
        for (var i = 0; i < frames.Count; i++) Put(lead + i, frames[i]);

        using var results = _vocoderSession.Run(
            [NamedOnnxValue.CreateFromTensor("codes", codes)]);

        var samples = results.First().AsTensor<float>().ToArray();
        if (lead == 0) return samples;

        // Cut by the same proportion of frames rather than by a fixed samples
        // per frame. The two agree whenever the vocoder's ratio is exact, and
        // when it is not this cuts at the right place anyway — which matters,
        // because a few samples out here is the reference's last syllable left
        // at the front of the answer.
        var cut = (int)((double)lead / total * samples.Length);
        return samples[cut..];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _prefillSession.Dispose();
        _decodeSession.Dispose();
        _codePredictorSession.Dispose();
        _vocoderSession.Dispose();
    }
}
