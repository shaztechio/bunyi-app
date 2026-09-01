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

using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Bunyi.Core.Qwen;

/// <summary>
/// What the engine needs of a voice-clone pipeline.
/// </summary>
/// <remarks>
/// An interface for the same reason design mode has one: the adapter above it
/// can then be tested without the 3.86 GB export.
/// </remarks>
public interface IClonePipeline : IDisposable
{
    /// <summary>The export's own sampling defaults.</summary>
    SamplingOptions DefaultSampling { get; }

    /// <summary>Speaks the request in the voice of the clip.</summary>
    SpeechResult Generate(
        CloneRequest request,
        ReadOnlySpan<float> reference,
        SamplingOptions? options = null,
        IProgress<int>? progress = null,
        int? maxFrames = null,
        CancellationToken ct = default);
}

/// <summary>
/// Drives the voice-clone graphs (spec §1, clone mode).
/// </summary>
/// <remarks>
/// <para>
/// Six graphs. Two are this mode's own — a speaker encoder that hears what the
/// voice is like, and a tokenizer encoder that turns the recording into the same
/// kind of codes the model itself produces. The other four are the talker loop,
/// shared with design mode.
/// </para>
/// <para>
/// Ported from the export's <c>generate_clone_onnx.py</c>, but that script is a
/// weak oracle: it builds its prefill three times over and leaves the abandoned
/// attempts in the file. Where it was ambiguous the reading that makes clone
/// reduce to the validated design layout was taken, and the export's own
/// <c>validation/</c> recordings are the arbiter.
/// </para>
/// </remarks>
public sealed class ClonePipeline : IClonePipeline
{
    /// <summary>
    /// The reference clip's length in samples, fixed by the export.
    /// </summary>
    /// <remarks>
    /// Ten seconds at 24 kHz. The tokenizer encoder's input shape is not dynamic
    /// — it is literally <c>[1, 240000]</c> in the graph — so a clip is padded
    /// or truncated to exactly this, and the codes are trimmed afterwards to
    /// however much was real.
    /// </remarks>
    public const int ReferenceSamples = 10 * MelSpectrogram.SampleRate;

    /// <summary>Samples per code frame: 24 kHz in, 12.5 Hz out.</summary>
    internal const int SamplesPerFrame = 1920;

    private readonly QwenConfig _config;
    private readonly ClonePrefill _prefill;
    private readonly QwenTokenizer _tokenizer;
    private readonly NpyArray _talkerCodec;
    private readonly NpyArray[] _groupCodec;
    private readonly TalkerLoop _talker;
    private readonly ILogSink _log;

    private readonly InferenceSession _speakerSession;
    private readonly InferenceSession _codecSession;

    private bool _disposed;

    /// <param name="folder">The export's root, holding <c>config.json</c>.</param>
    /// <param name="variant">The precision subfolder, normally <c>int4</c>.</param>
    public ClonePipeline(
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

            _prefill = new ClonePrefill(_config, projection, _talkerCodec, _groupCodec);
        }

        // Both encoders sit at the export's root rather than under the precision
        // folder: there is one of each, shared by every variant.
        _speakerSession = new InferenceSession(Path.Combine(folder, "speaker_encoder.onnx"));
        _codecSession = new InferenceSession(Path.Combine(folder, "tokenizer_encoder.onnx"));

        _talker = new TalkerLoop(
            _config,
            Path.Combine(folder, variant),
            _talkerCodec,
            _groupCodec,
            sampler ?? new TokenSampler(),
            _log,
            provider);
    }

    /// <inheritdoc />
    public SamplingOptions DefaultSampling => _config.Sampling;

    /// <inheritdoc />
    /// <param name="reference">The clip, mono at 24 kHz.</param>
    public SpeechResult Generate(
        CloneRequest request,
        ReadOnlySpan<float> reference,
        SamplingOptions? options = null,
        IProgress<int>? progress = null,
        int? maxFrames = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (reference.Length < MelSpectrogram.FftSize)
        {
            throw new ArgumentException(
                "That recording is too short to clone from. A few seconds of clear "
                + "speech works best.",
                nameof(reference));
        }

        // Everything downstream must hear the same audio. The codec encoder can
        // only take ten seconds, so anything past that is cut here rather than
        // inside it — otherwise the speaker embedding describes one recording
        // and the codes another, and the transcript describes words the model
        // was never shown. Given a transcript longer than the audio, the model
        // finishes the reference instead of speaking the text: observed, not
        // feared.
        var used = reference[..Math.Min(reference.Length, ReferenceSamples)];

        if (reference.Length > ReferenceSamples)
        {
            _log.Log(
                $"Voice clone: using the first "
                + $"{ReferenceSamples / (double)MelSpectrogram.SampleRate:F0}s of a "
                + $"{reference.Length / (double)MelSpectrogram.SampleRate:F1}s recording. "
                + "The transcript should cover only that much.");
        }
        else
        {
            _log.Log(
                $"Voice clone: reference is {used.Length / (double)MelSpectrogram.SampleRate:F1}s.");
        }

        var speaker = EncodeSpeaker(used);
        ct.ThrowIfCancellationRequested();

        var codes = EncodeReference(used);
        ct.ThrowIfCancellationRequested();

        _log.Log($"Voice clone: reference encoded to {codes.Count} frames.");

        return _talker.Generate(
            _prefill.Build(request, _tokenizer, speaker, codes),
            _prefill.TrailingHidden,
            options ?? _config.Sampling,
            maxFrames ?? TalkerLoop.FrameBudget(request.Text, _config.MaxNewTokens),
            "Voice clone",
            progress,
            vocoderContext: codes,
            ct);
    }

    /// <summary>
    /// What the voice sounds like, as one vector.
    /// </summary>
    /// <remarks>
    /// Fed the whole clip's log-mel rather than a fixed window: this graph's
    /// input length is dynamic, unlike the codec encoder's.
    /// </remarks>
    private float[] EncodeSpeaker(ReadOnlySpan<float> reference)
    {
        var mel = MelSpectrogram.Compute(reference);

        var tensor = new DenseTensor<float>(
            mel.Values, [1, mel.Frames, mel.Bins]);

        using var results = _speakerSession.Run(
            [NamedOnnxValue.CreateFromTensor("mels", tensor)]);

        return results.First().AsTensor<float>().ToArray();
    }

    /// <summary>
    /// The clip as codec frames, the same kind the model produces.
    /// </summary>
    /// <remarks>
    /// This is what makes it in-context learning rather than an impression: the
    /// model is shown the recording in its own vocabulary, beside the words it
    /// carries, and asked to continue the pattern.
    /// </remarks>
    private IReadOnlyList<int[]> EncodeReference(ReadOnlySpan<float> reference)
    {
        // The graph takes exactly ten seconds. Anything longer is cut; anything
        // shorter is padded with silence and then trimmed back out of the codes,
        // so the model is never shown silence that was not in the recording.
        var used = Math.Min(reference.Length, ReferenceSamples);

        var padded = new float[ReferenceSamples];
        reference[..used].CopyTo(padded);

        using var results = _codecSession.Run([
            NamedOnnxValue.CreateFromTensor(
                "waveform", new DenseTensor<float>(padded, [1, ReferenceSamples])),
        ]);

        var tensor = results.First().AsTensor<long>();
        var groups = tensor.Dimensions[1];
        var produced = tensor.Dimensions[2];

        var real = Math.Min(
            produced,
            (int)Math.Ceiling(used / (double)SamplesPerFrame));

        var frames = new int[real][];
        for (var frame = 0; frame < real; frame++)
        {
            var codes = new int[groups];
            for (var group = 0; group < groups; group++)
            {
                codes[group] = (int)tensor[0, group, frame];
            }

            frames[frame] = codes;
        }

        return frames;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _talker.Dispose();
        _speakerSession.Dispose();
        _codecSession.Dispose();

        _talkerCodec.Dispose();
        foreach (var table in _groupCodec) table.Dispose();
    }
}
