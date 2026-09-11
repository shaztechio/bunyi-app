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

using Bunyi.Core.Engine;

namespace Bunyi.Core.Qwen;

/// <summary>
/// Bounded rolling vocoder windows for the streaming demo. The original full
/// vocoder pass still supplies the final recording. This helper owns no ONNX
/// state, allowing exact sample accounting to be checked without model files.
/// </summary>
internal sealed class RollingAudioPreview(
    Func<List<int[]>, float[]> decode,
    Action<AudioPreviewChunk> publish,
    IReadOnlyList<int[]>? reference = null)
{
    internal const int ChunkFrames = 25;
    internal const int ContextFrames = 50;
    internal const int LookaheadFrames = 5;
    internal const int SampleRate = 24_000;
    internal const int SamplesPerFrame = 1_920;
    private int _emittedFrames;
    private bool _unavailable;

    /// <summary>Flush only after EOS, never after the frame budget or cancellation.</summary>
    public void Update(IReadOnlyList<int[]> frames, bool completed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_unavailable) return;
        try { DecodePending(frames, completed, ct); }
        catch (PreviewUnavailableException ex)
        {
            _unavailable = true;
            ct.ThrowIfCancellationRequested();
            publish(new AudioPreviewChunk([], SampleRate, 0, Failure: ex.Message));
        }
    }

    private void DecodePending(IReadOnlyList<int[]> frames, bool completed, CancellationToken ct)
    {
        while (frames.Count - _emittedFrames >= ChunkFrames + LookaheadFrames
            || (completed && frames.Count > _emittedFrames))
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(ChunkFrames, frames.Count - _emittedFrames);
            var generatedStart = Math.Max(0, _emittedFrames - ContextFrames);
            var referenceCount = Math.Min(reference?.Count ?? 0,
                Math.Max(0, ContextFrames - _emittedFrames));
            var end = Math.Min(frames.Count, _emittedFrames + count + LookaheadFrames);
            var window = new List<int[]>(referenceCount + end - generatedStart);
            if (referenceCount > 0)
                for (var i = reference!.Count - referenceCount; i < reference.Count; i++) window.Add(reference[i]);
            for (var i = generatedStart; i < end; i++) window.Add(frames[i]);

            var decoded = decode(window);
            // Any nonfinite model output invalidates the entire take, including
            // samples in context/lookahead. It is not a recoverable playback error.
            foreach (var sample in decoded)
                if (!float.IsFinite(sample)) throw new InvalidDataException("The model produced invalid preview audio.");
            ct.ThrowIfCancellationRequested();
            // All three supported exports have a measured 12.5 Hz codec and
            // 24 kHz output. Refuse an incompatible layout rather than guessing
            // a cut which could leak reference audio or duplicate a boundary.
            if (decoded.Length != checked(window.Count * SamplesPerFrame))
                throw new PreviewUnavailableException("This model's audio layout does not support the streaming preview.");
            var skip = (referenceCount + _emittedFrames - generatedStart) * SamplesPerFrame;
            var samples = decoded.AsSpan(skip, count * SamplesPerFrame).ToArray();
            publish(new AudioPreviewChunk(samples, SampleRate, (long)_emittedFrames * SamplesPerFrame));
            _emittedFrames += count;
        }
    }
}
/// <summary>Only known preview incompatibilities may fall back to final-only output.</summary>
internal sealed class PreviewUnavailableException(string message) : Exception(message);