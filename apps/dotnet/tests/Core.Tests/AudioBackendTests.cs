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

/// <summary>Checks the shipped native ABI without requiring an audio device.</summary>
/// <remarks>
/// These tests call miniaudio's native exports, unlike the old enum-formatting
/// tests which repeated SoundFlow's incorrect names. The null context below
/// deliberately produces no sound; audible ALSA verification is recorded in
/// RESEARCH-ONNX.md, not claimed by these tests.
/// </remarks>
public class AudioBackendTests
{
    [Theory]
    [InlineData(0, "WASAPI")]
    [InlineData(7, "PulseAudio")]
    [InlineData(8, "ALSA")]
    [InlineData(9, "JACK")]
    [InlineData(14, "Null")]
    public void Names_come_from_the_shipped_native_library(int id, string expected) =>
        Assert.Equal(expected, NativeAudioBackends.Name(id));

    [Fact]
    public void Native_zero_is_WASAPI_not_an_unreported_backend()
    {
        var line = NativeAudioBackends.Describe(0);
        Assert.StartsWith("Audio backend: WASAPI (enabled: ", line, StringComparison.Ordinal);
        Assert.DoesNotContain("not reported", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_backends_are_read_from_the_native_build()
    {
        var names = NativeAudioBackends.EnabledNames();
        Assert.NotEmpty(names);
        Assert.DoesNotContain("Unknown", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(OperatingSystem.IsWindows() ? "WASAPI" : "ALSA", names);
    }

    [Fact]
    public void Native_ALSA_is_not_mislabelled_PulseAudio()
    {
        Assert.StartsWith("Audio backend: ALSA (enabled: ",
            NativeAudioBackends.Describe(8), StringComparison.Ordinal);
    }

    [Fact]
    public void An_initialized_null_context_is_reported_as_null()
    {
        // Native Null is 14; the managed enum incorrectly calls that Custom.
        using var engine = new MiniAudioEngine(new[] { (MiniAudioBackend)14 });
        Assert.StartsWith("Audio backend: Null (enabled: ",
            NativeAudioBackends.Describe((int)engine.ActiveBackend), StringComparison.Ordinal);
    }
}
