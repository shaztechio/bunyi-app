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

using System.Buffers.Binary;

namespace Bunyi.Core.Audio;

/// <summary>Offline mono WSOLA: overlapping waveform-aligned grains, fixed endpoints,
/// unchanged sample rate. Bounded buffers regardless of clip length.</summary>
internal static class SpeechTimeCompression
{
    public static float Write(string source, string destination, long samples, long target, CancellationToken ct, bool allowAboveLimit = false)
    {
        if (target < 128 || target >= samples || (!allowAboveLimit && samples * 4 > target * 5))
            throw new InvalidOperationException("Pitch-preserving fitting needs a valid window; speeds above 1.25× require this cue's override.");
        using var input = File.OpenRead(source);
        if (input.Length != checked(samples * 4)) throw new InvalidDataException("The original cue audio has the wrong length.");
        using var writer = new BinaryWriter(File.Create(destination));
        var window = (int)Math.Min(1024, target / 2) & ~1;
        var search = window / 8;
        // Smaller hops keep neighboring input grains overlapping at higher
        // explicitly requested rates. Extremely high rates remain lossy and
        // cannot guarantee intelligible speech; the UI warns before use.
        var hop = Math.Max(1, Math.Min(window / 4, (int)((window - 2 * search - 1) / (samples / (double)target))));
        var sum = new float[window];
        var weights = new float[window];
        var taper = new float[window];
        for (var i = 0; i < window; i++) taper[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / window));
        var sourceBuffer = new float[window + search * 2];
        var bytes = new byte[sourceBuffer.Length * 4];
        long outputPosition = 0;
        long previousSource = -1;
        float peak = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var last = outputPosition == target - window;
            var nominal = outputPosition * (samples - window) / (target - window);
            // Adjacent source windows overlap even at 1.25x and opposite search
            // extremes. Anchor the first and last windows to the actual ends;
            // never discard a partial final window as a streaming flush can do.
            var low = outputPosition == 0 || last ? nominal : Math.Max(previousSource + 1, nominal - search);
            var high = outputPosition == 0 || last ? nominal : Math.Min(samples - window - 1,
                Math.Min(nominal + search, previousSource + window - 1));
            if (high < low) low = high = Math.Clamp(nominal, 0, samples - window);
            var count = checked((int)(high - low + window));
            input.Position = low * 4;
            input.ReadExactly(bytes.AsSpan(0, count * 4));
            for (var i = 0; i < count; i++)
            {
                sourceBuffer[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
                if (!float.IsFinite(sourceBuffer[i])) throw new InvalidDataException("The original cue contains invalid samples.");
            }
            var best = Math.Clamp(nominal, low, high);
            var bestScore = Score((int)(best - low));
            if (outputPosition > 0 && !last)
            {
                for (var candidate = low; candidate <= high; candidate += 4)
                {
                    var score = Score((int)(candidate - low));
                    if (score > bestScore + 1e-9) { bestScore = score; best = candidate; }
                }
                var coarse = best;
                for (var candidate = Math.Max(low, coarse - 3); candidate <= Math.Min(high, coarse + 3); candidate++)
                {
                    var score = Score((int)(candidate - low));
                    if (score > bestScore + 1e-9) { bestScore = score; best = candidate; }
                }
            }
            var offset = (int)(best - low);
            for (var i = 0; i < window; i++) { sum[i] += sourceBuffer[offset + i] * taper[i]; weights[i] += taper[i]; }
            previousSource = best;
            var next = Math.Min(outputPosition + hop, target - window);
            var emit = last ? window : (int)(next - outputPosition);
            for (var i = 0; i < emit; i++)
            {
                var sample = sum[i] / weights[i];
                if (!float.IsFinite(sample)) throw new InvalidDataException("Time compression produced invalid audio. The original take is unchanged.");
                writer.Write(sample);
                peak = Math.Max(peak, Math.Abs(sample));
            }
            if (last) break;
            Array.Copy(sum, emit, sum, 0, window - emit);
            Array.Copy(weights, emit, weights, 0, window - emit);
            Array.Clear(sum, window - emit, emit);
            Array.Clear(weights, window - emit, emit);
            outputPosition = next;
        }
        return peak;

        double Score(int candidate)
        {
            double dot = 0, a2 = 0, b2 = 0;
            for (var i = 0; i < window - hop; i += 4)
            {
                if (weights[i] <= 0) continue;
                var a = sum[i] / weights[i];
                var b = sourceBuffer[candidate + i];
                dot += a * (double)b; a2 += a * (double)a; b2 += b * (double)b;
            }
            return a2 > 1e-16 && b2 > 1e-16 ? dot / Math.Sqrt(a2 * b2) : -1;
        }
    }
}
