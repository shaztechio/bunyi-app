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

using System.Text.Json;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Which system produced a clip (spec DATA-FORMATS, §9a).
/// </summary>
/// <remarks>
/// The whole value of this field is that it survives the file being moved.
/// Nearly every test here exists to stop someone replacing it with a check of
/// the running system, which would be simpler, would pass on the machine that
/// wrote the file, and would be wrong everywhere else.
/// </remarks>
public sealed class PlatformStampTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public PlatformStampTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void This_build_names_its_own_system()
    {
        var current = OutputMetadata.CurrentPlatform;

        Assert.Contains(current, new[] { "Windows", "macOS", "Linux" });

        if (OperatingSystem.IsWindows()) Assert.Equal("Windows", current);
        if (OperatingSystem.IsLinux()) Assert.Equal("Linux", current);
        if (OperatingSystem.IsMacOS()) Assert.Equal("macOS", current);
    }

    [Fact]
    public void The_name_is_the_one_people_use()
    {
        // Not "Win32NT", not "OSX", not a runtime identifier. Someone reading
        // "Made with Bunyi 0.1.0 (macOS)" should not have to decode it.
        Assert.DoesNotContain(OutputMetadata.CurrentPlatform, new[] { "Win32NT", "OSX", "Unix" });
    }

    [Fact]
    public void A_clip_says_which_system_made_it_rather_than_which_is_reading()
    {
        // The one that matters. A file made on a Mac, opened here, still says
        // macOS — otherwise the field is worse than nothing, because it looks
        // like provenance and is really a mirror.
        var output = Output(new OutputMetadata
        {
            Mode = "Preset voice",
            Text = "Hello there.",
            Language = "english",
            Speaker = "ryan",
            ModelRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
            AppVersion = "1.1.0",
            Platform = "macOS",
            Created = DateTimeOffset.UtcNow,
        });

        Assert.Contains("Made with: Bunyi 1.1.0 (macOS)", output.Details());
    }

    [Fact]
    public void A_clip_from_before_the_field_existed_simply_says_less()
    {
        // Not "(Unknown)", and not a guess. Those files are not broken; they
        // just do not record where they came from.
        var output = Output(new OutputMetadata
        {
            Mode = "Preset voice",
            Text = "Hello there.",
            Language = "english",
            Speaker = "ryan",
            ModelRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
            AppVersion = "0.1.0",
            Created = DateTimeOffset.UtcNow,
        });

        var details = output.Details();

        Assert.Contains("Made with: Bunyi 0.1.0", details);
        Assert.DoesNotContain("Unknown", details);
        Assert.DoesNotContain("()", details);
    }

    [Fact]
    public void It_is_written_into_the_file_and_read_back()
    {
        var path = Path.Combine(_root, "clip.wav");
        WavWriter.Write(path, new short[2_400]);

        WavMetadata.TryWrite(path, new OutputMetadata
        {
            Mode = "Voice clone",
            Text = "Hello there.",
            Language = "english",
            ModelRepo = "wavekat/Qwen3-TTS-0.6B-Base-ONNX",
            AppVersion = "0.1.0",
            Platform = OutputMetadata.CurrentPlatform,
            Created = DateTimeOffset.UtcNow,
        });

        var read = WavMetadata.TryRead(path);

        Assert.NotNull(read);
        Assert.Equal(OutputMetadata.CurrentPlatform, read!.Platform);
    }

    [Fact]
    public void It_is_omitted_from_the_json_rather_than_written_empty()
    {
        // Every other optional field is omitted when absent; this follows suit,
        // so a reader can tell "not recorded" from "recorded as nothing".
        var json = JsonSerializer.Serialize(new OutputMetadata
        {
            Mode = "Preset voice",
            Text = "Hello there.",
            Language = "english",
            ModelRepo = "repo",
            AppVersion = "0.1.0",
            Created = DateTimeOffset.UtcNow,
        });

        Assert.DoesNotContain("platform", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_key_is_the_one_the_spec_names()
    {
        // Both apps read each other's files, so the key is a contract.
        var json = JsonSerializer.Serialize(new OutputMetadata
        {
            Mode = "Preset voice",
            Text = "Hello there.",
            Language = "english",
            ModelRepo = "repo",
            AppVersion = "0.1.0",
            Platform = "Linux",
            Created = DateTimeOffset.UtcNow,
        });

        Assert.Contains("\"platform\":\"Linux\"", json);
    }

    private GeneratedOutput Output(OutputMetadata metadata)
    {
        var path = Path.Combine(_root, $"clip-{Guid.NewGuid():N}.wav");
        WavWriter.Write(path, new short[2_400]);
        WavMetadata.TryWrite(path, metadata);

        return GeneratedOutputs.Read(_root).Single(o => o.Path == path);
    }
}
