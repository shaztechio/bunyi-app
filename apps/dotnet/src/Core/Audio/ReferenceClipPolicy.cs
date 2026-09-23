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

namespace Bunyi.Core.Audio;

/// <summary>Choose a quiet end before the automatic reference limit (spec §4).</summary>
public static class ReferenceClipPolicy
{
    public static double AutomaticEnd(float[] samples, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (samples.Length <= sampleRate * 10) return samples.Length / (double)sampleRate;
        var window = Math.Max(1, sampleRate / 100);
        var count = Math.Min(samples.Length / window, 1000);
        var levels = new double[count];
        for (var frame = 0; frame < count; frame++)
        {
            double sum = 0;
            for (var i = frame * window; i < (frame + 1) * window; i++)
                sum += (double)samples[i] * samples[i];
            levels[frame] = Math.Sqrt(sum / window);
        }
        var threshold = Math.Clamp(levels.Max() * .03, .0001, .01);
        int? quietStart = null;
        int? selected = null;
        var heardSpeech = false;
        for (var index = 0; index < levels.Length; index++)
        {
            if (levels[index] <= threshold)
            {
                quietStart ??= index;
                if (quietStart.Value >= 200 && heardSpeech && index - quietStart.Value + 1 >= 15)
                    selected = quietStart.Value + 10;
            }
            else { heardSpeech = true; quietStart = null; }
        }
        if (selected is null)
            throw new InvalidDataException("No clear pause was found in the first 10 seconds. Choose a shorter reference recording that ends at a natural pause.");
        return selected.Value * window / (double)sampleRate;
    }
}
