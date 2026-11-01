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

using System.IO.Compression;
using System.Text.Json.Nodes;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class SrtSpeedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bunyi-speed-tests-" + Guid.NewGuid().ToString("N"));
    private static SrtCue Cue(string start, string end) => new(Guid.NewGuid().ToString("N"), "1", "Hello.", "Hello.", start, end);
    private static GenerateRequest Request(SrtSession session) => new(TtsMode.PresetVoice, "SRT", "english", "ryan") { Srt = session };
    private static readonly TestLog Log = new();
    private static float[] Tone(int samples, double hz = 440) => Enumerable.Range(0, samples).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / 24000))).ToArray();
    private static async Task Generate(SrtSession session, int samples = 28800)
    {
        try
        {
            await session.GenerateAsync(Request(session), "test-model", (_, _, _) => Task.FromResult(
                new SynthesisResult([], 24000, 15) { RawSamples = Tone(samples) }), _ => { }, () => { }, Log, default);
        }
        catch (SrtTimingException) { } // Timing errors are the input to the fitting operation.
    }
    private string FilePath(string name) { Directory.CreateDirectory(_root); return Path.Combine(_root, name); }
    private static float[] ReadRaw(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        var result = new float[reader.BaseStream.Length / 4];
        for (var i = 0; i < result.Length; i++) result[i] = reader.ReadSingle();
        return result;
    }

    [Fact]
    public void Compression_preserves_pitch_and_exact_target_length_and_endpoints()
    {
        var source = FilePath("tone.f32"); var target = FilePath("fitted.f32");
        var input = Tone(57600);
        input[0] = 0.7f; input[^1] = -0.8f;
        using (var writer = new BinaryWriter(File.Create(source))) foreach (var value in input) writer.Write(value);
        SpeechTimeCompression.Write(source, target, input.Length, 48000, default);
        var output = ReadRaw(target);
        Assert.Equal(48000, output.Length);
        Assert.Equal(0.7f, output[0], 5);
        Assert.Equal(-0.8f, output[^1], 5);
        var interior = output.Skip(4800).Take(38400).ToArray();
        var crossings = Enumerable.Range(0, interior.Length - 1).Count(i => interior[i] <= 0 && interior[i + 1] > 0);
        Assert.InRange(crossings / (interior.Length / 24000d), 435, 445); // Resampling would raise pitch to 528 Hz.
        Assert.All(output, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void Compression_keeps_the_final_sound_instead_of_dropping_a_partial_window()
    {
        var source = FilePath("tail.f32"); var target = FilePath("tail-fit.f32");
        using (var writer = new BinaryWriter(File.Create(source)))
            for (var i = 0; i < 28800; i++) writer.Write(i >= 28544 ? 0.4f : 0f);
        SpeechTimeCompression.Write(source, target, 28800, 24000, default);
        Assert.Contains(ReadRaw(target).TakeLast(128), x => x > 0.1);
    }

    [Fact]
    public async Task Fit_resolves_overlap_without_inference_and_reset_restores_original_exactly()
    {
        var a = Cue("00:00:00,000", "00:00:00,800"); var b = Cue("00:00:01,000", "00:00:02,500");
        using var session = new SrtSession([a, b]);
        await Generate(session);
        Assert.Equal("error", session.Diagnostics[0].Status);
        var originalPreview = session.PreviewPath(a.Id)!;
        var bytes = File.ReadAllBytes(originalPreview);
        await session.SpeedUpToFitAsync(a.Id);
        Assert.Equal(1.2, session.SpeedInfo(a.Id)!.Speed, 8);
        Assert.Equal(24000, session.Diagnostics[0].SampleCount);
        Assert.Equal("warning", session.Diagnostics[0].Status); // Own end still warns; overlap is resolved.
        Assert.NotEqual(originalPreview, session.PreviewPath(a.Id));
        await session.GenerateAsync(Request(session), "test-model", (_, _, _) => throw new Exception("Do not regenerate speech"), _ => { }, () => { }, Log, default);
        session.WriteTrack(FilePath("track.wav"), Log, default);
        session.ResetSpeed(a.Id);
        Assert.Equal(originalPreview, session.PreviewPath(a.Id));
        Assert.Equal(bytes, File.ReadAllBytes(originalPreview));
        Assert.Equal("error", session.Diagnostics[0].Status);
    }

    [Fact]
    public async Task Refitting_always_uses_the_original_and_deadline_edits_do_not_automatically_refit()
    {
        var cue = Cue("00:00:00,000", "00:00:01,000");
        using var session = new SrtSession([cue]);
        await Generate(session);
        await session.SpeedUpToFitAsync(cue.Id);
        var first = File.ReadAllBytes(session.Get(cue)!.EffectiveRawPath);
        session.Update([cue with { End = "00:00:01,100" }]);
        Assert.Equal(24000, session.SpeedInfo(cue.Id)!.CurrentSamples);
        await session.SpeedUpToFitAsync(cue.Id);
        Assert.Equal(26400, session.SpeedInfo(cue.Id)!.CurrentSamples);
        session.Update([cue]);
        await session.SpeedUpToFitAsync(cue.Id);
        Assert.Equal(first, File.ReadAllBytes(session.Get(cue)!.EffectiveRawPath));
    }

    [Fact]
    public async Task Above_limit_or_cancelled_operations_preserve_the_current_take()
    {
        var cue = Cue("00:00:00,000", "00:00:00,900");
        using var session = new SrtSession([cue]);
        await Generate(session);
        var original = session.PreviewPath(cue.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SpeedUpToFitAsync(cue.Id));
        Assert.Equal(original, session.PreviewPath(cue.Id));
        session.Update([cue with { End = "00:00:00,960" }]);
        Assert.Equal(1.25, session.SpeedInfo(cue.Id)!.RequiredSpeed);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SpeedUpToFitAsync(cue.Id, cancel.Token));
        Assert.Equal(original, session.PreviewPath(cue.Id));
        await session.SpeedUpToFitAsync(cue.Id);
        Assert.Equal(1.25, session.SpeedInfo(cue.Id)!.Speed);
    }

    [Fact]
    public async Task Fit_uses_the_next_included_start_or_final_end_and_text_changes_discard_adjustments()
    {
        var a = Cue("00:00:00,000", "00:00:00,500");
        var excluded = Cue("00:00:00,600", "00:00:00,700") with { Included = false };
        var last = Cue("00:00:01,000", "00:00:02,000");
        using var session = new SrtSession([a, excluded, last]);
        await Generate(session);
        Assert.Equal(24000, session.SpeedInfo(a.Id)!.AvailableSamples);
        Assert.Equal(24000, session.SpeedInfo(last.Id)!.AvailableSamples);
        await session.SpeedUpToFitAsync(a.Id);
        session.Update([a with { Text = "New text" }, excluded, last]);
        Assert.Null(session.SpeedInfo(a.Id));
        Assert.NotNull(session.PreviewPath(last.Id));
    }

    [Fact]
    public async Task Override_is_per_cue_and_disabling_it_restores_an_over_limit_take()
    {
        var a = Cue("00:00:00,000", "00:00:00,500");
        var b = Cue("00:00:00,500", "00:00:01,000");
        using var session = new SrtSession([a, b]);
        await Generate(session);
        var original = session.PreviewPath(a.Id);
        Assert.False(session.SpeedInfo(a.Id)!.CanFit);
        session.Update([a with { AllowSpeedLimitOverride = true }, b]);
        Assert.True(session.SpeedInfo(a.Id)!.CanFit);
        Assert.False(session.SpeedInfo(b.Id)!.CanFit);
        await session.SpeedUpToFitAsync(a.Id);
        Assert.Equal(2.4, session.SpeedInfo(a.Id)!.Speed);
        Assert.Equal(12000, session.Diagnostics[0].SampleCount);
        Assert.False(session.Diagnostics[0].Status == "error");
        session.Update([a, b]);
        Assert.Equal(original, session.PreviewPath(a.Id));
        Assert.Equal(1, session.SpeedInfo(a.Id)!.Speed);
        Assert.Equal("error", session.Diagnostics[0].Status);
    }

    [Theory]
    [InlineData(2.4)]
    [InlineData(4.0)]
    [InlineData(10.0)]
    public void Explicit_high_rate_processing_remains_finite_exact_and_pitch_preserving(double speed)
    {
        var source = FilePath("high.f32"); var target = FilePath("high-fit.f32");
        var audio = Tone((int)(24000 * speed));
        using (var writer = new BinaryWriter(File.Create(source))) foreach (var sample in audio) writer.Write(sample);
        SpeechTimeCompression.Write(source, target, audio.Length, 24000, default, allowAboveLimit: true);
        var result = ReadRaw(target);
        Assert.Equal(24000, result.Length);
        Assert.All(result, sample => Assert.True(float.IsFinite(sample)));
        var middle = result.Skip(2400).Take(19200).ToArray();
        var crossings = Enumerable.Range(0, middle.Length - 1).Count(i => middle[i] <= 0 && middle[i + 1] > 0);
        Assert.InRange(crossings / 0.8, 430, 450);
    }

    [Fact]
    public async Task Save_open_preserves_high_rate_permission_and_audio_without_regeneration()
    {
        var cue = Cue("00:00:00,000", "00:00:00,500") with { AllowSpeedLimitOverride = true };
        using var session = new SrtSession([cue]);
        await Generate(session);
        await session.SpeedUpToFitAsync(cue.Id);
        var project = FilePath("override.bunyi-srt");
        await session.SaveProjectAsync(project, Request(session));
        using (var zip = ZipFile.OpenRead(project))
        using (var manifest = new StreamReader(zip.GetEntry("project.json")!.Open()))
            Assert.Equal(3, JsonNode.Parse(manifest.ReadToEnd())!["schemaVersion"]!.GetValue<int>());
        var loaded = await SrtSession.OpenProjectAsync(project);
        using var restored = loaded.Session;
        Assert.True(restored.Cues[0].AllowSpeedLimitOverride);
        Assert.Equal(2.4, restored.SpeedInfo(cue.Id)!.Speed);
        await restored.GenerateAsync(loaded.Request, "test-model", (_, _, _) => throw new Exception("Reuse the saved adjusted take"),
            _ => { }, () => { }, Log, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Projects_cannot_bypass_the_per_cue_permission_or_schema(bool downgrade)
    {
        var cue = Cue("00:00:00,000", "00:00:00,500") with { AllowSpeedLimitOverride = true };
        using var session = new SrtSession([cue]);
        await Generate(session);
        await session.SpeedUpToFitAsync(cue.Id);
        var project = FilePath("tampered-override.bunyi-srt");
        await session.SaveProjectAsync(project, Request(session));
        using (var zip = ZipFile.Open(project, ZipArchiveMode.Update))
        {
            JsonNode manifest;
            using (var input = new StreamReader(zip.GetEntry("project.json")!.Open())) manifest = JsonNode.Parse(input.ReadToEnd())!;
            if (downgrade) manifest["schemaVersion"] = 2;
            else manifest["cues"]![0]!["allowSpeedLimitOverride"] = false;
            zip.GetEntry("project.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("project.json").Open()); writer.Write(manifest.ToJsonString());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => SrtSession.OpenProjectAsync(project));
    }

    [Fact]
    public async Task Turning_off_permission_keeps_an_adjustment_that_is_within_the_normal_limit()
    {
        var cue = Cue("00:00:00,000", "00:00:01,000") with { AllowSpeedLimitOverride = true };
        using var session = new SrtSession([cue]);
        await Generate(session);
        await session.SpeedUpToFitAsync(cue.Id);
        var adjusted = session.PreviewPath(cue.Id);
        session.Update([cue with { AllowSpeedLimitOverride = false }]);
        Assert.Equal(adjusted, session.PreviewPath(cue.Id));
        Assert.Equal(1.2, session.SpeedInfo(cue.Id)!.Speed);
    }

    [Fact]
    public async Task Export_sidecar_records_effective_duration_original_duration_and_speed()
    {
        var cue = Cue("00:00:00,000", "00:00:01,000");
        using var session = new SrtSession([cue]);
        await Generate(session);
        await session.SpeedUpToFitAsync(cue.Id);
        var path = FilePath("track.srt.json");
        session.WriteSidecar(path, new OutputMetadata { Mode = "Preset voice", Text = "SRT", Language = "english", ModelRepo = "test", AppVersion = "test", Created = DateTimeOffset.UtcNow });
        var row = JsonNode.Parse(File.ReadAllText(path))!["cues"]![0]!;
        Assert.Equal(1.2, row["speed"]!.GetValue<double>());
        Assert.Equal(28800, row["originalSampleCount"]!.GetValue<long>());
        Assert.Equal(24000, row["sampleCount"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("speed")]
    [InlineData("samples")]
    [InlineData("sha256")]
    [InlineData("version")]
    public async Task Rejects_invalid_saved_adjustments_without_changing_the_current_session(string field)
    {
        var cue = Cue("00:00:00,000", "00:00:01,000");
        using var session = new SrtSession([cue]);
        await Generate(session);
        await session.SpeedUpToFitAsync(cue.Id);
        var path = FilePath("invalid.bunyi-srt");
        await session.SaveProjectAsync(path, Request(session));
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            JsonNode manifest;
            using (var input = new StreamReader(zip.GetEntry("project.json")!.Open())) manifest = JsonNode.Parse(input.ReadToEnd())!;
            var adjustment = manifest["clips"]![0]!["adjustment"]!;
            switch (field)
            {
                case "speed": adjustment["speed"] = 1.4; break;
                case "samples": adjustment["samples"] = 24001; break;
                case "sha256": adjustment["sha256"] = new string('0', 64); break;
                case "version": manifest["schemaVersion"] = 1; break;
            }
            zip.GetEntry("project.json")!.Delete();
            using var output = new StreamWriter(zip.CreateEntry("project.json").Open()); output.Write(manifest.ToJsonString());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => SrtSession.OpenProjectAsync(path));
        Assert.True(session.SpeedInfo(cue.Id)!.Adjusted);
        Assert.Equal(24000, session.SpeedInfo(cue.Id)!.CurrentSamples);
    }

    [Fact]
    public async Task Adjusted_project_preserves_original_and_adjustment_and_reset_downgrades_format()
    {
        var cue = Cue("00:00:00,000", "00:00:01,000");
        var project = FilePath("adjusted.bunyi-srt");
        using var session = new SrtSession([cue]);
        await Generate(session);
        var original = File.ReadAllBytes(session.PreviewPath(cue.Id)!);
        await session.SpeedUpToFitAsync(cue.Id);
        var adjusted = File.ReadAllBytes(session.PreviewPath(cue.Id)!);
        await session.SaveProjectAsync(project, Request(session));
        using (var zip = ZipFile.OpenRead(project))
        using (var reader = new StreamReader(zip.GetEntry("project.json")!.Open()))
            Assert.Equal(2, JsonNode.Parse(reader.ReadToEnd())!["schemaVersion"]!.GetValue<int>());
        var opened = await SrtSession.OpenProjectAsync(project);
        using var restored = opened.Session;
        Assert.Equal(1.2, restored.SpeedInfo(cue.Id)!.Speed);
        Assert.Equal(adjusted, File.ReadAllBytes(restored.PreviewPath(cue.Id)!));
        restored.ResetSpeed(cue.Id);
        Assert.Equal(original, File.ReadAllBytes(restored.PreviewPath(cue.Id)!));
        await restored.SaveProjectAsync(project, opened.Request);
        using var saved = ZipFile.OpenRead(project);
        Assert.DoesNotContain(saved.Entries, entry => entry.FullName.StartsWith("adjusted/", StringComparison.Ordinal));
        using var manifest = new StreamReader(saved.GetEntry("project.json")!.Open());
        Assert.Equal(1, JsonNode.Parse(manifest.ReadToEnd())!["schemaVersion"]!.GetValue<int>());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private sealed class TestLog : ILogSink { public void Log(string message) { } }
}
