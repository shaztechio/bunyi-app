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
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class AdaptivePlaybackBufferTests
{
    [Theory]
    [InlineData(.2)]
    [InlineData(.5)]
    [InlineData(1.5)]
    public void Steady_generation_has_no_playback_underruns(double rate)
    {
        var plan = new AdaptivePlaybackBuffer();
        plan.Configure(110);
        double produced = 0, consumed = 0;
        var playing = false;
        double started = 0;
        for (var tick = 1; tick <= 20000 && consumed < 100; tick++)
        {
            var elapsed = tick * .1;
            var generated = Math.Min(100, elapsed * rate);
            plan.ObserveGenerated(generated);
            var delivered = Math.Floor(generated / 2) * 2;
            if (delivered > produced)
            {
                produced = delivered;
                plan.ObserveProduced(produced, elapsed);
            }
            var complete = produced >= 100;
            if (!playing && (complete || produced - consumed >= plan.TargetSeconds(elapsed)))
            {
                playing = true;
                started = produced;
            }
            if (!playing) continue;
            if (!complete) Assert.True(produced - consumed >= .1 - 1e-8, $"Underrun at {elapsed}s, rate {rate}");
            consumed += Math.Min(.1, produced - consumed);
        }
        Assert.True(consumed >= 99.99);
        Assert.InRange(started, 10, 100); // Very slow runs may deliberately wait for completion.
        if (rate > 1) Assert.True(started < 20);
    }

    [Fact]
    public void Slowdown_and_stalled_arrivals_increase_the_target()
    {
        var plan = new AdaptivePlaybackBuffer();
        plan.Configure(100);
        plan.ObserveProduced(2, 2);
        plan.ObserveProduced(4, 4);
        var initial = plan.TargetSeconds(4);
        plan.ObserveProduced(6, 8);
        var slower = plan.TargetSeconds(8);
        Assert.True(slower > initial);
        Assert.True(plan.TargetSeconds(10) > slower);
    }

    [Fact]
    public void Underestimated_duration_keeps_a_future_horizon()
    {
        var plan = new AdaptivePlaybackBuffer();
        plan.Configure(25);
        plan.ObserveProduced(20, 50);
        plan.ObserveProduced(22, 60);
        plan.ObserveGenerated(40);
        Assert.True(plan.TargetSeconds(60) > 40);
    }

    [Fact]
    public void A_larger_head_start_can_use_more_than_the_device_ring()
    {
        var plan = new AdaptivePlaybackBuffer();
        plan.Configure(100);
        plan.ObserveProduced(2, 10);
        plan.ObserveProduced(4, 20);
        Assert.True(plan.TargetSeconds(20) > 20);
    }

    [Fact]
    public void Cache_preserves_pcm_beyond_ring_capacity_and_is_removed_on_disposal()
    {
        string path;
        var source = Enumerable.Range(0, 24000 * 45).Select(i => (i % 200 - 100) / 100f).ToArray();
        using (var cache = new PreviewAudioSpool())
        {
            path = cache.Path;
            cache.Append(source.AsSpan(0, 12345), .5f, CancellationToken.None);
            cache.Append(source.AsSpan(12345), .5f, CancellationToken.None);
            Assert.Equal(source.Length, cache.SamplesWritten);
            var block = new float[8192];
            var offset = 0;
            int count;
            while ((count = cache.Read(block)) > 0)
            {
                for (var i = 0; i < count; i++) Assert.Equal(source[offset + i] * .5f, block[i]);
                offset += count;
            }
            Assert.Equal(source.Length, offset);
            Assert.True(File.Exists(path));
        }
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Cancelled_cache_write_is_not_published()
    {
        using var cache = new PreviewAudioSpool();
        Assert.Throws<OperationCanceledException>(() => cache.Append(new float[100], 1, new CancellationToken(true)));
        Assert.Equal(0, cache.SamplesWritten);
    }

    private delegate void ReadPcm(Span<float> output, int channels);
    private delegate void CachePcm(ReadOnlySpan<float> input, float gain, CancellationToken ct);

    [Fact]
    public async Task Adaptive_target_larger_than_ring_does_not_block_production_or_completion()
    {
        // Exercise the real disk feeder and device callback without a native device.
        using var player = new StreamingAudioPlayer(new SilentLog());
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(StreamingAudioPlayer);
        var spool = new PreviewAudioSpool();
        type.GetField("_spool", hidden)!.SetValue(player, spool);
        var feed = type.GetMethod("FeedAsync", hidden)!.CreateDelegate<Func<Task>>(player);
        var cache = type.GetMethod("Cache", hidden)!.CreateDelegate<CachePcm>(player);
        var read = type.GetMethod("Read", hidden)!.CreateDelegate<ReadPcm>(player);
        var readCount = type.GetField("_read", hidden)!;
        player.ConfigureBuffer(100);
        var source = Enumerable.Repeat(.25f, 24000 * 45).ToArray();
        var feeder = Task.Run(feed);
        type.GetField("_feeder", hidden)!.SetValue(player, feeder);
        cache(source, 1, CancellationToken.None);
        Assert.Equal(45, player.BufferedSeconds);
        Assert.False(player.HasStarted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completion = player.CompleteAsync(timeout.Token);
        var output = new float[8192];
        while (!completion.IsCompleted)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var before = (long)readCount.GetValue(player)!;
            read(output, 1);
            var consumed = (long)readCount.GetValue(player)! - before;
            for (var i = 0; i < consumed; i++) Assert.Equal(.25f, output[i]);
            await Task.Delay(1, timeout.Token);
        }
        await completion;
        Assert.Equal(source.Length, (long)readCount.GetValue(player)!);
        Assert.Null(player.Failure);
        player.Dispose();
        Assert.False(File.Exists(spool.Path));
    }

    private sealed class SilentLog : Bunyi.Core.Diagnostics.ILogSink
    {
        public void Log(string message) { }
    }
}
