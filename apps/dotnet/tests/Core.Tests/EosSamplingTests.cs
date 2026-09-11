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
public sealed class EosSamplingTests
{
    [Fact]
    public void Diagnostics_observe_filtered_probability_without_changing_selection()
    {
        float[] logits = [0, 2, 1];
        var selected = new TokenSampler(() => 0.5).Sample(logits, new(Temperature: 1, TopK: 2));
        var before = logits.ToArray();
        Assert.Equal(1, selected);
        Assert.Equal(0, TokenSampler.ProbabilityOf(logits, 0));
        Assert.InRange(TokenSampler.ProbabilityOf(logits, 2), 0.2689, 0.2690);
        Assert.Equal(before, logits);
    }
    [Fact]
    public void Eos_can_end_sampling_after_many_repeated_codes()
    {
        float[] logits = [1, 0, float.NegativeInfinity, 3];
        var selected = new TokenSampler(() => 0.999).Sample(logits, new(TopK: 2),
            Enumerable.Repeat(0, 10_000).ToArray());
        Assert.Equal(3, selected);
        Assert.True(TokenSampler.ProbabilityOf(logits, 3) > 0.5);
    }
    [Fact]
    public void Frames_agree_with_vocoder() => Assert.Equal(24_000d / 1920d, TalkerLoop.FramesPerSecond);
}
