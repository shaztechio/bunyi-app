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
using Bunyi.Core.Engine;
using Bunyi.Core.Models;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>Presentation and headless layout checks; not a native screen-reader test.</summary>
public class DownloadFeedbackTests : HeadlessWindows
{
    [Fact]
    public void A_service_wait_keeps_progress_and_elapsed_without_false_stall_or_reconnect()
    {
        var model = new DownloadViewModel { Reconnect = () => throw new InvalidOperationException() };
        model.Update(Snapshot(), Start);
        var bytes = model.OverallBytes;
        model.Update(Snapshot() with { Phase = DownloadPhase.Waiting,
            ServiceWait = new("huggingface.co", Start.AddSeconds(120), 1, 120) }, Start);
        model.Tick(Start.AddSeconds(60));
        Assert.Contains("Retrying in 60s", model.Receipt);
        Assert.Equal(bytes, model.OverallBytes);
        Assert.Equal("Model download elapsed: 1:00", model.Elapsed);
        Assert.False(model.Slow);
        Assert.False(model.Stalled);
        Assert.False(model.CanReconnect);
        Assert.Contains("stop", model.Explanation);
    }

    [AvaloniaFact]
    public async Task Mirror_failure_offers_an_explicit_retry_with_the_same_inputs()
    {
        var failure = new DownloadServiceException("download_service_unavailable", "Downloads paused",
            new("https://models.bunyi.app/onnx/customvoice/manifest.sha256"));
        var engine = new FakeEngine { GenerateFailure = failure };
        var switches = 0;
        using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog())
        {
            Script = "Hello again", CanRecoverDownload = (_, ex) => ex == failure && switches == 0,
            UseHuggingFace = (_, _) => { switches++; engine.GenerateFailure = null; },
        };
        var window = new MainWindow { DataContext = model };
        window.Show();
        await model.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(0, switches);
        Assert.True(model.CanUseHuggingFace);
        Assert.True(window.FindControl<Button>("DownloadFromHuggingFaceButton")!.IsEffectivelyVisible);
        var request = engine.LastRequest;
        var retry = model.DownloadFromHuggingFaceCommand.ExecuteAsync(null);
        Assert.Equal(1, switches);
        Assert.Equal(request, engine.LastRequest);
        Assert.False(model.CanUseHuggingFace);
        engine.Complete("test-output.wav");
        await retry;
        window.Close();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Changing_mode_or_settings_invalidates_the_recovery_offer(bool changeMode)
    {
        var engine = new FakeEngine { GenerateFailure = new DownloadServiceException("download_service_unavailable", "Paused", new("https://models.bunyi.app/file")) };
        var switches = 0;
        using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog())
        {
            Script = "Hello", CanRecoverDownload = (_, _) => true,
            UseHuggingFace = (_, _) => switches++,
        };
        await model.GenerateCommand.ExecuteAsync(null);
        Assert.True(model.CanUseHuggingFace);
        if (changeMode) model.Mode = Bunyi.Core.TtsMode.VoiceClone;
        else model.RefreshModelNotice();
        Assert.False(model.CanUseHuggingFace);
        await model.DownloadFromHuggingFaceCommand.ExecuteAsync(null);
        Assert.Equal(0, switches);
    }

    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    private static DownloadProgress Snapshot(long received = 1) => new(
        DownloadPhase.Downloading, BytesReceived: received, BytesReused: 2_350_000_000,
        BytesTotal: 5_000_000_000, CurrentFile: "model.onnx.data",
        CurrentFileBytes: 1_200_000_000 + received, CurrentFileTotal: 3_000_000_000,
        LastReceivedAt: Start, WaitingSince: Start);

    [Fact]
    public void Slow_receipts_use_recent_speed_and_reconnect_preserves_elapsed_time()
    {
        var reconnects = 0;
        var model = new DownloadViewModel { Reconnect = () => reconnects++ };
        for (var second = 0; second <= 31; second++)
            model.Update(Snapshot(1 + second * 55_000) with
            {
                LastReceivedAt = Start.AddSeconds(second),
                BytesPerSecond = 20_000_000, // Old whole-model average must be ignored.
            }, Start.AddSeconds(second));
        Assert.True(model.Slow);
        Assert.Equal(55_000, model.RecentRate);
        Assert.Contains("55.0 KB/s", model.SpeedAndEta);
        Assert.Contains("5.0 GB", model.OverallLabel);
        Assert.Contains("3.0 GB", model.FileTotalLabel);
        Assert.Equal("Model download elapsed: 0:31", model.Elapsed);
        model.Tick(Start.AddSeconds(61));
        Assert.True(model.Stalled);
        Assert.False(model.Slow);
        model.Update(Snapshot(31 * 55_000 + 2) with { LastReceivedAt = Start.AddSeconds(62) }, Start.AddSeconds(62));
        Assert.True(model.Slow); // A single byte cannot imply a healthy connection.
        model.ReconnectCommand.Execute(null);
        model.ReconnectCommand.Execute(null);
        Assert.Equal(1, reconnects);
        model.Update(Snapshot() with { Phase = DownloadPhase.Reconnecting }, Start.AddSeconds(63));
        model.Update(Snapshot(), Start.AddSeconds(64));
        Assert.False(model.Slow);
        Assert.Equal("Model download elapsed: 1:04", model.Elapsed);
        model.Update(Snapshot() with { Phase = DownloadPhase.Verifying }, Start.AddSeconds(70));
        model.Tick(Start.AddHours(1));
        Assert.False(model.Slow);
        Assert.Equal("Model download elapsed: 1:00:00", model.Elapsed);
        model.Clear();
        model.Update(Snapshot(), Start.AddHours(2));
        Assert.Equal("Model download elapsed: 0:00", model.Elapsed);
    }

    [Fact]
    public void New_file_resets_speed_and_small_remaining_downloads_do_not_warn()
    {
        var model = new DownloadViewModel();
        for (var second = 0; second <= 31; second++)
            model.Update(Snapshot(1 + second * 55_000) with
            {
                CurrentFileTotal = 1_200_000_000 + second * 55_000 + 100,
                LastReceivedAt = Start.AddSeconds(second),
            }, Start.AddSeconds(second));
        Assert.False(model.Slow);
        model.Update(Snapshot(20_000_000) with { CurrentFile = "next", LastReceivedAt = Start.AddSeconds(32) }, Start.AddSeconds(32));
        Assert.Equal(0, model.RecentRate);
        Assert.False(model.Slow);
    }

    [Fact]
    public void A_single_byte_is_visible_even_when_both_percentages_are_unchanged()
    {
        var model = new DownloadViewModel();
        model.Update(Snapshot(), Start);
        var overall = model.OverallPercent;
        var file = model.FilePercent;
        var bytes = model.OverallBytes;
        model.Update(Snapshot(2), Start);
        Assert.Equal(overall, model.OverallPercent);
        Assert.Equal(file, model.FilePercent);
        Assert.NotEqual(bytes, model.OverallBytes);
        Assert.Equal("Received 1 byte", model.Receipt);
    }

    [Fact]
    public void Silence_becomes_waiting_then_stalled_and_one_byte_recovers()
    {
        var model = new DownloadViewModel();
        model.Update(Snapshot() with { BytesPerSecond = 1000, Eta = TimeSpan.FromHours(1) }, Start);
        model.Tick(Start.AddSeconds(3));
        Assert.Equal("Waiting for more data", model.Receipt);
        model.Tick(Start.AddSeconds(30));
        Assert.True(model.Stalled);
        Assert.Equal("Download may be stalled", model.Receipt);
        Assert.DoesNotContain("KB", model.SpeedAndEta);
        model.Update(Snapshot(2) with { LastReceivedAt = Start.AddSeconds(31) }, Start.AddSeconds(31));
        Assert.False(model.Stalled);
        Assert.Equal("Received 1 byte", model.Receipt);
    }

    [Fact]
    public void Hashing_is_not_a_stalled_connection_and_unknown_totals_are_independent()
    {
        var model = new DownloadViewModel();
        model.Update(Snapshot() with { BytesTotal = 0 }, Start);
        Assert.Equal("Size unknown", model.OverallPercent);
        Assert.True(model.HasFileTotal);
        model.Update(Snapshot() with { Phase = DownloadPhase.Verifying }, Start.AddMinutes(5));
        Assert.False(model.Stalled);
        Assert.Equal("Checking model files", model.Receipt);
    }

    [AvaloniaFact]
    public async Task Stopping_transcription_setup_cannot_be_overwritten_by_a_late_receipt()
    {
        using var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            ReferenceAudioPath = "reference.wav",
        };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Transcribe = async (_, ct) =>
        {
            model.ReportTranscriptionDownload(Snapshot());
            await release.Task;
            ct.ThrowIfCancellationRequested();
            return "Reference words";
        };
        var run = model.ListenAgainCommand.ExecuteAsync(null);
        Assert.True(model.Download.Visible);
        model.StopCommand.Execute(null);
        model.ReportTranscriptionDownload(Snapshot(2));
        model.TickDownload();
        Assert.True(model.IsBusy);
        Assert.False(model.Download.Visible);
        Assert.Equal("Stopping…", model.Status);
        release.TrySetResult();
        await run;
        Assert.False(model.IsBusy);
        Assert.StartsWith("Stopped", model.Status);
    }

    [AvaloniaFact]
    public void The_window_has_two_named_meters_and_cannot_revert_to_download_after_loading()
    {
        var engine = new FakeEngine();
        using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog());
        var window = Open(new MainWindow { DataContext = model, Width = 620, Height = 580 });
        engine.Publish(new(EngineState.Downloading, Download: Snapshot()));
        Assert.True(model.Download.Visible);
        var meters = window.GetLogicalDescendants().OfType<ProgressBar>().ToArray();
        Assert.True(meters.Single(b => b.Name == "OverallDownloadProgress").IsEffectivelyVisible);
        Assert.True(meters.Single(b => b.Name == "CurrentFileDownloadProgress").IsEffectivelyVisible);
        engine.Publish(new(EngineState.Loading));
        model.TickDownload();
        Assert.False(model.Download.Visible);
        Assert.Equal("Loading the model…", model.Status);
    }
}
