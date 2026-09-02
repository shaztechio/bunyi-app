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
/// Preset voice, behind the engine's synthesizer seam (spec §1).
/// </summary>
/// <remarks>
/// <para>
/// The third mode to run on our own pipeline, and the last: with it, one
/// inference driver serves all three, and the third-party library that used
/// to serve this one is gone (WHY-NOT-ELBRUNO.md).
/// </para>
/// <para>
/// <b>It offers the export's speakers</b>, in the export's order, once a model
/// is loaded. Until then the list is empty, which is the truth rather than a
/// guess — a picker filled from a hardcoded list would show names a differently
/// configured model does not have.
/// </para>
/// </remarks>
public sealed class PresetSpeechSynthesizer(
    ILogSink log,
    Func<string, ILogSink, IPresetPipeline>? open = null) : ISpeechSynthesizer
{
    /// <summary>The voice used when a request names none.</summary>
    /// <remarks>
    /// The same default the previous pipeline applied, so a request that never
    /// chose a speaker sounds the same as it did. Only when the export has it;
    /// otherwise the first name it lists.
    /// </remarks>
    internal const string DefaultSpeaker = "ryan";

    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));

    private readonly Func<string, ILogSink, IPresetPipeline> _open =
        open ?? ((folder, sink) => new PresetPipeline(folder, sink));

    private IPresetPipeline? _pipeline;
    private string? _folder;

    /// <inheritdoc />
    public IReadOnlyList<string> Speakers => _pipeline?.Speakers ?? [];

    /// <inheritdoc />
    public bool IsLoaded => _pipeline is not null;

    /// <inheritdoc />
    /// <remarks>
    /// True. §1 gives preset voice a style instruction and the model card
    /// documents it; the only thing that ever refused it was the previous
    /// pipeline's per-variant flag. Prompt construction is ours now, and the
    /// instruction is text conditioning in front of the sequence — the same
    /// rows design mode builds for its description.
    /// </remarks>
    public bool SupportsInstruct => true;

    /// <inheritdoc />
    public Task LoadAsync(string modelFolder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFolder);

        if (_folder == modelFolder && _pipeline is not null) return Task.CompletedTask;

        // Replacing, so the old one goes first: two pipelines resident at once
        // is the difference between fitting on a 16 GB machine and not.
        Release();

        ct.ThrowIfCancellationRequested();

        _pipeline = _open(modelFolder, _log);
        _folder = modelFolder;

        _log.Log($"Model ready. Speakers: {string.Join(", ", _pipeline.Speakers)}.");

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

        var result = _pipeline.Generate(
            new PresetRequest(
                request.Text,
                SpeakerFor(request.Speaker, _pipeline.Speakers),
                request.Instruct,
                request.Language),
            progress: frames,
            ct: ct);

        return Task.FromResult(new SynthesisResult(
            DesignSpeechSynthesizer.ToPcm16(result.Samples), 24_000, result.Frames));
    }

    /// <summary>The speaker to use: the one asked for, or the default.</summary>
    internal static string SpeakerFor(string? requested, IReadOnlyList<string> offered)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();

        foreach (var name in offered)
        {
            if (string.Equals(name, DefaultSpeaker, StringComparison.OrdinalIgnoreCase)) return name;
        }

        return offered.Count > 0 ? offered[0] : DefaultSpeaker;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to do: this pipeline holds no cache between runs. Every frame's
    /// KV cache dies with the call that made it.
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
