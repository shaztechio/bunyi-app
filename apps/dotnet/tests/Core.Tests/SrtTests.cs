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

using System.Buffers.Binary;
using System.Text.Json;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class SrtTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bunyi-srt-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestLog _log = new();
    private static SrtCue Cue(string start = "00:00:01,000", string end = "00:00:02,000", string text = "Hello.")
        => new(Guid.NewGuid().ToString("N"), "1", text, text, start, end);
    private static GenerateRequest Request(SrtSession session) => new(TtsMode.PresetVoice, "SRT", "english", "ryan") { Srt = session };
    private static Task<SynthesisResult> Audio(GenerateRequest r, CancellationToken ct, IProgress<int> progress)
    {
        ct.ThrowIfCancellationRequested(); progress.Report(12);
        return Task.FromResult(new SynthesisResult([], 24000, 12) { RawSamples = Enumerable.Repeat(0.5f, 24000).ToArray() });
    }
    private Task Generate(SrtSession s, GenerateRequest? r = null,
        Func<GenerateRequest, CancellationToken, IProgress<int>, Task<SynthesisResult>>? synth = null, CancellationToken ct = default)
        => s.GenerateAsync(r ?? Request(s), "model-v1", synth ?? Audio, _ => { }, () => { }, _log, ct);

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Parses_bom_multiline_unicode_duplicate_numbers_and_flags(string newline)
    {
        var text = "\uFEFF1\n00:00:01,000 --> 00:00:02,000\n<i>Hello</i>\n世界\n\n1\n00:00:03,000 --> 00:00:04,000\n[music] <font>soft</font>\n".Replace("\n", newline);
        var cues = SrtDocument.Parse(text);
        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello 世界", cues[0].SpokenText);
        Assert.NotEqual(cues[0].Id, cues[1].Id);
        Assert.Contains("non-speech", cues[1].Flags);
        Assert.Contains("Unknown markup", cues[1].Flags);
        Assert.True(cues[1].Included);
        Assert.Contains("<font>", cues[1].SpokenText);
    }

    [Theory]
    [InlineData(24000, "pass")]
    [InlineData(36000, "warning")]
    [InlineData(48000, "warning")]
    [InlineData(48001, "error")]
    public void Complete_sample_counts_decide_warning_overlap_and_exact_equality(long samples, string expected)
    {
        var a = Cue(); var b = Cue("00:00:03,000", "00:00:04,000");
        var result = SrtDocument.Check([a, b], id => id == a.Id ? samples : 100);
        Assert.Equal(expected, result[0].Status);
        if (samples == 48001) Assert.Equal(1 / 24000d, result[0].OverlapSeconds);
    }

    [Fact]
    public void Exclusion_removes_deadline_and_display_overlap_does_not_imply_speech_overlap()
    {
        var a = Cue(end: "00:00:04,000"); var b = Cue("00:00:02,000", "00:00:03,000");
        Assert.Equal("warning", SrtDocument.Check([a, b], _ => 100)[0].Status);
        Assert.Equal("pass", SrtDocument.Check([a, b with { Included = false }], _ => 24000)[0].Status);
    }

    [Theory]
    [InlineData("-00:00:01,000", "00:00:02,000")]
    [InlineData("00:00:01,000", "00:00:01,000")]
    [InlineData("00:60:01,000", "00:00:02,000")]
    [InlineData("99999999999999999999:00:00,000", "00:00:02,000")]
    public void Invalid_times_remain_editable_and_block_generation(string start, string end)
    {
        var cue = Cue(start, end);
        Assert.Equal(start, cue.Start);
        Assert.Throws<InvalidDataException>(() => SrtDocument.Validate([cue]));
    }

    [Fact]
    public void Rejects_structural_errors_empty_text_and_unsorted_included_cues()
    {
        Assert.Throws<InvalidDataException>(() => SrtDocument.Parse("broken"));
        Assert.Throws<InvalidDataException>(() => SrtDocument.Parse(""));
        Assert.Throws<InvalidDataException>(() => SrtDocument.Validate([Cue(text: "<i></i>")]));
        Assert.Throws<InvalidDataException>(() => SrtDocument.Validate([Cue(), Cue()]));
        Assert.Throws<InvalidDataException>(() => SrtDocument.Validate([Cue() with { Included = false }]));
    }

    [Fact]
    public async Task Rejects_non_utf8_before_generation()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "bad.srt");
        await File.WriteAllBytesAsync(path, [0xff, 0xfe, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => SrtDocument.ReadAsync(path));
    }

    [Fact]
    public async Task Cache_survives_timing_edits_exclusion_and_stop_but_not_text_or_voice_changes()
    {
        var a = Cue(); var b = Cue("00:00:03,000", "00:00:04,000");
        using var session = new SrtSession([a, b]);
        var calls = 0;
        Task<SynthesisResult> Count(GenerateRequest r, CancellationToken ct, IProgress<int> p) { calls++; return Audio(r, ct, p); }
        await Generate(session, synth: Count);
        session.Update([a with { End = "00:00:02,500" }, b with { Included = false }]);
        await Generate(session, synth: Count);
        Assert.Equal(2, calls);
        session.Update([a with { Text = "Different" }, b]);
        await Generate(session, synth: Count);
        Assert.Equal(3, calls);
        await Generate(session, Request(session) with { Speaker = "aiden" }, Count);
        Assert.Equal(5, calls);
        using var cancel = new CancellationTokenSource();
        session.Invalidate(b.Id);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generate(session,
            Request(session) with { Speaker = "aiden" }, ct: cancel.Token));
        Assert.NotNull(session.PreviewPath(a.Id));
    }

    [Fact]
    public async Task Overlap_keeps_all_takes_and_timing_correction_reuses_them()
    {
        var a = Cue(); var b = Cue("00:00:01,500", "00:00:03,000");
        using var session = new SrtSession([a, b]);
        var failure = await Assert.ThrowsAsync<SrtTimingException>(() => Generate(session));
        Assert.Equal(0.5, failure.Diagnostics[0].OverlapSeconds);
        Assert.NotNull(session.PreviewPath(b.Id));
        session.Update([a, b with { Start = "00:00:02,000" }]);
        await Generate(session, synth: (_, _, _) => throw new Exception("Should reuse clips"));
    }

    [Fact]
    public async Task Streams_silence_places_exact_samples_extends_last_cue_and_uses_uniform_gain()
    {
        Directory.CreateDirectory(_root);
        var a = Cue(end: "00:00:01,500"); var b = Cue("00:00:03,000", "00:00:03,500", "Other.");
        using var session = new SrtSession([a, b]);
        await Generate(session, synth: (r, ct, p) => Task.FromResult(new SynthesisResult([], 24000, 12)
            { RawSamples = Enumerable.Repeat(r.Text == "Hello." ? 2f : 1f, 24000).ToArray() }));
        var path = Path.Combine(_root, "track.wav");
        var result = session.WriteTrack(path, _log, default);
        Assert.Equal(96000, result.Samples);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(44 + 96000 * 2, bytes.Length);
        Assert.All(bytes.Skip(44).Take(48000), value => Assert.Equal((byte)0, value));
        Assert.Equal((short)Math.Round(0.98 * short.MaxValue), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + 25000 * 2)));
        Assert.Equal((short)Math.Round(0.49 * short.MaxValue), BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + 73000 * 2)));
        Assert.All(bytes.Skip(44 + 48000 * 2).Take(24000 * 2), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task Refuses_nonfinite_audio_and_oversized_sparse_timeline_without_publishing()
    {
        using var session = new SrtSession([Cue()]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Generate(session, synth: (_, _, _) => Task.FromResult(
            new SynthesisResult([], 24000, 1) { RawSamples = [float.NaN] })));
        Assert.False(session.HasClips);
        await Generate(session);
        session.Update([session.Cues[0] with { Start = "99:00:00,000", End = "99:00:01,000" }]);
        Assert.Throws<InvalidDataException>(() => session.WriteTrack(Path.Combine(_root, "huge.wav"), _log, default));
        Assert.False(File.Exists(Path.Combine(_root, "huge.wav")));
    }

    [Fact]
    public async Task Rejects_a_completion_if_the_script_changed_while_synthesis_was_running()
    {
        using var session = new SrtSession([Cue()]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Generate(session, synth: (r, ct, p) =>
        {
            session.Update([session.Cues[0] with { Text = "New words" }]);
            return Audio(r, ct, p);
        }));
        Assert.False(session.HasClips);
    }

    [Fact]
    public async Task Regenerating_one_cue_does_not_generate_other_missing_cues()
    {
        var a = Cue(); var b = Cue("00:00:03,000", "00:00:04,000");
        using var session = new SrtSession([a, b]);
        await Assert.ThrowsAsync<SrtTimingException>(() => Generate(session, Request(session) with { SrtCueId = a.Id }));
        Assert.NotNull(session.PreviewPath(a.Id));
        Assert.Null(session.PreviewPath(b.Id));
        await Generate(session);
        Assert.NotNull(session.PreviewPath(b.Id));
    }

    [Fact]
    public async Task Sidecar_copy_is_opaque_and_disposal_removes_working_audio()
    {
        Directory.CreateDirectory(_root);
        var session = new SrtSession([Cue()]);
        await Generate(session);
        var preview = session.PreviewPath(session.Cues[0].Id)!;
        var metadata = new OutputMetadata { Mode = "Preset voice", Text = "SRT", Language = "english", ModelRepo = "test", AppVersion = "test", Created = DateTimeOffset.UtcNow };
        session.WriteSidecar(SrtSession.SidecarPath(preview), metadata);
        var destination = Path.Combine(_root, "copy.wav");
        OutputFiles.Copy(preview, destination);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(SrtSession.SidecarPath(destination)));
        Assert.Equal("srt", json.RootElement.GetProperty("sourceKind").GetString());
        session.Dispose();
        Assert.False(File.Exists(preview));
        Assert.True(File.Exists(destination));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private sealed class TestLog : ILogSink { public void Log(string message) { } }
}
