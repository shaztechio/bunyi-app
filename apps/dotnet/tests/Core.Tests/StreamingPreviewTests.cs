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

using Bunyi.Core.Engine;
using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class StreamingPreviewTests
{
    [Theory]
    [InlineData(39, false)]
    [InlineData(40, false)]
    [InlineData(41, true)]
    public void Threshold_uses_unrounded_upper_duration(int count, bool shouldStream)
    {
        var estimate = SpeechDurationEstimate.ForText(string.Join(' ', Enumerable.Repeat("word", count)));
        Assert.Equal(count / 2.0, estimate.UpperSeconds);
        Assert.Equal(shouldStream, estimate.ShouldStream);
    }

    [Fact]
    public void Estimate_handles_blank_punctuation_and_unspaced_scripts()
    {
        Assert.Equal(new SpeechDurationEstimate(0, 0), SpeechDurationEstimate.ForText("  ...  "));
        Assert.True(SpeechDurationEstimate.ForText(new string('声', 61), "chinese").ShouldStream);
        Assert.False(SpeechDurationEstimate.ForText(new string('声', 60)).ShouldStream);
        var plain = SpeechDurationEstimate.ForText("hello world");
        var paused = SpeechDurationEstimate.ForText("hello, world.");
        Assert.True(paused.UpperSeconds > plain.UpperSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(125)]
    public void Every_generated_frame_is_published_once_and_reference_is_never_published(int referenceCount)
    {
        var chunks = new List<AudioPreviewChunk>();
        var windows = new List<int>();
        var preview = new RollingAudioPreview(window =>
        {
            windows.Add(window.Count);
            return Decode(window);
        }, chunks.Add, Enumerable.Range(0, referenceCount).Select(i => new[] { -i - 1 }).ToArray());
        var frames = new List<int[]>();
        for (var i = 0; i < 133; i++)
        {
            frames.Add([i + 1]);
            preview.Update(frames, completed: false, default);
        }
        Assert.Equal(125 * RollingAudioPreview.SamplesPerFrame, chunks.Sum(c => c.Samples.Length));
        preview.Update(frames, completed: true, default);
        preview.Update(frames, completed: true, default);
        var actual = chunks.SelectMany(c => c.Samples).ToArray();
        Assert.Equal(Decode(frames), actual);
        long next = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(next, chunk.SampleOffset);
            Assert.Equal(24_000, chunk.SampleRate);
            next += chunk.Samples.Length;
        }
        Assert.All(windows, n => Assert.InRange(n, 1, 80));
    }

    [Fact]
    public void Short_tail_waits_for_successful_completion()
    {
        var chunks = new List<AudioPreviewChunk>();
        var preview = new RollingAudioPreview(Decode, chunks.Add);
        var frames = Enumerable.Range(1, 29).Select(i => new[] { i }).ToList();
        preview.Update(frames, false, default);
        Assert.Empty(chunks);
        preview.Update(frames, true, default);
        Assert.Equal(29 * RollingAudioPreview.SamplesPerFrame, chunks.Sum(c => c.Samples.Length));
    }

    [Fact]
    public void Cancellation_after_decode_prevents_publish()
    {
        using var cancel = new CancellationTokenSource();
        var chunks = new List<AudioPreviewChunk>();
        var preview = new RollingAudioPreview(window =>
        {
            cancel.Cancel();
            return Decode(window);
        }, chunks.Add);
        Assert.Throws<OperationCanceledException>(() => preview.Update([[1]], true, cancel.Token));
        Assert.Empty(chunks);
    }

    [Fact]
    public void Incompatible_decoder_layout_is_rejected_before_reference_audio_can_escape()
    {
        var chunks = new List<AudioPreviewChunk>();
        var preview = new RollingAudioPreview(_ => new float[7], chunks.Add, [[-1]]);
        preview.Update([[1]], true, default);
        preview.Update([[1], [2]], true, default);
        Assert.Single(chunks);
        Assert.Empty(chunks[0].Samples);
        Assert.Contains("audio layout", chunks[0].Failure);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Invalid_model_audio_fails_the_take_instead_of_signalling_fallback(float invalid)
    {
        var chunks = new List<AudioPreviewChunk>();
        var preview = new RollingAudioPreview(window =>
        {
            var pcm = Decode(window);
            pcm[0] = invalid; // Even invalid reference context must fail the take.
            return pcm;
        }, chunks.Add, [[-1]]);
        Assert.Throws<InvalidDataException>(() => preview.Update([[1]], true, default));
        Assert.Empty(chunks);
    }

    [Fact]
    public void Unexpected_decoder_failure_propagates()
    {
        var chunks = new List<AudioPreviewChunk>();
        var preview = new RollingAudioPreview(_ => throw new InvalidOperationException("decoder failed"), chunks.Add);
        Assert.Throws<InvalidOperationException>(() => preview.Update([[1]], true, default));
        Assert.Empty(chunks);
    }
    private static float[] Decode(List<int[]> window) => window
        .SelectMany(frame => Enumerable.Repeat((float)frame[0], RollingAudioPreview.SamplesPerFrame)).ToArray();
}