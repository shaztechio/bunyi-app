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

namespace Bunyi.Core.Engine;

/// <summary>Audio produced by a synthesizer.</summary>
/// <param name="Samples">16-bit PCM, mono.</param>
/// <param name="SampleRate">Hertz. Expected to be 24 000 (spec §2).</param>
/// <param name="Frames">Codec frames generated — 12 per second of audio.</param>
public sealed record SynthesisResult(short[] Samples, int SampleRate, int Frames)
{
    public TimeSpan Duration => SampleRate > 0
        ? TimeSpan.FromSeconds((double)Samples.Length / SampleRate)
        : TimeSpan.Zero;
}

/// <summary>
/// The seam between the engine's state machine and whatever actually runs the
/// model.
/// </summary>
/// <remarks>
/// <para>
/// It exists so the state machine can be tested. The rules in §2 and §9 — one
/// run at a time, a distinct stopping state, memory released on every exit
/// path, the UI thread never doing inference — are the parts most likely to be
/// got wrong and the parts no CI machine can exercise through a real 5.88 GB
/// model. Behind this interface they are ordinary unit tests.
/// </para>
/// <para>
/// It is also where a second implementation goes. The library that drives the
/// preset-voice export covers CustomVoice only; voice design and clone need
/// their own pipelines, and they differ in how they run rather than in what the
/// engine does with the result.
/// </para>
/// </remarks>
public interface ISpeechSynthesizer : IAsyncDisposable
{
    /// <summary>Speakers the loaded model offers, empty until one is loaded.</summary>
    IReadOnlyList<string> Speakers { get; }

    /// <summary>Whether a model is resident.</summary>
    bool IsLoaded { get; }

    /// <summary>
    /// Whether the loaded model acts on a style instruction (spec §1).
    /// </summary>
    /// <remarks>
    /// Not every export does. The 0.6B CustomVoice model silently ignores one,
    /// so an app that offered the field regardless would present an input that
    /// changes nothing — and, worse, record it in the output's metadata as
    /// though it had. §1 already refuses that bargain for clone mode; the same
    /// reasoning applies here.
    /// </remarks>
    bool SupportsInstruct { get; }

    /// <summary>Loads a model from a folder, replacing any already loaded.</summary>
    Task LoadAsync(string modelFolder, CancellationToken ct);

    /// <summary>Generates audio. Long-running and CPU-bound.</summary>
    Task<SynthesisResult> SynthesizeAsync(GenerateRequest request, CancellationToken ct);

    /// <summary>
    /// Hands back the working memory a finished run was using (spec §2).
    /// </summary>
    /// <remarks>
    /// The model stays resident; only the cache goes. Called on every exit path
    /// — success, stop and error — because a run is stopped or killed most
    /// often precisely when the machine is short of memory, so releasing only
    /// on success would hold it in exactly the cases that needed it back.
    /// </remarks>
    void ReleaseWorkingMemory();
}
