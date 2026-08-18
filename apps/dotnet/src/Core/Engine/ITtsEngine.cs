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
using Bunyi.Core.Models;

namespace Bunyi.Core.Engine;

/// <summary>What the engine is doing (spec §2).</summary>
public enum EngineState
{
    Idle,
    Downloading,
    Loading,
    Transcribing,
    Generating,

    /// <summary>
    /// Stopping, but not stopped.
    /// </summary>
    /// <remarks>
    /// §2 requires this to be a state of its own. Cancelling stops the
    /// consumer; the inference engine may run to completion regardless, and it
    /// still holds the model while it does. Reporting Idle at that point would
    /// re-enable Generate and invite a second job against the same model — and
    /// switching mode would then free that model out from under work still
    /// using it. So Stop does not promise the machine has stopped computing,
    /// only that the app has stopped waiting, and it says so honestly while it
    /// waits.
    /// </remarks>
    Stopping,

    Error,
}

/// <summary>A snapshot of what the engine is doing.</summary>
public sealed record EngineStatus(
    EngineState State,
    double Progress = 0,
    string? Detail = null,
    int Frames = 0,
    string? Message = null)
{
    /// <summary>
    /// Whether work is in progress. Everything that starts a run is gated on
    /// this, which is what makes a second job impossible rather than unlikely.
    /// </summary>
    public bool IsBusy => State is not (EngineState.Idle or EngineState.Error);

    public static EngineStatus Idle { get; } = new(EngineState.Idle);
}

/// <summary>What to generate (spec §1).</summary>
public sealed record GenerateRequest(
    TtsMode Mode,
    string Text,
    string Language = "auto",
    string? Speaker = null,

    /// <summary>
    /// The style instruction for preset voice, or the voice description for
    /// voice design. Clone ignores it: the 12 Hz Base model cannot take one,
    /// and §1 forbids offering the field there.
    /// </summary>
    string? Instruct = null,

    string? ReferenceAudioPath = null,
    string? ReferenceTranscript = null);

/// <summary>The audio a run produced.</summary>
public sealed record GenerateResult(
    string OutputPath,
    TimeSpan Duration,
    int Frames,
    TimeSpan Elapsed);

/// <summary>
/// Turns text into a 24 kHz mono WAV (spec §1, §2).
/// </summary>
public interface ITtsEngine : IAsyncDisposable
{
    /// <summary>What the engine is doing now.</summary>
    EngineStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes, on any thread.</summary>
    event EventHandler<EngineStatus>? StatusChanged;

    /// <summary>The file the last successful run wrote, or null.</summary>
    string? LastOutputPath { get; }

    /// <summary>The speakers the loaded model offers (spec §1).</summary>
    IReadOnlyList<string> Speakers { get; }

    /// <summary>
    /// Forgets the previous result.
    /// </summary>
    /// <remarks>
    /// §2: starting a run clears the previous one, so nothing offers to play
    /// old audio while new audio is being made, and a cancelled run leaves
    /// nothing to play rather than falling back to the file from before. The
    /// file itself is untouched on disk.
    /// </remarks>
    void ClearLastOutput();

    /// <summary>
    /// Releases the loaded model, so its files can be removed (spec §3d).
    /// </summary>
    Task UnloadAsync();

    /// <summary>Downloads and loads what is needed, then generates.</summary>
    /// <exception cref="EngineBusyException">A run is already in progress.</exception>
    Task<GenerateResult> GenerateAsync(
        GenerateRequest request,
        IProgress<EngineStatus>? progress,
        CancellationToken ct);

    /// <summary>
    /// Asks the current run to stop (spec §2, §9).
    /// </summary>
    /// <remarks>
    /// Sets <see cref="EngineState.Stopping"/> and cancels. It never sets Idle:
    /// deciding when the engine is actually finished belongs to the run itself,
    /// which is the only thing that knows when the abandoned work ended.
    /// </remarks>
    void RequestStop();

    /// <summary>
    /// Completes when the engine is idle, or the timeout elapses.
    /// </summary>
    /// <returns>Whether it reached idle before the timeout.</returns>
    /// <remarks>
    /// What §9's busy-close waits on. The timeout exists so a window cannot be
    /// trapped open by an engine that will not finish, and the confirmation
    /// says so — a prompt promising to close "once it has stopped" would be a
    /// lie in exactly the case the timeout exists for.
    /// </remarks>
    Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Doctor found something that would stop the run (spec §11).
/// </summary>
/// <remarks>
/// Carries the whole report rather than a message, because §11 wants blockers
/// reported in a dialog and the same findings written to the log — and a
/// caller cannot rebuild a report from a sentence.
/// </remarks>
public sealed class PreflightFailedException(DoctorReport report)
    : InvalidOperationException(
        string.Join(" ", report.Blockers.Select(b => $"{b.Title}: {b.Detail}")))
{
    public DoctorReport Report { get; } = report;
}

/// <summary>A run was asked for while one was already going.</summary>
public sealed class EngineBusyException(EngineState state)
    : InvalidOperationException($"The engine is {state.ToString().ToLowerInvariant()}.")
{
    public EngineState State { get; } = state;
}
