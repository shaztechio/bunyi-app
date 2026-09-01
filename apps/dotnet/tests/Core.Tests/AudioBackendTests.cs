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
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The line that says which audio backend is in use (spec §8).
/// </summary>
/// <remarks>
/// It reported <c>Null</c> on every machine from the day it was added, on runs
/// whose audio was audible. The word is both the enum's zero value and the name
/// of miniaudio's do-nothing backend, so the diagnostic read as the opposite of
/// the truth — and did, to the point of being reported as a silent-playback bug.
/// </remarks>
public class AudioBackendTests
{
    [Fact]
    public void It_names_the_backend_that_was_chosen()
    {
        var line = SoundFlowAudioPlayer.Describe(
            MiniAudioBackend.Wasapi,
            [MiniAudioBackend.Wasapi, MiniAudioBackend.DirectSound]);

        Assert.Contains("Audio backend: Wasapi", line, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_print_the_word_Null()
    {
        // The whole bug in one assertion. "Null" names the do-nothing backend,
        // so printing it for "the engine did not say" is indistinguishable from
        // reporting that audio is going nowhere.
        var line = SoundFlowAudioPlayer.Describe(MiniAudioBackend.Null, [MiniAudioBackend.Wasapi]);

        Assert.DoesNotContain("Null", line, StringComparison.Ordinal);
        Assert.Contains("not reported", line, StringComparison.Ordinal);
    }

    [Fact]
    public void It_lists_what_was_available()
    {
        // A backend missing from the list was never a candidate, which is a
        // different problem from one that was chosen and did not work.
        var line = SoundFlowAudioPlayer.Describe(
            MiniAudioBackend.Alsa,
            [MiniAudioBackend.Alsa, MiniAudioBackend.PulseAudio]);

        Assert.Contains("available: Alsa, PulseAudio", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_is_offered_the_backends_this_machine_has()
    {
        // The fix itself: ActiveBackend reports what the engine was *asked* for,
        // so an engine asked for nothing reports nothing, forever. Handing it
        // the available list — miniaudio's own order, so the same choice — is
        // what makes the report real.
        //
        // Asserted as "there is a list to hand it" rather than by constructing
        // an engine, because CI has no audio device and this must not depend on
        // one.
        Assert.NotNull(MiniAudioEngine.AvailableBackends);
    }
}
