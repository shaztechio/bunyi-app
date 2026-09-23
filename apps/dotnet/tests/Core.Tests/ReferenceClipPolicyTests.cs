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

public sealed class ReferenceClipPolicyTests
{
    [Theory]
    [InlineData(16000)]
    [InlineData(24000)]
    [InlineData(48000)]
    public void Picks_last_sustained_pause_without_cutting_through_speech(int rate)
    {
        var audio = Enumerable.Repeat(.2f, rate * 12).ToArray();
        Array.Fill(audio, .00001f, rate * 4, rate / 5);
        Array.Fill(audio, .00001f, rate * 8, rate / 5);
        Assert.Equal(8.1, ReferenceClipPolicy.AutomaticEnd(audio, rate), 6);
        Array.Fill(audio, .2f, rate * 8, rate / 5);
        Array.Fill(audio, 0f, rate * 8, rate / 10);
        Assert.Equal(4.1, ReferenceClipPolicy.AutomaticEnd(audio, rate), 6);
        Assert.Equal(3, ReferenceClipPolicy.AutomaticEnd(audio[..(rate * 3)], rate));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.2f)]
    public void Long_recordings_without_a_safe_pause_request_another_clip(float level)
    {
        var error = Assert.Throws<InvalidDataException>(() => ReferenceClipPolicy.AutomaticEnd(
            Enumerable.Repeat(level, 16000 * 12).ToArray(), 16000));
        Assert.Contains("shorter reference recording", error.Message);
    }
}
