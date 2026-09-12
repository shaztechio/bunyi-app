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
    public void First_playback_starts_at_ten_seconds_even_for_a_long_slow_recording(double rate)
    {
        var plan = new AdaptivePlaybackBuffer();
        plan.Configure(97);
        for (var produced = 2; produced <= 58; produced += 2)
        {
            plan.ObserveGenerated(produced + .4);
            plan.ObserveProduced(produced, produced / rate);
            Assert.Equal(10, plan.TargetSeconds(produced / rate, hasStarted: false));
        }
    }

    [Fact]
    public void Refills_adapt_to_slowdowns_but_never_exceed_twenty_seconds()
    {
        var plan = new AdaptivePlaybackBuffer();
        plan.Configure(97);
        plan.ObserveProduced(2, 2);
        plan.ObserveProduced(4, 4);
        Assert.Equal(10, plan.TargetSeconds(4, hasStarted: true));
        plan.ObserveProduced(6, 14);
        Assert.InRange(plan.TargetSeconds(14, hasStarted: true), 11, 20);
        Assert.Equal(20, plan.TargetSeconds(100, hasStarted: true));
        Assert.Equal(10, plan.TargetSeconds(100, hasStarted: false));
    }

    [Fact]
    public void Recording_length_never_becomes_the_playback_target()
    {
        var plan = new AdaptivePlaybackBuffer();
        plan.Configure(97);
        plan.ObserveProduced(45, 200);
        plan.ObserveProduced(47, 215);
        plan.ObserveGenerated(100);
        Assert.Equal(10, plan.TargetSeconds(215, hasStarted: false));
        Assert.Equal(20, plan.TargetSeconds(215, hasStarted: true));
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
    public void Play_now_bypasses_only_one_refill_and_keeps_the_ten_second_minimum()
    {
        using var player = new StreamingAudioPlayer(new SilentLog());
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var type = typeof(StreamingAudioPlayer);
        type.GetField("_firstPlaybackTimestamp", hidden)!.SetValue(player, 1L);
        type.GetField("_prebufferSamples", hidden)!.SetValue(player, 20L * 24000);
        var write = type.GetMethod("Write", hidden)!.CreateDelegate<CachePcm>(player);
        var read = type.GetMethod("Read", hidden)!.CreateDelegate<ReadPcm>(player);
        write(Enumerable.Repeat(.1f, 24000 * 10 - 1).ToArray(), 1, CancellationToken.None);
        var output = new float[512];
        player.StartPlaybackNow();
        read(output, 1);
        Assert.All(output, sample => Assert.Equal(0, sample));
        write([.1f], 1, CancellationToken.None);
        player.StartPlaybackNow();
        read(output, 1);
        Assert.All(output, sample => Assert.Equal(.1f, sample));
        read(new float[240000], 1);
        Assert.True(player.IsBuffering);
        write(Enumerable.Repeat(.1f, 24000 * 10).ToArray(), 1, CancellationToken.None);
        read(output, 1);
        Assert.All(output, sample => Assert.Equal(0, sample));
    }

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
