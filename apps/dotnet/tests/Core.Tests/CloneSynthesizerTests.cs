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

using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Clone mode behind the engine's synthesizer seam (spec §1, §4).
/// </summary>
/// <remarks>
/// A fake pipeline throughout: what is under test is what this refuses and what
/// it passes along, none of which needs a 3.86 GB export to be wrong. Clone is
/// the mode where a missing input produces confident nonsense rather than an
/// error, so most of this is about the refusing.
/// </remarks>
public sealed class CloneSynthesizerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public CloneSynthesizerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- What the window is told ----

    [Fact]
    public void It_offers_no_speakers()
    {
        // The voice comes from the recording. A picker as well would be two
        // answers to one question.
        Assert.Empty(new CloneSpeechSynthesizer(new RecordingLog()).Speakers);
    }

    [Fact]
    public void It_takes_no_style_instruction()
    {
        // §1 forbids the field here: the Base model cannot act on one, and a
        // style that silently did nothing is this mode's characteristic trap.
        Assert.False(new CloneSpeechSynthesizer(new RecordingLog()).SupportsInstruct);
    }

    // ---- What it refuses ----

    [Fact]
    public async Task Without_a_model_loaded_it_says_so()
    {
        await using var synth = new CloneSpeechSynthesizer(new RecordingLog());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => synth.SynthesizeAsync(Request(), default));

        Assert.Contains("No model is loaded", error.Message);
    }

    [Fact]
    public async Task Without_a_recording_it_asks_for_one()
    {
        await using var synth = await LoadedAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => synth.SynthesizeAsync(Request(audio: null), default));

        Assert.Contains("Choose a recording", error.Message);
    }

    [Fact]
    public async Task A_recording_that_has_since_moved_is_named()
    {
        // Between choosing a file and pressing Generate, it can be renamed,
        // unplugged or deleted. Saying which one beats a decode error.
        await using var synth = await LoadedAsync();
        var gone = Path.Combine(_root, "moved-away.wav");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => synth.SynthesizeAsync(Request(audio: gone), default));

        Assert.Contains("moved-away.wav", error.Message);
    }

    [Fact]
    public async Task Without_a_transcript_it_refuses_rather_than_guessing()
    {
        // §4 makes the transcript effectively mandatory. Generating anyway
        // returns fluent audio saying something else, which is worse than an
        // error because it looks like success.
        await using var synth = await LoadedAsync();
        var clip = MakeClip();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => synth.SynthesizeAsync(Request(audio: clip, transcript: "  "), default));

        Assert.Contains("Type what the recording says", error.Message);
    }

    [Fact]
    public async Task It_refuses_before_reading_the_recording()
    {
        // Decoding is the slow part, and none of it helps if the transcript is
        // missing anyway.
        var pipeline = new FakePipeline();
        await using var synth = await LoadedAsync(pipeline);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => synth.SynthesizeAsync(
                Request(audio: Path.Combine(_root, "nope.wav"), transcript: ""), default));

        Assert.Null(pipeline.LastRequest);
    }

    // ---- What it passes along ----

    [Fact]
    public async Task The_text_transcript_and_language_all_reach_the_pipeline()
    {
        var pipeline = new FakePipeline();
        await using var synth = await LoadedAsync(pipeline);
        var clip = MakeClip();

        await synth.SynthesizeAsync(
            new GenerateRequest(
                TtsMode.VoiceClone,
                "Say this instead.",
                "english",
                ReferenceAudioPath: clip,
                ReferenceTranscript: "What the clip says."),
            default);

        Assert.NotNull(pipeline.LastRequest);
        Assert.Equal("Say this instead.", pipeline.LastRequest!.Text);
        Assert.Equal("What the clip says.", pipeline.LastRequest.ReferenceTranscript);
        Assert.Equal("english", pipeline.LastRequest.Language);
    }

    [Fact]
    public async Task The_recording_reaches_it_as_samples_at_the_model_rate()
    {
        var pipeline = new FakePipeline();
        await using var synth = await LoadedAsync(pipeline);

        await synth.SynthesizeAsync(Request(audio: MakeClip(seconds: 2)), default);

        // Two seconds at 24 kHz, whatever rate the file was written at.
        Assert.InRange(pipeline.LastSampleCount, 47_000, 49_000);
    }

    [Fact]
    public async Task Loading_the_same_folder_twice_does_not_reopen_it()
    {
        // Every open maps gigabytes. Doing it again because the mode was
        // switched away and back is a pause the user cannot explain.
        var opened = 0;
        await using var synth = new CloneSpeechSynthesizer(
            new RecordingLog(), "int4", (_, _, _) => { opened++; return new FakePipeline(); });

        await synth.LoadAsync(_root, default);
        await synth.LoadAsync(_root, default);

        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task A_different_folder_releases_the_first()
    {
        // Two of these resident at once is the difference between fitting on a
        // smaller machine and not.
        var opened = new List<FakePipeline>();
        await using var synth = new CloneSpeechSynthesizer(
            new RecordingLog(), "int4",
            (_, _, _) => { var p = new FakePipeline(); opened.Add(p); return p; });

        await synth.LoadAsync(_root, default);
        await synth.LoadAsync(Path.GetTempPath(), default);

        Assert.True(opened[0].Disposed, "the first pipeline was left resident");
    }

    [Fact]
    public async Task Unloading_releases_it()
    {
        // §3d: a model being deleted has to be evicted first, and on Windows the
        // delete fails outright otherwise.
        var pipeline = new FakePipeline();
        await using var synth = await LoadedAsync(pipeline);

        await synth.UnloadAsync();

        Assert.True(pipeline.Disposed);
        Assert.False(synth.IsLoaded);
    }

    // ---- Fixtures ----

    private GenerateRequest Request(
        string? audio = "set-by-caller", string transcript = "What the clip says.") =>
        new(TtsMode.VoiceClone,
            "Speak these words.",
            ReferenceAudioPath: audio == "set-by-caller" ? MakeClip() : audio,
            ReferenceTranscript: transcript);

    private async Task<CloneSpeechSynthesizer> LoadedAsync(FakePipeline? pipeline = null)
    {
        var synth = new CloneSpeechSynthesizer(
            new RecordingLog(), "int4", (_, _, _) => pipeline ?? new FakePipeline());

        await synth.LoadAsync(_root, default);
        return synth;
    }

    /// <summary>A real, readable WAV — the decoder is not being faked.</summary>
    private string MakeClip(int seconds = 1)
    {
        var path = Path.Combine(_root, $"clip-{Guid.NewGuid():N}.wav");
        var count = 24_000 * seconds;
        var pcm = new short[count];

        for (var i = 0; i < count; i++)
        {
            pcm[i] = (short)(Math.Sin(i * 0.05) * 8000);
        }

        Bunyi.Core.Audio.WavWriter.Write(path, pcm, 24_000);
        return path;
    }

    private sealed class FakePipeline : IClonePipeline
    {
        public CloneRequest? LastRequest { get; private set; }

        public int LastSampleCount { get; private set; }

        public bool Disposed { get; private set; }

        public SamplingOptions DefaultSampling => SamplingOptions.Default;

        public SpeechResult Generate(
            CloneRequest request,
            ReadOnlySpan<float> reference,
            SamplingOptions? options = null,
            IProgress<int>? progress = null,
            int? maxFrames = null,
            CancellationToken ct = default)
        {
            LastRequest = request;
            LastSampleCount = reference.Length;

            return new SpeechResult(new float[2400], [[0, 0]]);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLog : ILogSink
    {
        public void Log(string message) { }
    }
}
