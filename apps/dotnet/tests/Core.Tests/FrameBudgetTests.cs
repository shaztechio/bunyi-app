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

using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// How long a run is allowed to go on for.
/// </summary>
/// <remarks>
/// Reported from using the app: a clone that ran for minutes with nothing to
/// show. It was not hung — sampling is random, and a draw occasionally never
/// produces the token that ends a run. The export's own cap is 8192 frames,
/// which is eleven minutes of audio and about half an hour of CPU.
/// </remarks>
public sealed class FrameBudgetTests
{
    private const int HardCap = 8192;

    [Fact]
    public void A_short_sentence_gets_seconds_rather_than_half_an_hour()
    {
        // The case that was reported. Twenty-odd characters should not be able
        // to run for eleven minutes of audio.
        var frames = TalkerLoop.FrameBudget("Hello how are you today.", HardCap);

        Assert.True(frames < 500, $"a short sentence may still run to {frames} frames");
        Assert.True(frames / TalkerLoop.FramesPerSecond <= 40);
    }

    [Fact]
    public void It_is_generous_enough_for_a_faithful_reading()
    {
        // A real run of that sentence made 45 frames. The budget has to sit
        // well above what the model legitimately produces, or it truncates
        // speech that was going fine.
        Assert.True(TalkerLoop.FrameBudget("Hello how are you today.", HardCap) > 45 * 2);
    }

    [Fact]
    public void Longer_text_gets_proportionally_longer()
    {
        var shortish = TalkerLoop.FrameBudget(new string('a', 100), HardCap);
        var longer = TalkerLoop.FrameBudget(new string('a', 1000), HardCap);

        Assert.True(longer > shortish * 5, "the budget does not follow the text");
    }

    [Fact]
    public void A_paragraph_is_never_cut_short_by_this()
    {
        // 1,200 characters is around two minutes of speech. The budget must
        // clear that comfortably, or the guard becomes the bug.
        var frames = TalkerLoop.FrameBudget(new string('a', 1_200), HardCap);

        Assert.True(frames / TalkerLoop.FramesPerSecond > 120);
    }

    [Fact]
    public void Very_short_text_still_gets_room()
    {
        // "Hi." is three characters and still needs more than a fraction of a
        // second, because the model pads and pauses.
        var frames = TalkerLoop.FrameBudget("Hi.", HardCap);

        Assert.True(frames / TalkerLoop.FramesPerSecond >= 30);
    }

    [Fact]
    public void The_export_cap_is_still_the_ceiling()
    {
        // Whatever the text, the model's own limit wins.
        Assert.Equal(HardCap, TalkerLoop.FrameBudget(new string('a', 500_000), HardCap));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_text_still_gets_a_workable_budget(string? text)
    {
        // Nothing should reach here with empty text, but a budget of zero would
        // turn that into "the model produced no audio", which points at the
        // wrong thing entirely.
        Assert.True(TalkerLoop.FrameBudget(text, HardCap) > 0);
    }

    [Fact]
    public void Frames_and_seconds_agree_with_the_vocoder()
    {
        // 1920 samples a frame at 24 kHz.
        Assert.Equal(24_000d / 1920d, TalkerLoop.FramesPerSecond);
    }
}
