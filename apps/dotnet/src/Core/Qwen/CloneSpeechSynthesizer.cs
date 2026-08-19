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

namespace Bunyi.Core.Qwen;

/// <summary>
/// Voice clone, behind the engine's synthesizer seam (spec §1, §4).
/// </summary>
/// <remarks>
/// <para>
/// The same seam design mode uses, for the same reason: the engine's state
/// machine, downloads, metadata and stop behaviour are mode-agnostic and already
/// work.
/// </para>
/// <para>
/// This one has more to refuse than the others, because clone mode is the one
/// where a missing input produces confident nonsense rather than an error. No
/// recording, an unreadable recording, or no transcript each stop the run with
/// something a person can act on.
/// </para>
/// </remarks>
public sealed class CloneSpeechSynthesizer(
    ILogSink log,
    string variant = "int4",
    Func<string, string, ILogSink, IClonePipeline>? open = null) : ISpeechSynthesizer
{
    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));

    private readonly Func<string, string, ILogSink, IClonePipeline> _open =
        open ?? ((folder, precision, sink) => new ClonePipeline(folder, precision, sink));

    private IClonePipeline? _pipeline;
    private string? _folder;

    /// <inheritdoc />
    /// <remarks>
    /// Always empty. The voice comes from the recording, and a picker offering
    /// speakers as well would be offering two answers to one question.
    /// </remarks>
    public IReadOnlyList<string> Speakers => [];

    /// <inheritdoc />
    public bool IsLoaded => _pipeline is not null;

    /// <inheritdoc />
    /// <remarks>
    /// False, and §1 requires it: the Base model takes no style instruction, so
    /// the window must not offer a field for one. A described style that
    /// silently did nothing is exactly the trap this mode is most prone to.
    /// </remarks>
    public bool SupportsInstruct => false;

    /// <inheritdoc />
    public Task LoadAsync(string modelFolder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFolder);

        if (_folder == modelFolder && _pipeline is not null) return Task.CompletedTask;

        // Replacing, so the old one goes first — the same reasoning as design
        // mode: two pipelines resident at once is the difference between
        // fitting on a smaller machine and not.
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
    public Task<SynthesisResult> SynthesizeAsync(GenerateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_pipeline is null)
        {
            throw new InvalidOperationException("No model is loaded.");
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceAudioPath))
        {
            throw new InvalidOperationException(
                "Choose a recording of the voice to clone first.");
        }

        if (!File.Exists(request.ReferenceAudioPath))
        {
            throw new InvalidOperationException(
                $"That recording is no longer where it was: "
                + $"{Path.GetFileName(request.ReferenceAudioPath)}. Choose it again.");
        }

        // §4 calls the transcript effectively mandatory, and the caller is meant
        // to have filled it in by listening. Reaching here without one means
        // that did not happen, and generating anyway would return fluent audio
        // saying something else.
        if (string.IsNullOrWhiteSpace(request.ReferenceTranscript))
        {
            throw new InvalidOperationException(
                "The transcript is empty, so there is no way to line the recording "
                + "up with its words. Type what the recording says.");
        }

        var reference = ReferenceAudio.Load(
            request.ReferenceAudioPath, MelSpectrogram.SampleRate, _log);

        ct.ThrowIfCancellationRequested();

        var result = _pipeline.Generate(
            new CloneRequest(request.Text, request.ReferenceTranscript, request.Language),
            reference,
            ct: ct);

        return Task.FromResult(new SynthesisResult(
            DesignSpeechSynthesizer.ToPcm16(result.Samples), 24_000, result.Frames));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to do, as with design mode: no cache is held between runs.
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
