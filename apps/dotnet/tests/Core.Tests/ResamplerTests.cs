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
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Bringing a reference clip to 24 kHz mono (spec §4).
/// </summary>
/// <remarks>
/// §4 names this as the clone bug: the model asserts 24 kHz, and the wrong rate
/// gives "distorted, wrong-pitch clones" — a failure the user cannot diagnose,
/// because the app appears to work and only the voice is wrong. So these tests
/// measure the signal rather than the sample count: a resampler can produce
/// exactly the right number of samples and still be useless.
/// </remarks>
public class ResamplerTests
{
    /// <summary>A sine wave, for measuring what survives the conversion.</summary>
    private static float[] Sine(double hz, int rate, double seconds, double amplitude = 0.5)
    {
        var samples = new float[(int)(rate * seconds)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        }

        return samples;
    }

    /// <summary>
    /// How much energy sits at one frequency, by correlating against it.
    /// </summary>
    /// <remarks>
    /// A single-bin Goertzel rather than a full transform: the questions here are
    /// all "how much of this exact tone is left", and the answer is a dot product
    /// against a sine and a cosine. Skips the first and last tenth, where the
    /// kernel overlaps the ends of the clip.
    /// </remarks>
    private static double Energy(float[] samples, double hz, int rate)
    {
        var from = samples.Length / 10;
        var to = samples.Length - from;

        double re = 0, im = 0;
        for (var i = from; i < to; i++)
        {
            var phase = 2 * Math.PI * hz * i / rate;
            re += samples[i] * Math.Cos(phase);
            im += samples[i] * Math.Sin(phase);
        }

        var n = to - from;
        return n > 0 ? 2 * Math.Sqrt(re * re + im * im) / n : 0;
    }

    [Fact]
    public void The_model_rate_is_the_one_the_spec_pins()
    {
        Assert.Equal(24_000, Resampler.ModelSampleRate);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(32_000)]
    [InlineData(22_050)]
    [InlineData(16_000)]
    [InlineData(8_000)]
    public void A_tone_keeps_its_pitch_and_its_level(int sourceRate)
    {
        // The whole point. Wrong-pitch output is what §4 warns about, and a tone
        // that arrives at a different frequency is exactly that.
        var input = Sine(1000, sourceRate, 0.5);

        var output = Resampler.ToMono(input, 1, sourceRate, 24_000);

        var level = Energy(output, 1000, 24_000);
        Assert.InRange(level, 0.45, 0.55);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(16_000)]
    public void The_clip_keeps_its_duration(int sourceRate)
    {
        // A clip that changes length has changed speed, which changes pitch.
        var input = Sine(440, sourceRate, 1.0);

        var output = Resampler.ToMono(input, 1, sourceRate, 24_000);

        Assert.InRange(output.Length, 24_000 - 50, 24_000 + 50);
    }

    [Fact]
    public void Content_above_the_output_Nyquist_is_removed_rather_than_folded()
    {
        // The reason this is a windowed sinc and not linear interpolation. At
        // 24 kHz nothing above 12 kHz can be represented; a 15 kHz tone either
        // disappears or comes back as 9 kHz. Linear interpolation gives the
        // second, and 9 kHz of metallic hiss is what a clone would learn.
        var input = Sine(15_000, 48_000, 0.5);

        var output = Resampler.ToMono(input, 1, 48_000, 24_000);

        var alias = Energy(output, 9_000, 24_000);
        Assert.True(alias < 0.02, $"15 kHz folded back to 9 kHz at level {alias:F4}");
    }

    [Fact]
    public void A_tone_just_under_the_cutoff_still_gets_through()
    {
        // The other half: a filter that removes the aliasing by removing
        // everything is no use. Voices carry real energy up here.
        var input = Sine(8_000, 48_000, 0.5);

        var output = Resampler.ToMono(input, 1, 48_000, 24_000);

        Assert.True(Energy(output, 8_000, 24_000) > 0.3,
            "8 kHz should survive a 24 kHz output");
    }

    [Fact]
    public void Nothing_changes_when_the_rate_already_matches()
    {
        // Not "close enough": a clip already at 24 kHz must pass through
        // untouched, so re-loading one costs it nothing.
        var input = Sine(1000, 24_000, 0.1);

        var output = Resampler.ToMono(input, 1, 24_000, 24_000);

        Assert.Equal(input.Length, output.Length);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Stereo_is_averaged_rather_than_halved()
    {
        // Taking the left channel would come back quiet, or silent, for a clip
        // where the voice sits on the right — and §4 is about clips people bring
        // from elsewhere.
        var stereo = new float[] { 1f, 0f, 1f, 0f, 1f, 0f };

        var mono = Resampler.Downmix(stereo, 2);

        Assert.Equal([0.5f, 0.5f, 0.5f], mono);
    }

    [Fact]
    public void A_voice_on_one_channel_only_still_arrives()
    {
        var stereo = new float[880 * 2];
        var right = Sine(1000, 44_100, 0.02, amplitude: 1.0);
        for (var i = 0; i < right.Length && i < 880; i++) stereo[i * 2 + 1] = right[i];

        var mono = Resampler.ToMono(stereo, 2, 44_100, 24_000);

        Assert.Contains(mono, s => Math.Abs(s) > 0.1f);
    }

    [Fact]
    public void Six_channels_average_too()
    {
        var frame = new float[] { 0f, 1f, 2f, 3f, 4f, 5f };

        var mono = Resampler.Downmix(frame, 6);

        Assert.Equal([2.5f], mono);
    }

    [Fact]
    public void A_constant_signal_keeps_its_level()
    {
        // Gain has to be flat. A resampler whose weights do not sum to one makes
        // every clip quieter or louder than it was, which the model then learns
        // as part of the voice.
        var input = new float[4800];
        Array.Fill(input, 0.7f);

        var output = Resampler.ToMono(input, 1, 48_000, 24_000);

        var middle = output.AsSpan(output.Length / 4, output.Length / 2);
        foreach (var sample in middle) Assert.Equal(0.7f, sample, 3);
    }

    [Fact]
    public void Silence_stays_silent()
    {
        var output = Resampler.ToMono(new float[4800], 1, 48_000, 24_000);

        Assert.All(output, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Upsampling_works_as_well_as_downsampling()
    {
        // Phone-quality clips are common, and 8 kHz has to come up to 24.
        var input = Sine(1000, 8_000, 0.5);

        var output = Resampler.ToMono(input, 1, 8_000, 24_000);

        Assert.InRange(output.Length, 12_000 - 40, 12_000 + 40);
        Assert.InRange(Energy(output, 1000, 24_000), 0.45, 0.55);
    }

    [Fact]
    public void An_empty_clip_gives_an_empty_result()
    {
        Assert.Empty(Resampler.ToMono([], 1, 48_000, 24_000));
    }

    [Fact]
    public void Nonsense_rates_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Resampler.ToMono([0f], 1, 0, 24_000));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Resampler.ToMono([0f], 1, 48_000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Resampler.ToMono([0f], 0, 48_000, 24_000));
    }

    [Fact]
    public void Every_sample_stays_finite_and_in_range()
    {
        // Ringing near a loud transient can overshoot; it must not produce
        // values a 16-bit write would wrap.
        var input = new float[2400];
        for (var i = 1200; i < input.Length; i++) input[i] = 1f;   // a hard step

        var output = Resampler.ToMono(input, 1, 48_000, 24_000);

        Assert.All(output, s => Assert.True(float.IsFinite(s), $"{s} is not finite"));
        Assert.All(output, s => Assert.InRange(s, -1.5f, 1.5f));
    }
}
