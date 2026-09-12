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
/// Preset voice behind the engine's synthesizer seam (spec §1).
/// </summary>
/// <remarks>
/// Every case here runs without the 5.9 GB export. What the adapter does —
/// offering the export's speakers, choosing a default, passing the style
/// instruction through, releasing one pipeline before opening another — needs
/// no model to be wrong.
/// </remarks>
public class PresetSynthesizerTests
{
    private sealed class FakePipeline : IPresetPipeline
    {
        public PresetRequest? Asked { get; private set; }
        public bool Disposed { get; private set; }
        public float[] Samples { get; set; } = [0f, 0.5f, -0.5f];
        public IReadOnlyList<string> Speakers { get; set; } = ["serena", "vivian", "ryan"];
        public Func<PresetRequest, SpeechResult>? Answer { get; set; }

        public SamplingOptions DefaultSampling => SamplingOptions.Default;

        public SpeechResult Generate(
            PresetRequest request, SamplingOptions? options = null,
            IProgress<int>? progress = null, int? maxFrames = null,
            CancellationToken ct = default)
        {
            Asked = request;
            ct.ThrowIfCancellationRequested();

            return Answer?.Invoke(request)
                ?? new SpeechResult(Samples, [new int[16], new int[16]]);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Lines { get; } = [];
        public void Log(string message) => Lines.Add(message);
    }

    private static (PresetSpeechSynthesizer Synth, List<FakePipeline> Opened) New()
    {
        var opened = new List<FakePipeline>();

        var synth = new PresetSpeechSynthesizer(
            new RecordingLog(),
            (_, _) =>
            {
                var pipeline = new FakePipeline();
                opened.Add(pipeline);
                return pipeline;
            });

        return (synth, opened);
    }

    [Fact]
    public async Task It_offers_the_exports_speakers_once_loaded_and_none_before()
    {
        // Empty until a model is loaded is the truth rather than a guess: a
        // picker filled from a hardcoded list would show names a differently
        // configured export does not have.
        var (synth, _) = New();
        Assert.Empty(synth.Speakers);

        await synth.LoadAsync(@"C:\models\preset", default);

        Assert.Equal(["serena", "vivian", "ryan"], synth.Speakers);
    }

    [Fact]
    public void It_acts_on_the_style_instruction()
    {
        // §1 gives preset voice a style instruction and the model card documents
        // it. The only thing that ever refused it was the previous pipeline's
        // per-variant flag — RESEARCH-ONNX.md, "policy, not capability".
        var (synth, _) = New();

        Assert.True(synth.SupportsInstruct);
    }

    [Fact]
    public async Task Loading_the_same_folder_again_keeps_what_is_open()
    {
        var (synth, opened) = New();

        await synth.LoadAsync(@"C:\models\preset", default);
        await synth.LoadAsync(@"C:\models\preset", default);

        Assert.Single(opened);
    }

    [Fact]
    public async Task Loading_a_different_folder_releases_the_old_one_first()
    {
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
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\preset", default);

        await synth.UnloadAsync();

        Assert.False(synth.IsLoaded);
        Assert.Empty(synth.Speakers);
        Assert.True(opened[0].Disposed);
    }

    [Fact]
    public async Task Generating_before_loading_is_an_error_rather_than_silence()
    {
        var (synth, _) = New();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => synth.SynthesizeAsync(new GenerateRequest(TtsMode.PresetVoice, "hello"), default));
    }

    [Fact]
    public async Task The_request_reaches_the_pipeline_intact()
    {
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\preset", default);

        await synth.SynthesizeAsync(
            new GenerateRequest(TtsMode.PresetVoice, "Hello there.",
                Language: "english", Speaker: "vivian", Instruct: "Softly, as a bedtime story"),
            default);

        var asked = opened[0].Asked!;
        Assert.Equal("Hello there.", asked.Text);
        Assert.Equal("vivian", asked.Speaker);
        Assert.Equal("Softly, as a bedtime story", asked.Instruction);
        Assert.Equal("english", asked.Language);
    }

    [Fact]
    public async Task No_speaker_means_the_same_default_as_before()
    {
        // The previous pipeline fell back to "ryan" when a request named no
        // speaker. A request that never chose one should sound the same as it
        // did, so the default is the same — when the export has it.
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\preset", default);

        await synth.SynthesizeAsync(
            new GenerateRequest(TtsMode.PresetVoice, "Hello", Speaker: null), default);

        Assert.Equal("ryan", opened[0].Asked!.Speaker);
    }

    [Fact]
    public void Without_the_default_the_first_listed_speaker_stands_in()
    {
        Assert.Equal("aiden", PresetSpeechSynthesizer.SpeakerFor(null, ["aiden", "sohee"]));
        Assert.Equal("Ryan", PresetSpeechSynthesizer.SpeakerFor(null, ["aiden", "Ryan"]));
        Assert.Equal("sohee", PresetSpeechSynthesizer.SpeakerFor("  sohee ", ["aiden", "sohee"]));
    }

    [Fact]
    public async Task The_result_is_24_kHz_and_carries_the_frame_count()
    {
        var (synth, opened) = New();
        await synth.LoadAsync(@"C:\models\preset", default);
        opened[0].Answer = _ => new SpeechResult(new float[2400], [.. Enumerable.Repeat(new int[16], 5)]);

        var result = await synth.SynthesizeAsync(
            new GenerateRequest(TtsMode.PresetVoice, "Hello"), default);

        Assert.Equal(24_000, result.SampleRate);
        Assert.Equal(5, result.Frames);
        Assert.Equal(2400, result.Samples.Length);
    }

    [Fact]
    public async Task Cancelling_reaches_the_pipeline()
    {
        var (synth, _) = New();
        await synth.LoadAsync(@"C:\models\preset", default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => synth.SynthesizeAsync(new GenerateRequest(TtsMode.PresetVoice, "Hello"), cancelled.Token));
    }
}
