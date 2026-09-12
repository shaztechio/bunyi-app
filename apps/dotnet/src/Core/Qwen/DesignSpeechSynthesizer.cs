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
/// Voice design, behind the engine's synthesizer seam (spec §1).
/// </summary>
/// <remarks>
/// <para>
/// The engine's state machine, its download, its metadata and its stop
/// behaviour are all mode-agnostic and already work; this exists so design mode
/// gets all of that rather than a second copy of it.
/// </para>
/// <para>
/// <b>It offers no speakers.</b> A design export's <c>spk_id</c> is empty — the
/// voice comes from a description instead — and reporting speakers it does not
/// have is what would let the window show a picker that changes nothing.
/// </para>
/// </remarks>
public sealed class DesignSpeechSynthesizer(
    ILogSink log,
    string variant = "int4",
    Func<string, string, ILogSink, IDesignPipeline>? open = null) : ISpeechSynthesizer
{
    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));

    private readonly Func<string, string, ILogSink, IDesignPipeline> _open =
        open ?? ((folder, precision, sink) => new DesignPipeline(folder, precision, sink));

    private IDesignPipeline? _pipeline;
    private string? _folder;

    /// <inheritdoc />
    /// <remarks>
    /// Always empty. §1 gives design mode a description rather than a speaker
    /// list, and the export has none to offer.
    /// </remarks>
    public IReadOnlyList<string> Speakers => [];

    /// <inheritdoc />
    public bool IsLoaded => _pipeline is not null;

    /// <inheritdoc />
    /// <remarks>
    /// True, and this is the mode where it is unambiguous: the description is
    /// not a decoration on a chosen voice, it is the only thing that decides
    /// what the voice is.
    /// </remarks>
    public bool SupportsInstruct => true;

    /// <inheritdoc />
    public Task LoadAsync(string modelFolder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFolder);

        if (_folder == modelFolder && _pipeline is not null) return Task.CompletedTask;

        // Replacing, so the old one goes first: two 3.8 GB pipelines resident at
        // once is the difference between fitting on a 16 GB machine and not.
        Release();

        ct.ThrowIfCancellationRequested();

        _pipeline = _open(modelFolder, variant, _log);
        _folder = modelFolder;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnloadAsync()
    {
        Release();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<SynthesisResult> SynthesizeAsync(
        GenerateRequest request, CancellationToken ct, IProgress<int>? frames = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_pipeline is null)
        {
            throw new InvalidOperationException("No model is loaded.");
        }

        // §1: the description is what makes this mode. Blank is allowed and
        // gives whatever voice the model settles on, which is the same
        // behaviour as leaving a style instruction empty elsewhere.
        var result = _pipeline.Generate(
            new DesignRequest(request.Text, request.Instruct, request.Language),
            progress: frames,
            maxFrames: request.SectionFrameLimit,
            ct: ct, audioPreview: request.AudioPreview);

        return Task.FromResult(new SynthesisResult(
            request.KeepRawSamples ? [] : ToPcm16(result.Samples, _log), 24_000, result.Frames)
            { RawSamples = request.KeepRawSamples ? result.Samples : null });
    }

    /// <summary>
    /// Turns the model's floats into the 16-bit samples §2 writes.
    /// </summary>
    /// <remarks>
    /// Attenuate an overdriven clip uniformly before conversion. Clamping
    /// individual peaks flattens the waveform and can introduce crackling.
    /// </remarks>
    internal static short[] ToPcm16(float[] samples, ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        double peak = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
                throw new InvalidDataException("The model produced invalid audio. Please generate again.");
            peak = Math.Max(peak, Math.Abs((double)sample));
        }

        var gain = peak > 1 ? 0.98 / peak : 1;
        if (gain < 1)
            log?.Log($"Output level: reduced by {-20 * Math.Log10(gain):F1} dB to prevent clipping (peak {peak:F3}).");

        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (float)(samples[i] * gain);
            pcm[i] = (short)Math.Round(value * short.MaxValue);
        }

        return pcm;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to do: this pipeline holds no cache between runs. Every frame's
    /// KV cache dies with the call that made it, and the sessions are the model
    /// itself rather than working memory.
    /// </remarks>
    public void ReleaseWorkingMemory()
    {
    }

    private void Release()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        _folder = null;
    }

    public ValueTask DisposeAsync()
    {
        Release();
        return ValueTask.CompletedTask;
    }
}
