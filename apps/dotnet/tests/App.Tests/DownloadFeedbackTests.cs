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
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-10T12:00:00Z");
    private static DownloadProgress Snapshot(long received = 1) => new(
        DownloadPhase.Downloading, BytesReceived: received, BytesReused: 2_350_000_000,
        BytesTotal: 5_000_000_000, CurrentFile: "model.onnx.data",
        CurrentFileBytes: 1_200_000_000 + received, CurrentFileTotal: 3_000_000_000,
        LastReceivedAt: Start, WaitingSince: Start);

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
