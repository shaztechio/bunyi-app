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

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>Request selection and preview lifecycle with fake audio, not a hardware playback test.</summary>
public sealed class StreamingPreviewTests : HeadlessWindows
{
    private delegate void QueuePcm(ReadOnlySpan<float> samples, float gain, CancellationToken cancellationToken);
    private delegate void ReadPcm(Span<float> samples, int channels);

    [Fact]
    public void Playback_waits_for_ten_full_seconds_of_playable_audio()
    {
        using var player = new StreamingAudioPlayer(new RecordingLog());
        // Exercise the production producer/consumer queue directly, without
        // constructing a native device. Held smoothing samples do not count
        // toward the ten seconds of PCM that must be ready for playback.
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var queue = typeof(StreamingAudioPlayer).GetMethod("Write", hidden)!.CreateDelegate<QueuePcm>(player);
        var read = typeof(StreamingAudioPlayer).GetMethod("Read", hidden)!.CreateDelegate<ReadPcm>(player);
        var samples = Enumerable.Repeat(.1f, 240000 - 1).ToArray();
        var output = new float[512];
        queue(samples, 1f, CancellationToken.None);
        Assert.Equal(239999d / 24000, player.BufferedSeconds);
        Assert.False(player.HasStarted);
        read(output, 1);
        Assert.All(output, value => Assert.Equal(0f, value));
        Assert.Equal(0, player.FirstPlaybackTimestamp);

        queue([.1f], 1f, CancellationToken.None);
        read(output, 1);
        Assert.All(output, value => Assert.Equal(.1f, value));
        Assert.False(player.IsBuffering);
        var timestamp = player.FirstPlaybackTimestamp;
        Assert.True(timestamp > 0);
        Assert.True(player.HasStarted);
        Assert.Equal((240000d - 512) / 24000, player.BufferedSeconds);
        read(output, 1);
        Assert.Equal(timestamp, player.FirstPlaybackTimestamp);
    }

    [Fact]
    public void Underrun_waits_for_ten_seconds_again_before_resuming()
    {
        using var player = new StreamingAudioPlayer(new RecordingLog());
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var queue = typeof(StreamingAudioPlayer).GetMethod("Write", hidden)!.CreateDelegate<QueuePcm>(player);
        var read = typeof(StreamingAudioPlayer).GetMethod("Read", hidden)!.CreateDelegate<ReadPcm>(player);
        queue(Enumerable.Repeat(.1f, 240000).ToArray(), 1f, CancellationToken.None);
        read(new float[240001], 1);
        Assert.True(player.IsBuffering);

        queue(Enumerable.Repeat(.2f, 239999).ToArray(), 1f, CancellationToken.None);
        var output = new float[512];
        read(output, 1);
        Assert.All(output, value => Assert.Equal(0f, value));
        Assert.True(player.IsBuffering);
        queue([.2f], 1f, CancellationToken.None);
        read(output, 1);
        Assert.All(output, value => Assert.Equal(.2f, value));
        Assert.False(player.IsBuffering);
    }

    [Fact]
    public async Task Completed_generation_plays_a_remainder_shorter_than_ten_seconds()
    {
        using var player = new StreamingAudioPlayer(new RecordingLog());
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic;
        var queue = typeof(StreamingAudioPlayer).GetMethod("Write", hidden)!.CreateDelegate<QueuePcm>(player);
        var read = typeof(StreamingAudioPlayer).GetMethod("Read", hidden)!.CreateDelegate<ReadPcm>(player);
        queue(Enumerable.Repeat(.3f, 1000).ToArray(), 1f, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var completion = player.CompleteAsync(timeout.Token);
        var output = new float[1000];
        read(output, 1);
        Assert.All(output, value => Assert.Equal(.3f, value));
        await completion;
        Assert.Null(player.Failure);
    }

    private sealed class Preview : IStreamingAudioPlayer
    {
        public bool IsBuffering { get; set; } = true;
        public bool HasStarted { get; set; }
        public double BufferedSeconds { get; set; }
        public double BufferTargetSeconds { get; set; } = 10;
        public double EstimatedSeconds { get; private set; }
        public double GeneratedSeconds { get; private set; }
        public void ConfigureBuffer(double estimatedSpeechSeconds) => EstimatedSeconds = estimatedSpeechSeconds;
        public void ReportGeneratedSeconds(double seconds) => GeneratedSeconds = seconds;
        public string? Failure { get; set; }
        public int Chunks { get; private set; }
        public bool Stopped { get; private set; }
        public bool Completing { get; private set; }
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Add(AudioPreviewChunk chunk, CancellationToken cancellationToken) => Chunks++;
        public Task CompleteAsync(CancellationToken cancellationToken)
        {
            Completing = true;
            return Drained.Task.WaitAsync(cancellationToken);
        }
        public void Stop() => Stopped = true;
        public void Dispose() => Stop();
    }

    private static string Words(int count) => string.Join(' ', Enumerable.Repeat("word", count));

    private static MainViewModel Model(FakeEngine engine, FakePlayer player, Preview preview, TtsMode mode, int count) =>
        new(engine, player, new RecordingLog(), previewPlayerFactory: () => preview)
        {
            Mode = mode, Script = Words(count), Instruct = "A calm adult voice",
            ReferenceAudioPath = "reference.wav", ReferenceTranscript = "Hello there",
        };

    [AvaloniaTheory]
    [InlineData(TtsMode.PresetVoice)]
    [InlineData(TtsMode.VoiceDesign)]
    [InlineData(TtsMode.VoiceClone)]
    public async Task All_modes_stream_only_above_twenty_seconds(TtsMode mode)
    {
        foreach (var count in new[] { 40, 41 })
        {
            var engine = new FakeEngine();
            var player = new FakePlayer();
            var preview = new Preview();
            using var model = Model(engine, player, preview, mode, count);
            var pending = model.GenerateCommand.ExecuteAsync(null);
            Assert.Equal(count > 40, engine.LastRequest!.AudioPreview is not null);
            Assert.Equal(count > 40, model.IsPreviewSession);
            engine.Complete("finished.wav");
            preview.Drained.TrySetResult();
            await pending;
            Assert.Equal(count > 40 ? 0 : 1, player.Played.Count);
        }
    }

    [AvaloniaFact]
    public async Task Saved_result_waits_for_preview_tail_without_playing_twice_and_stop_keeps_file()
    {
        var engine = new FakeEngine();
        var player = new FakePlayer();
        var preview = new Preview();
        using var model = Model(engine, player, preview, TtsMode.PresetVoice, 50);
        var window = Open(new MainWindow { DataContext = model });
        var pending = model.GenerateCommand.ExecuteAsync(null);
        engine.LastRequest!.AudioPreview!(new AudioPreviewChunk([.1f], 24000, 0));
        engine.Complete("finished.wav");
        Assert.True(preview.Completing);
        Assert.True(model.IsBusy);
        Assert.True(model.IsPreviewSession);
        Assert.Equal("finished.wav", model.LastOutputPath);
        Assert.False(window.GetLogicalDescendants().OfType<ListBox>().Single(c => c.Name == "ModePicker").IsEnabled);
        Assert.Empty(player.Played);

        model.StopCommand.Execute(null);
        Assert.True(preview.Stopped);
        await pending;
        Assert.False(model.IsBusy);
        Assert.Equal("finished.wav", model.LastOutputPath);
        Assert.Contains("saved", model.Status);
        Assert.Empty(player.Played);
        model.PlayCommand.Execute(null);
        Assert.Single(player.Played);
    }

    [AvaloniaFact]
    public async Task Cancel_stops_preview_immediately_and_rejects_late_audio()
    {
        var engine = new FakeEngine();
        var preview = new Preview();
        using var model = Model(engine, new FakePlayer(), preview, TtsMode.VoiceClone, 50);
        var pending = model.GenerateCommand.ExecuteAsync(null);
        var callback = engine.LastRequest!.AudioPreview!;
        callback(new AudioPreviewChunk([.1f], 24000, 0));
        model.StopCommand.Execute(null);
        Assert.True(preview.Stopped);
        callback(new AudioPreviewChunk([.2f], 24000, 1));
        Assert.Equal(1, preview.Chunks);
        engine.Publish(EngineStatus.Idle);
        engine.Pending.TrySetCanceled();
        await pending;
        Assert.Null(model.LastOutputPath);
        callback(new AudioPreviewChunk([.3f], 24000, 2));
        Assert.Equal(1, preview.Chunks);
    }

    [AvaloniaFact]
    public async Task Preview_device_failure_is_visible_and_does_not_discard_output()
    {
        var engine = new FakeEngine();
        var preview = new Preview { Failure = "No device" };
        preview.Drained.TrySetResult();
        using var model = Model(engine, new FakePlayer(), preview, TtsMode.VoiceDesign, 50);
        var pending = model.GenerateCommand.ExecuteAsync(null);
        model.TickPreview();
        Assert.Contains("Preview unavailable", model.PreviewStatus);
        engine.Complete("finished.wav");
        await pending;
        Assert.Equal("finished.wav", model.LastOutputPath);
        Assert.Contains("press Play", model.Status);
    }

    [AvaloniaFact]
    public async Task Playback_panel_explains_initial_wait_underrun_resume_and_finalization()
    {
        var engine = new FakeEngine();
        var preview = new Preview();
        using var model = Model(engine, new FakePlayer(), preview, TtsMode.PresetVoice, 50);
        var window = Open(new MainWindow { DataContext = model });
        var pending = model.GenerateCommand.ExecuteAsync(null);
        engine.Publish(new EngineStatus(EngineState.Generating));
        model.TickPreview();
        Assert.Equal("Preparing playback", model.PreviewStatus);
        Assert.Contains("generation speed", model.PreviewDetail);
        Assert.True(window.FindControl<Border>("PlaybackStatusPanel")!.IsVisible);
        Assert.True(model.ShowPreviewBuffer);

        preview.IsBuffering = false;
        preview.HasStarted = true;
        model.TickPreview();
        Assert.Equal("Playing audio", model.PreviewStatus);
        Assert.False(model.ShowPreviewBuffer);

        preview.IsBuffering = true;
        preview.BufferedSeconds = 4.8;
        model.TickPreview();
        Assert.Equal("Waiting for more audio", window.FindControl<TextBlock>("PreviewStatus")!.Text);
        Assert.Contains("resume automatically", window.FindControl<TextBlock>("PreviewDetail")!.Text);
        Assert.Equal("4 of 10 seconds of audio ready", model.PreviewBufferText);
        Assert.Equal(4.8, window.FindControl<ProgressBar>("PreviewBufferProgress")!.Value);
        preview.BufferTargetSeconds = 45;
        engine.Publish(new EngineStatus(EngineState.Generating, Frames: 250));
        Assert.Equal(20, preview.GeneratedSeconds);
        Assert.True(preview.EstimatedSeconds > 20);
        Assert.Equal("4 of 45 seconds of audio ready", model.PreviewBufferText);
        Assert.Equal(45, window.FindControl<ProgressBar>("PreviewBufferProgress")!.Maximum);

        preview.IsBuffering = false;
        model.TickPreview();
        Assert.Equal("Playing audio", model.PreviewStatus);
        preview.IsBuffering = true;
        engine.Publish(new EngineStatus(EngineState.Finalizing));
        Assert.Equal("Finishing your recording", model.PreviewStatus);
        Assert.False(model.ShowPreviewBuffer);
        engine.Complete("finished.wav");
        Assert.Equal("Finishing playback", model.PreviewStatus);
        Assert.Contains("saved", model.PreviewDetail);
        preview.Drained.TrySetResult();
        await pending;
        Assert.False(window.FindControl<Border>("PlaybackStatusPanel")!.IsVisible);
    }

    private sealed class PreviewClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [AvaloniaFact]
    public async Task A_pause_announcement_is_deferred_instead_of_lost_and_buffer_ticks_stay_quiet()
    {
        // Checks the view model's paced announcement value, not a real screen reader.
        var engine = new FakeEngine();
        var preview = new Preview { HasStarted = true, IsBuffering = false };
        var clock = new PreviewClock();
        using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog(),
            clock: clock, previewPlayerFactory: () => preview) { Script = Words(50) };
        var pending = model.GenerateCommand.ExecuteAsync(null);
        Assert.Contains("Playing audio", model.Announcement);
        preview.IsBuffering = true;
        model.TickPreview();
        Assert.Equal("Waiting for more audio", model.PreviewStatus);
        Assert.DoesNotContain("Waiting for more audio", model.Announcement);
        clock.Now += MainViewModel.AnnouncementGap;
        model.TickPreview();
        Assert.Contains("Waiting for more audio", model.Announcement);
        Assert.Contains("resume automatically", model.Announcement);
        var spoken = model.Announcement;
        preview.BufferedSeconds = 3;
        model.TickPreview();
        Assert.Equal(spoken, model.Announcement);
        engine.Complete("finished.wav");
        preview.Drained.TrySetResult();
        await pending;
    }

    [AvaloniaFact]
    public async Task Request_and_streaming_decision_are_frozen_during_transcription()
    {
        var engine = new FakeEngine();
        var preview = new Preview();
        var transcript = new TaskCompletionSource<string>();
        using var model = Model(engine, new FakePlayer(), preview, TtsMode.VoiceClone, 50);
        model.ReferenceTranscript = "";
        model.Transcribe = (_, _) => transcript.Task;
        var pending = model.GenerateCommand.ExecuteAsync(null);
        Assert.True(model.IsPreviewSession);
        model.SelectedSegment = HistorySegment.Instance;
        Assert.False(model.ShowingHistory);
        model.Script = "Changed after submitting";
        model.Language = "French";
        transcript.SetResult("Reference words");
        Assert.Equal(Words(50), engine.LastRequest!.Text);
        Assert.NotEqual("French", engine.LastRequest.Language);
        Assert.Equal("Reference words", engine.LastRequest.ReferenceTranscript);
        Assert.NotNull(engine.LastRequest.AudioPreview);
        engine.Complete("finished.wav");
        preview.Drained.TrySetResult();
        await pending;
    }
}
