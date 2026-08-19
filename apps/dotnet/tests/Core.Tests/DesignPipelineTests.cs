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
using Bunyi.Core.Qwen;
using Bunyi.Core.Diagnostics;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The voice-design pipeline, against the reference implementation (spec §1).
/// </summary>
/// <remarks>
/// <para>
/// Run greedily on both sides — top-k of one leaves the softmax with all its
/// mass on a single token, so nothing is sampled and both runs are
/// reproducible. That makes the <b>codes</b> comparable rather than the audio,
/// which is a far stronger check: sixteen integers a frame, against a waveform
/// that could be approximately right for the wrong reasons.
/// </para>
/// <para>
/// <b>Bit-exact agreement is not achievable, and is not the goal.</b> Our
/// prefill embeddings differ from the reference's by about one part in 10^8 —
/// measured at 2 to 7 x 10^-9 per value — because a dot product over 2048 terms
/// does not give the same last bits under two different summation orders. Both
/// are correct float32 arithmetic. Twenty-eight layers of attention amplify
/// that, and where the model's own top two candidates are close, the choice
/// flips.
/// </para>
/// <para>
/// So correctness is asserted as: the same number of frames, the same length of
/// audio, agreement up to the first close decision, and — the part that
/// separates "numerically unlucky" from "wrong" — that any divergence happens
/// where the reference itself was nearly undecided.
/// </para>
/// </remarks>
public class DesignPipelineTests
{
    private sealed record Margin(int group, int chosen, int runner_up, double margin);

    private sealed record Truth(
        string note, string text, string? instruct, string language,
        int frames, int[][] codes, int wav_samples, Margin[] frame0_margins);

    private static Truth Reference { get; } =
        JsonSerializer.Deserialize<Truth>(
            File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "Fixtures", "design-greedy-truth.json")))!;

    private static string? Root
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("BUNYI_DESIGN_MODEL")
                ?? @"C:\bs\dm\models\models\wavekat\Qwen3-TTS-1.7B-VoiceDesign-ONNX";

            return File.Exists(Path.Combine(root, "int4", "vocoder.onnx")) ? root : null;
        }
    }

    /// <summary>Greedy: nothing left to sample.</summary>
    private static SamplingOptions Greedy { get; } =
        new(Temperature: 1f, TopK: 1, RepetitionPenalty: 1f);

    /// <summary>
    /// The one run these tests share.
    /// </summary>
    /// <remarks>
    /// Loading four sessions and generating takes seconds; doing it per test
    /// would take minutes. The run is deterministic, so one is enough.
    /// </remarks>
    private static readonly Lazy<(SpeechResult Result, TimeSpan Elapsed)> Run = new(() =>
    {
        using var pipeline = new DesignPipeline(Root!, "int4", new NullLog());

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = pipeline.Generate(
            new DesignRequest(Reference.text, Reference.instruct, Reference.language),
            Greedy,
            maxFrames: 40);

        return (result, clock.Elapsed);
    }, isThreadSafe: true);

    private static SpeechResult Ours()
    {
        Skip.If(Root is null,
            "The 5.85 GB voice-design export is not on this machine. "
            + "Set BUNYI_DESIGN_MODEL to its folder to run these.");

        return Run.Value.Result;
    }

    [SkippableFact]
    public void It_produces_the_same_number_of_frames_as_the_reference()
    {
        // Generation ends when the model emits its stop token, so the count is
        // the model's own decision rather than a cap being hit — matching it
        // means the run took the same shape.
        Assert.Equal(Reference.frames, Ours().Frames);
    }

    [SkippableFact]
    public void It_produces_the_same_length_of_audio()
    {
        Assert.Equal(Reference.wav_samples, Ours().Samples.Length);
    }

    [SkippableFact]
    public void The_audio_is_24_kHz_of_real_signal()
    {
        // §2 requires 24 kHz. Silence would also be the right length.
        var result = Ours();

        Assert.Equal(1.0, result.Duration(24_000).TotalSeconds, 1);
        Assert.Contains(result.Samples, s => Math.Abs(s) > 0.01f);
        Assert.All(result.Samples, s => Assert.True(float.IsFinite(s)));
    }

    [SkippableFact]
    public void Every_frame_carries_a_code_for_every_codebook()
    {
        var result = Ours();

        Assert.All(result.Codes, frame => Assert.Equal(16, frame.Length));
        Assert.All(result.Codes, frame => Assert.All(frame, c => Assert.InRange(c, 0, 2047)));
    }

    [SkippableFact]
    public void The_talkers_own_choice_matches_the_reference_on_the_first_frame()
    {
        // Code 0 comes from the talker, and it is the one downstream of prefill
        // alone. Matching it means the sequence, the projection and the whole
        // 28-layer forward pass agree.
        Assert.Equal(Reference.codes[0][0], Ours().Codes[0][0]);
    }

    [SkippableFact]
    public void The_first_frame_agrees_until_the_reference_itself_is_undecided()
    {
        // The code predictor's decisions, in order, until the first one the
        // reference nearly got wrong itself.
        var ours = Ours().Codes[0];
        var firstClose = Reference.frame0_margins.First(m => m.margin < 0.05).group;

        for (var group = 0; group <= firstClose; group++)
        {
            Assert.Equal(Reference.codes[0][group], ours[group]);
        }
    }

    [SkippableFact]
    public void The_first_difference_is_at_the_closest_decision_of_the_whole_run()
    {
        // The test that separates "numerically unlucky" from "wrong", and it
        // can only be applied to the FIRST difference: once one code differs,
        // the predictor is fed a different embedding and its later decisions
        // are made from a state the reference never had, so the reference's
        // margins no longer describe them.
        //
        // A one-ULP input difference can only overturn a decision that was
        // nearly tied. A real bug would diverge wherever it liked.
        var ours = Ours().Codes[0];
        var theirs = Reference.codes[0];

        var first = Enumerable.Range(0, 16).FirstOrDefault(g => ours[g] != theirs[g], -1);
        Skip.If(first < 0, "This run matched the reference exactly, so there is nothing to explain.");

        // Code 0 is the talker's; codes 1..15 are the predictor's, and its
        // margins are indexed from zero.
        Assert.True(first > 0, "the talker's own choice differed, which no rounding explains");

        var decision = Reference.frame0_margins[first - 1];
        var closest = Reference.frame0_margins.Min(m => m.margin);

        Assert.Equal(closest, decision.margin);
        Assert.Equal(decision.runner_up, ours[first]);
    }

    [SkippableFact]
    public void The_closest_decision_is_close_enough_for_rounding_to_reach()
    {
        // Why the previous test means anything: 0.024 out of a logit near 15 is
        // a relative margin under two parts in a thousand, which one ULP of
        // input error can cross after twenty-eight layers. The next closest is
        // 0.064 — not far behind, so this is a run where two decisions were
        // genuinely tight rather than one freak.
        var margins = Reference.frame0_margins.Select(m => m.margin).Order().ToArray();

        Assert.True(margins[0] < 0.05, $"the closest decision is {margins[0]:F4}");
        Assert.True(margins[^1] > 10 * margins[0],
            "most decisions should be far from tied, or the comparison proves nothing");
    }

    [SkippableFact]
    public void Two_runs_of_ours_agree_exactly()
    {
        // Greedy has nothing to sample, so any difference between two runs would
        // be the runtime itself being non-deterministic — which would make every
        // comparison above meaningless.
        Skip.If(Root is null, "The voice-design export is not on this machine.");

        using var pipeline = new DesignPipeline(Root!, "int4", new NullLog());

        var again = pipeline.Generate(
            new DesignRequest(Reference.text, Reference.instruct, Reference.language),
            Greedy, maxFrames: 40);

        Assert.Equal(Reference.frames, again.Frames);

        for (var frame = 0; frame < again.Frames; frame++)
        {
            Assert.Equal(Ours().Codes[frame], again.Codes[frame]);
        }
    }

    [SkippableFact]
    public void A_frame_cap_stops_it_early()
    {
        Skip.If(Root is null, "The voice-design export is not on this machine.");

        using var pipeline = new DesignPipeline(Root!, "int4", new NullLog());

        var capped = pipeline.Generate(
            new DesignRequest(Reference.text, Reference.instruct, Reference.language),
            Greedy, maxFrames: 3);

        Assert.Equal(3, capped.Frames);
    }

    [SkippableFact]
    public void Cancelling_stops_it_between_frames()
    {
        // §2's Stop has to reach this loop: a generation runs for minutes and
        // the only way out otherwise is closing the window.
        Skip.If(Root is null, "The voice-design export is not on this machine.");

        using var pipeline = new DesignPipeline(Root!, "int4", new NullLog());
        using var cancelled = new CancellationTokenSource();

        var seen = 0;
        var progress = new Progress<int>(_ => { });

        Assert.Throws<OperationCanceledException>(() => pipeline.Generate(
            new DesignRequest(Reference.text, Reference.instruct, Reference.language),
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

    /// <summary>Reports on the calling thread, so a test can act between frames.</summary>
    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class NullLog : ILogSink
    {
        public void Log(string message) { }
    }
}
