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
/// The preset-voice pipeline against the real 0.6B export.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless the export is on the machine, like <see cref="DesignPipelineTests"/>.
/// Unlike design mode there is no Python reference to pin frames against: the
/// export ships none, and the library this pipeline replaced cannot be made to
/// decode greedily. So the gate is the properties a correct port must have and a
/// broken one cannot fake — determinism under greedy decoding, speakers that
/// differ, an instruction that reaches the model, audio that is real signal —
/// with the structural equivalence to the validated design layout pinned in
/// <see cref="PresetPrefillTests"/>.
/// </para>
/// <para>
/// Pinned to the CPU provider for the reason the design tests are: CUDA is
/// deterministic and different, and a run that is both would fail these on a
/// machine where the accelerator works.
/// </para>
/// </remarks>
public class PresetPipelineTests
{
    private const string Text = "Hello! We'll begin in just a few minutes.";

    private static string? Root
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("BUNYI_PRESET_MODEL")
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bunyi", "Models", "models", "elbruno", "Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX");

            return File.Exists(Path.Combine(root, "embeddings", "speaker_ids.json"))
                && File.Exists(Path.Combine(root, "vocoder.onnx"))
                ? root
                : null;
        }
    }

    private static SamplingOptions Greedy { get; } =
        new(Temperature: 1f, TopK: 1, RepetitionPenalty: 1f);

    private static PresetPipeline Open() =>
        new(Root!, new NullLog(), provider: ExecutionProviderChoice.Cpu);

    /// <summary>One pipeline and one greedy run of ryan, shared by the tests that only read it.</summary>
    private static readonly Lazy<(PresetPipeline Pipeline, SpeechResult Ryan)> Shared = new(() =>
    {
        var pipeline = Open();
        var ryan = pipeline.Generate(new PresetRequest(Text, "ryan"), Greedy, maxFrames: 80);
        return (pipeline, ryan);
    }, isThreadSafe: true);

    private static (PresetPipeline Pipeline, SpeechResult Ryan) Ours()
    {
        Skip.If(Root is null,
            "The 5.9 GB preset-voice export is not on this machine. "
            + "Set BUNYI_PRESET_MODEL to its folder to run these.");

        return Shared.Value;
    }

    [SkippableFact]
    public void It_offers_the_nine_speakers_in_the_exports_order()
    {
        var (pipeline, _) = Ours();

        Assert.Equal(
            ["serena", "vivian", "uncle_fu", "ryan", "aiden", "ono_anna", "sohee", "eric", "dylan"],
            pipeline.Speakers);
    }

    [SkippableFact]
    public void It_speaks()
    {
        var (_, ryan) = Ours();

        // Somewhere between two and eight seconds for that sentence: a run that
        // stopped at once is silence, one that ran to the cap is rambling.
        Assert.InRange(ryan.Frames, 25, 80);
        Assert.Contains(ryan.Samples, s => Math.Abs(s) > 0.05f);
        Assert.All(ryan.Samples, s => Assert.True(float.IsFinite(s)));
        Assert.All(ryan.Codes, frame => Assert.Equal(16, frame.Length));
        Assert.All(ryan.Codes, frame => Assert.All(frame, c => Assert.InRange(c, 0, 2047)));
    }

    [SkippableFact]
    public void Greedy_decoding_is_deterministic()
    {
        // Nothing to sample, so any difference is the runtime itself being
        // non-deterministic — which would make every comparison below
        // meaningless.
        var (pipeline, ryan) = Ours();

        var again = pipeline.Generate(new PresetRequest(Text, "ryan"), Greedy, maxFrames: 80);

        Assert.Equal(ryan.Frames, again.Frames);
        for (var f = 0; f < ryan.Frames; f++) Assert.Equal(ryan.Codes[f], again.Codes[f]);
    }

    [SkippableFact]
    public void A_different_speaker_is_a_different_voice()
    {
        // The whole of what the speaker row does. If serena and ryan produced
        // the same codes, the row would be decoration.
        var (pipeline, ryan) = Ours();

        var serena = pipeline.Generate(new PresetRequest(Text, "serena"), Greedy, maxFrames: 80);

        Assert.NotEqual(ryan.Codes.Select(f => f[0]), serena.Codes.Select(f => f[0]));
    }

    [SkippableFact]
    public void The_style_instruction_reaches_the_model()
    {
        // #104, answered on the real export rather than argued: with everything
        // else held fixed and nothing sampled, the instruction changes what the
        // model produces. The library this replaced dropped it for this
        // variant; this is the pipeline not doing that.
        var (pipeline, ryan) = Ours();

        var whispered = pipeline.Generate(
            new PresetRequest(Text, "ryan", "Whisper, very softly and slowly."), Greedy, maxFrames: 80);

        Assert.NotEqual(ryan.Codes.Select(f => f[0]), whispered.Codes.Select(f => f[0]));
    }

    [SkippableFact]
    public void An_unknown_speaker_is_refused_before_any_inference()
    {
        var (pipeline, _) = Ours();

        var error = Assert.Throws<ArgumentException>(
            () => pipeline.Generate(new PresetRequest(Text, "nobody"), Greedy));

        Assert.Contains("ryan", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void A_frame_cap_stops_it_early()
    {
        var (pipeline, _) = Ours();

        var capped = pipeline.Generate(new PresetRequest(Text, "ryan"), Greedy, maxFrames: 3);

        Assert.Equal(3, capped.Frames);
    }

    [SkippableFact]
    public void Cancelling_stops_it_between_frames()
    {
        var (pipeline, _) = Ours();
        using var cancelled = new CancellationTokenSource();
        var seen = 0;

        Assert.Throws<OperationCanceledException>(() => pipeline.Generate(
            new PresetRequest(Text, "ryan"),
            Greedy,
            new SynchronousProgress(frames =>
            {
                seen = frames;
                if (frames >= 2) cancelled.Cancel();
            }),
            maxFrames: 40,
            cancelled.Token));

        Assert.InRange(seen, 2, 4);
    }

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class NullLog : ILogSink
    {
        public void Log(string message) { }
    }
}
