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

using Bunyi.Core.Design;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Choosing the next codec token (spec §1, design mode).
/// </summary>
/// <remarks>
/// Ported from the export's reference script. Every step here is a place where
/// being slightly wrong still produces speech — worse pronunciation, a voice
/// that drifts, a clip that will not stop — rather than an error, so each one
/// is pinned.
/// </remarks>
public class SamplingTests
{
    /// <summary>A sampler whose randomness a test decides.</summary>
    private static TokenSampler With(params double[] draws)
    {
        var i = 0;
        return new TokenSampler(() => draws[Math.Min(i++, draws.Length - 1)]);
    }

    private static SamplingOptions Greedy => new(Temperature: 1f, TopK: 1, RepetitionPenalty: 1f);

    [Fact]
    public void Top_one_always_picks_the_likeliest()
    {
        // Whatever the draw, one candidate means one answer.
        float[] logits = [1f, 5f, 2f, 0f];

        Assert.Equal(1, With(0.0).Sample(logits, Greedy));
    }

    [Fact]
    public void The_draw_walks_the_distribution()
    {
        // Two equally likely tokens: the first half of the range gives the
        // first, the second half the second.
        Assert.Equal(0, With(0.10).Sample([0f, 0f], new SamplingOptions(1f, 0, 1f)));
        Assert.Equal(1, With(0.90).Sample([0f, 0f], new SamplingOptions(1f, 0, 1f)));
    }

    [Fact]
    public void Only_the_top_k_can_be_chosen()
    {
        // Even a draw at the very top of the range cannot reach a token that
        // top-k removed.
        float[] logits = [10f, 9f, -50f, -60f];

        for (var draw = 0.0; draw < 1.0; draw += 0.05)
        {
            var chosen = With(draw).Sample([.. logits], new SamplingOptions(1f, 2, 1f));
            Assert.True(chosen is 0 or 1, $"draw {draw} chose {chosen}");
        }
    }

    [Fact]
    public void A_top_k_wider_than_the_vocabulary_keeps_everything()
    {
        var chosen = With(0.99).Sample([0f, 0f, 0f], new SamplingOptions(1f, 99, 1f));

        Assert.Equal(2, chosen);
    }

    [Fact]
    public void The_cutoff_is_the_kth_largest_score()
    {
        Assert.Equal(3f, TokenSampler.TopKCutoff([1f, 5f, 3f, 4f], 3));
        Assert.Equal(5f, TokenSampler.TopKCutoff([1f, 5f, 3f, 4f], 1));
        Assert.Equal(float.NegativeInfinity, TokenSampler.TopKCutoff([1f, 2f], 0));
    }

    [Fact]
    public void The_cutoff_handles_repeats_without_losing_candidates()
    {
        // Three tokens tied at the top, k=2: the cutoff is that value, and more
        // than k survive. The reference has the same behaviour, and clamping to
        // exactly k here would need a tie-break the model does not specify.
        Assert.Equal(7f, TokenSampler.TopKCutoff([7f, 7f, 7f, 1f], 2));
    }

    [Fact]
    public void A_suppressed_token_is_never_chosen()
    {
        // Control tokens are suppressed by setting them to -infinity. Choosing
        // one puts a control code into the audio stream.
        float[] logits = [float.NegativeInfinity, 0f];

        for (var draw = 0.0; draw < 1.0; draw += 0.1)
        {
            Assert.Equal(1, With(draw).Sample([.. logits], new SamplingOptions(1f, 0, 1f)));
        }
    }

    [Fact]
    public void Suppressing_everything_is_an_error_rather_than_a_silent_token()
    {
        float[] logits = [float.NegativeInfinity, float.NegativeInfinity];

        Assert.Throws<InvalidOperationException>(
            () => With(0.5).Sample(logits, new SamplingOptions(1f, 0, 1f)));
    }

    [Fact]
    public void A_low_temperature_sharpens_the_distribution()
    {
        // The same logits and the same draw: colder sampling should land on the
        // likeliest token where warmer sampling does not.
        float[] logits = [0f, 1f];

        Assert.Equal(1, With(0.30).Sample([.. logits], new SamplingOptions(0.1f, 0, 1f)));
        Assert.Equal(0, With(0.30).Sample([.. logits], new SamplingOptions(2.0f, 0, 1f)));
    }

    [Fact]
    public void The_repetition_penalty_divides_a_positive_score()
    {
        float[] logits = [4f, 1f];
        TokenSampler.ApplyRepetitionPenalty(logits, 2f, [0]);

        Assert.Equal(2f, logits[0]);
        Assert.Equal(1f, logits[1]);
    }

    [Fact]
    public void The_repetition_penalty_multiplies_a_negative_score()
    {
        // The trap: dividing a negative score by a penalty above one makes it
        // LARGER, encouraging exactly what the penalty exists to discourage.
        float[] logits = [-4f];
        TokenSampler.ApplyRepetitionPenalty(logits, 2f, [0]);

        Assert.Equal(-8f, logits[0]);
    }

    [Fact]
    public void A_penalty_of_one_changes_nothing()
    {
        float[] logits = [4f, -4f];
        TokenSampler.ApplyRepetitionPenalty(logits, 1f, [0, 1]);

        Assert.Equal([4f, -4f], logits);
    }

    [Fact]
    public void The_penalty_ignores_tokens_outside_the_vocabulary()
    {
        // The code predictor's vocabulary is smaller than the talker's, and a
        // shared list of generated tokens would otherwise index past the end.
        float[] logits = [1f, 2f];

        TokenSampler.ApplyRepetitionPenalty(logits, 2f, [5, 0]);

        Assert.Equal(0.5f, logits[0]);
    }

    [Fact]
    public void Repeated_tokens_become_less_likely()
    {
        // The property the penalty is for, end to end through Sample.
        float[] logits = [3f, 3f];

        var withoutHistory = With(0.4).Sample([.. logits], new SamplingOptions(1f, 0, 1.5f));
        var withHistory = With(0.4).Sample([.. logits], new SamplingOptions(1f, 0, 1.5f), [0]);

        Assert.Equal(0, withoutHistory);
        Assert.Equal(1, withHistory);
    }

    [Fact]
    public void The_defaults_are_the_exports_own()
    {
        // From generate_onnx.py. Changing these changes how every clip sounds,
        // so they are worth stating rather than leaving as literals.
        Assert.Equal(0.9f, SamplingOptions.Default.Temperature);
        Assert.Equal(50, SamplingOptions.Default.TopK);
        Assert.Equal(1.05f, SamplingOptions.Default.RepetitionPenalty);
    }

    [Fact]
    public void Sampling_is_stable_across_a_long_run()
    {
        // A thousand draws over a realistic vocabulary, checking only that every
        // answer is a real token. The failure this catches is the cumulative
        // walk falling off the end when floating-point error leaves the target
        // just above the running total.
        var sampler = new TokenSampler(new Random(1234).NextDouble);
        var logits = new float[3072];
        var rng = new Random(99);

        for (var i = 0; i < 1000; i++)
        {
            for (var t = 0; t < logits.Length; t++) logits[t] = (float)(rng.NextDouble() * 20 - 10);

            var chosen = sampler.Sample(logits, SamplingOptions.Default);
            Assert.InRange(chosen, 0, logits.Length - 1);
        }
    }
}
