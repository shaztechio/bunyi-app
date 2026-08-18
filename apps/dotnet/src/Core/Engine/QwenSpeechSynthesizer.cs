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
using ElBruno.QwenTTS.Pipeline;
using Microsoft.ML.OnnxRuntime;

namespace Bunyi.Core.Engine;

/// <summary>
/// Preset-voice synthesis, over the <c>ElBruno.QwenTTS</c> ONNX pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Covers <b>preset voice only</b>. The library exposes CustomVoice variants
/// and nothing else; voice design and voice clone need their own
/// implementations of <see cref="ISpeechSynthesizer"/> against the exports that
/// ship Python reference scripts.
/// </para>
/// <para>
/// Two deliberate choices about how it is driven, both from
/// apps/dotnet/RESEARCH-ONNX.md. The pipeline is pointed at a folder
/// <see cref="Models.ModelDownloader"/> filled, so its own downloader — which
/// implements none of §3b — is never used. And audio comes back as raw samples
/// rather than a file it writes, so the filename, the folder and the RIFF
/// metadata stay ours (§2).
/// </para>
/// </remarks>
public sealed class QwenSpeechSynthesizer(ILogSink log) : ISpeechSynthesizer
{
    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Named only because the pipeline's factory requires one. It is never
    /// fetched: the engine calls this after ModelDownloader has already proved
    /// the folder complete, so the library's own downloader has nothing to do.
    /// </summary>
    private const string PresetVoiceRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX";

    private TtsPipeline? _pipeline;

    /// <inheritdoc />
    public IReadOnlyList<string> Speakers { get; private set; } = [];

    /// <inheritdoc />
    public bool IsLoaded => _pipeline is not null;

    /// <inheritdoc />
    /// <remarks>
    /// False for the 0.6B variant, which is this library's rule rather than the
    /// model's: Qwen documents style control on that checkpoint, and the
    /// restriction here is a hardcoded per-variant flag
    /// (<c>QwenModelVariantConfig.SupportsInstruct</c>) that reports false and
    /// logs "Instruction text ignored". Reported upstream as
    /// elbruno/ElBruno.QwenTTS#64; if the flag is corrected there this starts
    /// returning true on its own. Owning prompt construction lifts it either
    /// way, which M8 has to do anyway to reach voice design — see
    /// RESEARCH-ONNX.md.
    /// </remarks>
    public bool SupportsInstruct =>
        _pipeline is not null && QwenModelVariantConfig.SupportsInstruct(_pipeline.ModelVariant);

    /// <inheritdoc />
    public async Task LoadAsync(string modelFolder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFolder);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Unload();

            var talker = SessionOptionsFactory(OnnxRuntimeEnv.Current);

            // The vocoder always gets CPU: its graph fails on every GPU
            // provider tried, on the same node.
            var vocoder = SessionOptionsFactory(ExecutionProviderChoice.Cpu);

            _log.Log(
                $"Loading the model from {modelFolder} " +
                $"(talker on {OnnxRuntimeEnv.Current}, vocoder on CPU).");

            _pipeline = await TtsPipeline.CreateAsync(
                modelDir: modelFolder,
                downloadProgress: WarnIfItDownloads(),
                repoId: PresetVoiceRepo,
                sessionOptionsFactory: talker,
                vocoderSessionOptionsFactory: vocoder,
                variant: QwenModelVariant.Qwen06B,
                maxConcurrency: 1,
                cancellationToken: ct).ConfigureAwait(false);

            Speakers = [.. _pipeline.Speakers];
            _log.Log($"Model ready. Speakers: {string.Join(", ", Speakers)}.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task UnloadAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_pipeline is null) return;
            Unload();
            _log.Log("Released the model.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SynthesisResult> SynthesizeAsync(GenerateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pipeline = _pipeline
            ?? throw new InvalidOperationException("No model is loaded.");

        var speaker = string.IsNullOrWhiteSpace(request.Speaker) ? "ryan" : request.Speaker;

        // "auto" (§1's default) is what the pipeline already expects for "let
        // the model decide from the text", so it passes through unchanged.
        var language = string.IsNullOrWhiteSpace(request.Language) ? "auto" : request.Language;

        var instruct = string.IsNullOrWhiteSpace(request.Instruct) ? null : request.Instruct;

        var audio = await pipeline.SynthesizeToPcmAsync(
            request.Text, speaker, language, instruct, null, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        return new SynthesisResult(
            ToPcm16(audio.Samples.Span),
            audio.SampleRate,
            audio.Metrics?.GeneratedFrames ?? 0);
    }

    /// <summary>
    /// Converts whatever width the pipeline returned into the 16-bit samples
    /// the WAV writer takes.
    /// </summary>
    private static short[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var result = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            // Clamped rather than wrapped: a sample past full scale should be
            // loud, not inverted, which is what an overflow would sound like.
            var scaled = Math.Clamp(samples[i], -1f, 1f) * short.MaxValue;
            result[i] = (short)Math.Round(scaled);
        }
        return result;
    }

    /// <inheritdoc />
    public void ReleaseWorkingMemory()
    {
        // ONNX Runtime's arena keeps freed blocks rather than returning them,
        // which is right during a run and wrong after one. There is no public
        // API to shrink it between runs, so the collection is what hands back
        // the managed side; the arena is bounded by the session's own reuse.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// A progress sink that exists to catch something that should never happen.
    /// </summary>
    /// <remarks>
    /// The factory requires one, and by the time it is called ModelDownloader
    /// has already proved the folder complete. So any byte reported here means
    /// the library is fetching something behind our back — bypassing every
    /// guarantee §3b makes about progress, resume and checksums. Silence would
    /// hide it; this says so once.
    /// </remarks>
    private IProgress<ModelDownloadProgress> WarnIfItDownloads()
    {
        var warned = false;
        return new Progress<ModelDownloadProgress>(_ =>
        {
            if (warned) return;
            warned = true;
            _log.Log(
                "The inference library started its own download — the model folder " +
                "was expected to be complete. This bypasses the app's own download " +
                "handling; please report it.");
        });
    }

    private static Func<SessionOptions> SessionOptionsFactory(ExecutionProviderChoice choice) =>
        choice switch
        {
            ExecutionProviderChoice.Cuda => OrtSessionHelper.CreateCudaOptions,
            _ => OrtSessionHelper.CreateCpuOptions,
        };

    private void Unload()
    {
        _pipeline?.Dispose();
        _pipeline = null;
        Speakers = [];
    }

    public ValueTask DisposeAsync()
    {
        Unload();
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}
