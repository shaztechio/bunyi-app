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

using System.Diagnostics;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Infrastructure;
using Bunyi.Core.Models;

namespace Bunyi.Core.Engine;

/// <summary>
/// The generation engine: download, load, synthesize, write (spec §1, §2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here runs on the caller's thread.</b> §2 requires the window to
/// stay responsive for the whole run, so the work happens on the thread pool
/// and the caller only awaits it. That includes writing the WAV — on macOS the
/// equivalent step was what froze the app at the end of every generation,
/// because materialising the audio happened there rather than during inference.
/// </para>
/// <para>
/// <b>One run at a time.</b> Every entry point is gated on
/// <see cref="EngineStatus.IsBusy"/>, and nothing reports idle until abandoned
/// work has actually finished — see <see cref="FinishStoppingAsync"/>.
/// </para>
/// </remarks>
public sealed class OnnxTtsEngine : ITtsEngine
{
    private readonly ISpeechSynthesizer _synth;
    private readonly ModelDownloader _downloader;
    private readonly ILogSink _log;
    private readonly Func<TtsMode, ModelSource> _sourceFor;
    private readonly Func<TtsMode, ModelLayout> _layoutFor;
    private readonly Func<string> _modelsRoot;
    private readonly Func<string> _outputFolder;
    private readonly string _appVersion;
    private readonly TimeProvider _time;

    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<bool>> _idleWaiters = [];

    private CancellationTokenSource? _run;
    private EngineStatus _status = EngineStatus.Idle;
    private string? _loadedFolder;

    public OnnxTtsEngine(
        ISpeechSynthesizer synthesizer,
        ModelDownloader downloader,
        ILogSink log,
        Func<TtsMode, ModelSource> sourceFor,
        Func<TtsMode, ModelLayout>? layoutFor = null,
        Func<string>? modelsRoot = null,
        Func<string>? outputFolder = null,
        string appVersion = "0.1.0",
        TimeProvider? time = null)
    {
        _synth = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _sourceFor = sourceFor ?? throw new ArgumentNullException(nameof(sourceFor));
        _layoutFor = layoutFor ?? (_ => ModelLayout.PresetVoice);
        _modelsRoot = modelsRoot ?? (() => AppPaths.DefaultModelsFolder);
        _outputFolder = outputFolder ?? (() => AppPaths.Outputs);
        _appVersion = appVersion;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public EngineStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <inheritdoc />
    public event EventHandler<EngineStatus>? StatusChanged;

    /// <inheritdoc />
    public string? LastOutputPath { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> Speakers => _synth.Speakers;

    /// <inheritdoc />
    public void ClearLastOutput() => LastOutputPath = null;

    /// <inheritdoc />
    public async Task<GenerateResult> GenerateAsync(
        GenerateRequest request,
        IProgress<EngineStatus>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        CancellationTokenSource run;
        lock (_gate)
        {
            if (_status.IsBusy) throw new EngineBusyException(_status.State);
            run = _run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        // §2: starting a run clears the previous result, so nothing offers to
        // play the old audio while new audio is being made.
        ClearLastOutput();
        Publish(new EngineStatus(EngineState.Downloading), progress);

        var started = Stopwatch.StartNew();

        try
        {
            // Everything below is off the caller's thread. The window stays
            // responsive for the whole run, including the file write.
            var result = await Task.Run(async () =>
            {
                var token = run.Token;
                var mode = request.Mode;

                var folder = await _downloader.EnsureModelAsync(
                    _sourceFor(mode),
                    _layoutFor(mode),
                    _modelsRoot(),
                    // Deliberately NOT Progress<T>. That posts to the captured
                    // synchronization context, so a report can be delivered
                    // after the run has already published its final status —
                    // putting the engine back into Downloading forever and
                    // refusing every future run. Reporting inline keeps status
                    // updates ordered with respect to the work producing them.
                    new InlineProgress<DownloadProgress>(p => Publish(
                        new EngineStatus(EngineState.Downloading, p.Fraction, p.Human()), progress)),
                    token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                if (!_synth.IsLoaded || _loadedFolder != folder)
                {
                    Publish(new EngineStatus(EngineState.Loading), progress);
                    await _synth.LoadAsync(folder, token).ConfigureAwait(false);
                    _loadedFolder = folder;
                }

                token.ThrowIfCancellationRequested();
                Publish(new EngineStatus(EngineState.Generating), progress);

                var audio = await _synth.SynthesizeAsync(request, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (audio.Samples.Length == 0)
                {
                    throw new InvalidOperationException("The model produced no audio.");
                }

                var effective = request;
                if (instructWasIgnored())
                {
                    // Recorded metadata must describe what produced the audio.
                    // Keeping a style that was discarded would make the file
                    // claim a delivery it never had — and would send anyone
                    // reproducing it down the wrong path.
                    _log.Log(
                        "The style instruction was not applied, so it is not recorded " +
                        "in the file. The current preset-voice pipeline ignores one.");
                    effective = request with { Instruct = null };
                }

                var path = WriteOutput(effective, audio, folder);

                bool instructWasIgnored() =>
                    !string.IsNullOrWhiteSpace(request.Instruct)
                    && request.Mode != TtsMode.VoiceClone
                    && !_synth.SupportsInstruct;
                LastOutputPath = path;

                _log.Log(
                    $"Saved {path} — {audio.Duration.TotalSeconds:0.0}s, " +
                    $"{audio.Frames} frames, in {started.Elapsed.TotalSeconds:0.0}s.");

                return new GenerateResult(path, audio.Duration, audio.Frames, started.Elapsed);
            }, CancellationToken.None).ConfigureAwait(false);

            // Order matters. The memory a finished run was using goes back
            // after the WAV is written and BEFORE idle is published, because
            // idle is what triggers auto-play — and playback should not have to
            // compete with buffers the run no longer needs.
            Release();
            Publish(EngineStatus.Idle, progress);
            SignalIdleWaiters();

            return result;
        }
        catch (OperationCanceledException)
        {
            await FinishStoppingAsync(progress).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // The same release path as success: a run that threw allocated as
            // much as one that finished.
            _log.Log($"Generation failed: {ex}");
            Release();
            Publish(new EngineStatus(EngineState.Error, Message: ex.Message), progress);
            SignalIdleWaiters();
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_run, run)) _run = null;
            }
            run.Dispose();
        }
    }

    /// <inheritdoc />
    public void RequestStop()
    {
        CancellationTokenSource? run;
        lock (_gate)
        {
            if (!_status.IsBusy) return;
            run = _run;

            // Intent only. The run's own cancellation path decides when idle is
            // true; doing the wait here would race with it.
            _status = _status with { State = EngineState.Stopping, Detail = null };
        }

        StatusChanged?.Invoke(this, Status);
        _log.Log("Stopping the current operation.");

        try { run?.Cancel(); }
        catch (ObjectDisposedException) { /* the run already ended */ }
    }

    /// <inheritdoc />
    public Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        TaskCompletionSource<bool> waiter;
        lock (_gate)
        {
            if (!_status.IsBusy) return Task.FromResult(true);
            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _idleWaiters.Add(waiter);
        }

        return WaitAsync(waiter, timeout, ct);
    }

    private async Task<bool> WaitAsync(
        TaskCompletionSource<bool> waiter, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new CancellationTokenSource();
        var delay = Task.Delay(timeout, _time, timer.Token);

        var winner = await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false);
        if (winner == waiter.Task)
        {
            await timer.CancelAsync().ConfigureAwait(false);
            return true;
        }

        lock (_gate) _idleWaiters.Remove(waiter);
        return false;
    }

    /// <summary>
    /// Ends a cancelled run: stay busy until the engine has really let go.
    /// </summary>
    /// <remarks>
    /// The wait is real work, not a courtesy delay. Everything that starts a
    /// generation is gated on <see cref="EngineStatus.IsBusy"/>, so this window
    /// is what makes a second job against a still-loaded model impossible.
    /// Memory is released only after the abandoned work has finished — clearing
    /// it while the runtime is still allocating hands back buffers it is about
    /// to ask for again, which is churn rather than a saving.
    /// </remarks>
    private async Task FinishStoppingAsync(IProgress<EngineStatus>? progress)
    {
        Publish(new EngineStatus(EngineState.Stopping), progress);
        _log.Log("Stopping — waiting for the model to finish before reporting ready.");

        // Yield so an inference call still unwinding is not raced.
        await Task.Yield();

        Release();
        Publish(EngineStatus.Idle, progress);
        _log.Log("Stopped the current operation.");
        SignalIdleWaiters();
    }

    private void Release()
    {
        try { _synth.ReleaseWorkingMemory(); }
        catch (Exception ex)
        {
            // Reclaiming memory must not fail a run that otherwise succeeded.
            _log.Log($"Could not release working memory: {ex.Message}");
        }
    }

    private string WriteOutput(GenerateRequest request, SynthesisResult audio, string modelFolder)
    {
        var now = _time.GetLocalNow();
        var folder = AppPaths.EnsureFolder(_outputFolder());
        var path = Path.Combine(folder, WavWriter.FileNameFor(request.Mode, now));

        WavWriter.Write(path, audio.Samples, audio.SampleRate);

        var metadata = MetadataFor(request, modelFolder, now);
        if (!WavMetadata.TryWrite(path, metadata))
        {
            // Best-effort by design: a file that plays without its metadata
            // beats losing the audio to a failed tag write.
            _log.Log($"Could not write metadata into {Path.GetFileName(path)}; the audio is fine.");
        }

        return path;
    }

    private OutputMetadata MetadataFor(GenerateRequest request, string modelFolder, DateTimeOffset now)
    {
        var instruct = string.IsNullOrWhiteSpace(request.Instruct) ? null : request.Instruct;

        return new OutputMetadata
        {
            Mode = request.Mode.DisplayName(),
            Text = request.Text,
            Language = request.Language,

            // Exactly one voice field, chosen by mode: a reader must be able to
            // tell a delivery instruction from a voice description.
            Speaker = request.Mode == TtsMode.PresetVoice ? request.Speaker : null,
            Style = request.Mode == TtsMode.PresetVoice ? instruct : null,
            VoiceDescription = request.Mode == TtsMode.VoiceDesign ? instruct : null,
            ReferenceTranscript = request.Mode == TtsMode.VoiceClone
                ? (string.IsNullOrWhiteSpace(request.ReferenceTranscript) ? null : request.ReferenceTranscript)
                : null,

            ModelRepo = SourceName(request.Mode, modelFolder),
            AppVersion = _appVersion,
            Created = now,
        };
    }

    private string SourceName(TtsMode mode, string modelFolder) => _sourceFor(mode) switch
    {
        ModelSource.Repo repo => repo.Id,
        ModelSource.BaseUrl url => url.Url.AbsoluteUri,
        _ => modelFolder,
    };

    private void Publish(EngineStatus status, IProgress<EngineStatus>? progress)
    {
        lock (_gate) _status = status;
        StatusChanged?.Invoke(this, status);
        progress?.Report(status);
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that calls straight through.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> exists to marshal onto a UI thread, which is
    /// exactly wrong for state the engine owns: the hop makes delivery
    /// unordered with respect to the run. Marshalling for display is the App
    /// layer's job, and the caller's own progress object still does it.
    /// </remarks>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private void SignalIdleWaiters()
    {
        List<TaskCompletionSource<bool>> waiters;
        lock (_gate)
        {
            waiters = [.. _idleWaiters];
            _idleWaiters.Clear();
        }

        foreach (var waiter in waiters) waiter.TrySetResult(true);
    }

    public async ValueTask DisposeAsync()
    {
        RequestStop();
        await _synth.DisposeAsync().ConfigureAwait(false);
    }
}
