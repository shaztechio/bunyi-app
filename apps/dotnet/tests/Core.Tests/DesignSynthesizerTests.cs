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
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Voice design behind the engine's synthesizer seam (spec §1).
/// </summary>
/// <remarks>
/// Every case here runs without the 5.85 GB export, because none of what this
/// class does needs a model to be wrong: converting samples, refusing when
/// nothing is loaded, and releasing one pipeline before opening another.
/// </remarks>
public class DesignSynthesizerTests
{
    /// <summary>A pipeline that records what it was asked and answers cheaply.</summary>
    private sealed class FakePipeline : IDesignPipeline
    {
        public DesignRequest? Asked { get; private set; }
        public bool Disposed { get; private set; }
        public float[] Samples { get; set; } = [0f, 0.5f, -0.5f];
        public Func<DesignRequest, DesignResult>? Answer { get; set; }

        public SamplingOptions DefaultSampling => SamplingOptions.Default;

        public DesignResult Generate(
            DesignRequest request, SamplingOptions? options = null,
            IProgress<int>? progress = null, int? maxFrames = null,
            CancellationToken ct = default)
        {
            Asked = request;
            ct.ThrowIfCancellationRequested();

            return Answer?.Invoke(request)
                ?? new DesignResult(Samples, [new int[16], new int[16]]);
        }

        public void Dispose() => Disposed = true;
    }

    private static (DesignSpeechSynthesizer Synth, List<FakePipeline> Opened) New()
    {
        var opened = new List<FakePipeline>();

        var synth = new DesignSpeechSynthesizer(
            new RecordingLog(), "int4",
            (_, _, _) =>
            {
                var pipeline = new FakePipeline();
                opened.Add(pipeline);
                return pipeline;
            });

        return (synth, opened);
    }

    [Fact]
    public void It_offers_no_speakers_because_the_export_has_none()
    {
        // §1 gives design mode a description instead. Reporting speakers it does
        // not have is what would let the window show a picker that changes
        // nothing — the trap §1 refuses for clone mode's emotion field.
        var (synth, _) = New();

        Assert.Empty(synth.Speakers);
    }

    [Fact]
    public void It_acts_on_the_description()
    {
        // Unambiguously, here: the description is not a decoration on a chosen
        // voice, it is the only thing that decides what the voice is.
        var (synth, _) = New();

        Assert.True(synth.SupportsInstruct);
    }

    [Fact]
    public async Task Nothing_is_loaded_to_begin_with()
    {
        var (synth, opened) = New();

        Assert.False(synth.IsLoaded);
        Assert.Empty(opened);

        await synth.DisposeAsync();
    }

    [Fact]
    public async Task Loading_opens_the_model_in_that_folder()
    {
        var (synth, opened) = New();

        await synth.LoadAsync(@"C:\models\design", default);

        Assert.True(synth.IsLoaded);
        Assert.Single(opened);
    }

    [Fact]
    public async Task Loading_the_same_folder_again_keeps_what_is_open()
    {
        // The engine calls this before every generation; re-opening 3.8 GB of
        // sessions each time would make the second run as slow as the first.
        var (synth, opened) = New();

        await synth.LoadAsync(@"C:\models\design", default);
        await synth.LoadAsync(@"C:\models\design", default);

        Assert.Single(opened);
    }

    [Fact]
    public async Task Loading_a_different_folder_releases_the_old_one_first()
    {
        // Two pipelines resident at once is 7.6 GB, which is the difference
        // between fitting on a 16 GB machine and not.
        var (synth, opened) = New();

        await synth.LoadAsync(@"C:\models\one", default);
        await synth.LoadAsync(@"C:\models\two", default);

        Assert.Equal(2, opened.Count);
        Assert.True(opened[0].Disposed, "the first pipeline was still holding its weights");
        Assert.False(opened[1].Disposed);
    }

    [Fact]
    public async Task Unloading_releases_the_model_entirely()
    {
        // §3d: a model being deleted must be evicted first, and on Windows a
        // loaded session holds its weights open so the delete fails outright.
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\design", default);

        await synth.UnloadAsync();

        Assert.False(synth.IsLoaded);
        Assert.True(opened[0].Disposed);
    }

    [Fact]
    public async Task Generating_before_loading_is_an_error_rather_than_silence()
    {
        var (synth, _) = New();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => synth.SynthesizeAsync(new GenerateRequest(TtsMode.VoiceDesign, "hello"), default));
    }

    [Fact]
    public async Task The_request_reaches_the_pipeline_intact()
    {
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\design", default);

        await synth.SynthesizeAsync(
            new GenerateRequest(TtsMode.VoiceDesign, "Hello there.",
                Language: "english", Instruct: "A warm female voice"),
            default);

        var asked = opened[0].Asked!;
        Assert.Equal("Hello there.", asked.Text);
        Assert.Equal("A warm female voice", asked.Instruction);
        Assert.Equal("english", asked.Language);
    }

    [Fact]
    public async Task A_blank_description_is_allowed()
    {
        // The same behaviour as leaving a style instruction empty elsewhere:
        // the model settles on a voice of its own.
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\design", default);

        await synth.SynthesizeAsync(
            new GenerateRequest(TtsMode.VoiceDesign, "Hello", Instruct: null), default);

        Assert.Null(opened[0].Asked!.Instruction);
    }

    [Fact]
    public async Task The_result_is_24_kHz_and_carries_the_frame_count()
    {
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\design", default);
        opened[0].Answer = _ => new DesignResult(new float[2400], [.. Enumerable.Repeat(new int[16], 5)]);

        var result = await synth.SynthesizeAsync(
            new GenerateRequest(TtsMode.VoiceDesign, "Hello"), default);

        Assert.Equal(24_000, result.SampleRate);
        Assert.Equal(5, result.Frames);
        Assert.Equal(2400, result.Samples.Length);
        Assert.Equal(0.1, result.Duration.TotalSeconds, 3);
    }

    [Fact]
    public async Task Cancelling_reaches_the_pipeline()
    {
        var (synth, _) = New();
        await synth.LoadAsync(@"C:\models\design", default);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => synth.SynthesizeAsync(
                new GenerateRequest(TtsMode.VoiceDesign, "Hello"), cancelled.Token));
    }

    // ---- Sample conversion ----

    [Fact]
    public void Full_scale_maps_to_the_ends_of_the_range()
    {
        var pcm = DesignSpeechSynthesizer.ToPcm16([1f, -1f, 0f]);

        Assert.Equal(short.MaxValue, pcm[0]);
        Assert.Equal(-short.MaxValue, pcm[1]);
        Assert.Equal(0, pcm[2]);
    }

    [Fact]
    public void An_overshoot_saturates_rather_than_wrapping()
    {
        // A vocoder can exceed 1.0 slightly. Wrapping is not a quiet distortion
        // — it is a loud crack in the middle of a word, because the sample
        // jumps from full positive to full negative.
        var pcm = DesignSpeechSynthesizer.ToPcm16([1.4f, -1.8f]);

        Assert.Equal(short.MaxValue, pcm[0]);
        Assert.Equal(-short.MaxValue, pcm[1]);
    }

    [Fact]
    public void Quiet_samples_keep_their_proportions()
    {
        var pcm = DesignSpeechSynthesizer.ToPcm16([0.5f, 0.25f]);

        Assert.Equal(16_384, pcm[0]);
        Assert.Equal(8_192, pcm[1]);
    }

    [Fact]
    public void Silence_converts_to_silence()
    {
        Assert.All(DesignSpeechSynthesizer.ToPcm16(new float[64]), s => Assert.Equal(0, s));
    }

    [Fact]
    public void An_empty_clip_converts_to_an_empty_one()
    {
        Assert.Empty(DesignSpeechSynthesizer.ToPcm16([]));
    }

    /// <summary>A log that keeps what it was told.</summary>
    private sealed class RecordingLog : Bunyi.Core.Diagnostics.ILogSink
    {
        public void Log(string message) { }
    }
}
