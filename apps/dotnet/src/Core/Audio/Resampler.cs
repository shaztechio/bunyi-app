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

/// <summary>
/// Brings a reference clip to the rate and channel count the model asserts
/// (spec §4).
/// </summary>
/// <remarks>
/// <para>
/// §4 is unusually direct about why this exists: the model asserts 24 kHz mono,
/// and feeding it 44.1 or 48 kHz "produces distorted, wrong-pitch clones". That
/// is the failure a user cannot diagnose — the app appears to work and the voice
/// is simply wrong — so the guarantee is written here and tested, rather than
/// taken from a dependency. macOS hands the job to AVAudioConverter; there is no
/// equivalent to borrow on Windows and Linux.
/// </para>
/// <para>
/// The method is a windowed-sinc interpolation, not a linear one. Linear
/// interpolation is a poor low-pass filter, so downsampling 48 kHz to 24 kHz
/// folds everything above 12 kHz back down into the audible band as aliasing —
/// which sounds like a metallic edge on sibilants and is exactly the kind of
/// artefact a clone would learn. The kernel's cutoff is lowered by the
/// downsampling ratio so that content the output cannot represent is removed
/// before it can fold.
/// </para>
/// </remarks>
public static class Resampler
{
    /// <summary>The rate the models require (spec §2, §4).</summary>
    public const int ModelSampleRate = 24_000;

    /// <summary>
    /// Half the width of the interpolation kernel, in input samples.
    /// </summary>
    /// <remarks>
    /// Sixteen either side. A sinc truncated too early passes ripple through the
    /// stop band, and one truncated late costs time nobody notices: this runs
    /// once on a clip of a few seconds, not per frame of a generation.
    /// </remarks>
    private const int HalfWidth = 16;

    /// <summary>
    /// Converts interleaved samples to mono at <paramref name="targetRate" />.
    /// </summary>
    /// <param name="samples">Interleaved frames, any channel count.</param>
    /// <param name="channels">Channels in <paramref name="samples" />.</param>
    /// <param name="sourceRate">The rate the samples are at.</param>
    /// <param name="targetRate">The rate wanted, normally 24 kHz.</param>
    public static float[] ToMono(
        ReadOnlySpan<float> samples, int channels, int sourceRate, int targetRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);

        var mono = Downmix(samples, channels);
        return Resample(mono, sourceRate, targetRate);
    }

    /// <summary>
    /// Averages the channels together.
    /// </summary>
    /// <remarks>
    /// Averaged rather than taking the left channel. A clip recorded with the
    /// voice panned, or one where a second microphone carries most of the
    /// signal, would otherwise come back quiet or empty — and §4 is about clips
    /// people bring from elsewhere, not ones this app recorded.
    /// </remarks>
    internal static float[] Downmix(ReadOnlySpan<float> samples, int channels)
    {
        if (channels == 1) return samples.ToArray();

        var frames = samples.Length / channels;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += samples[frame * channels + channel];
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    /// <summary>Changes the sample rate of mono audio.</summary>
    internal static float[] Resample(ReadOnlySpan<float> mono, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return mono.ToArray();
        if (mono.Length == 0) return [];

        var ratio = (double)targetRate / sourceRate;
        var length = (int)Math.Round(mono.Length * ratio);
        if (length <= 0) return [];

        // Only band-limit when going down. Upsampling invents no frequencies
        // above the source's own Nyquist, so a cutoff below 1 would just dull it.
        var cutoff = Math.Min(1.0, ratio);

        var output = new float[length];
        var step = 1.0 / ratio;   // input samples per output sample

        for (var i = 0; i < length; i++)
        {
            var centre = i * step;
            var nearest = (int)Math.Floor(centre);

            var sum = 0.0;
            var weight = 0.0;

            for (var tap = -HalfWidth; tap <= HalfWidth; tap++)
            {
                var index = nearest + tap;
                if (index < 0 || index >= mono.Length) continue;

                var w = Kernel((index - centre) * cutoff);
                sum += mono[index] * w;
                weight += w;
            }

            // Normalised by the weights actually used, so the first and last
            // few samples — where the kernel hangs off the end of the clip —
            // keep their level instead of fading.
            output[i] = weight > 0 ? (float)(sum / weight) : 0f;
        }

        return output;
    }

    /// <summary>
    /// A sinc, tapered by a Blackman window.
    /// </summary>
    /// <remarks>
    /// The window is what makes truncation acceptable: a bare sinc chopped at 16
    /// samples leaks badly around the cutoff. Blackman trades a slightly wider
    /// transition for a stop band deep enough that the aliasing this exists to
    /// prevent stays inaudible.
    /// </remarks>
    private static double Kernel(double x)
    {
        var t = Math.Abs(x);
        if (t >= HalfWidth) return 0;
        if (t < 1e-9) return 1;

        var sinc = Math.Sin(Math.PI * x) / (Math.PI * x);

        // Blackman over the kernel's full width, centred on zero.
        var n = (x + HalfWidth) / (2.0 * HalfWidth);
        var window = 0.42
                     - 0.5 * Math.Cos(2 * Math.PI * n)
                     + 0.08 * Math.Cos(4 * Math.PI * n);

        return sinc * window;
    }
}
