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
using Bunyi.Core.Models;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bunyi.Core.Tests;

public class DownloadProgressTests
{
    [Fact]
    public void Reused_bytes_count_toward_the_fraction()
    {
        // A resumed download that skipped 4 GB must not show 0% while it
        // fetches the last 200 MB.
        var progress = new DownloadProgress(
            DownloadPhase.Downloading, BytesReceived: 200, BytesReused: 800, BytesTotal: 1000);

        Assert.Equal(1.0, progress.Fraction);
    }

    [Fact]
    public void An_unknown_total_reports_no_fraction_rather_than_a_wrong_one()
    {
        var progress = new DownloadProgress(DownloadPhase.Downloading, BytesReceived: 500);

        Assert.Equal(0, progress.Fraction);
        Assert.Equal("Downloading…", progress.Human());
    }

    [Fact]
    public void The_fraction_never_leaves_its_range()
    {
        // Sizes are estimates from HEAD; a server that under-reports must not
        // produce a bar past its end.
        var progress = new DownloadProgress(
            DownloadPhase.Downloading, BytesReceived: 5_000, BytesTotal: 1_000);

        Assert.Equal(1.0, progress.Fraction);
    }

    [Fact]
    public void The_human_line_reads_like_the_macOS_one()
    {
        // §3b gives the wording: "42% — about 3.1 MB/s, ~6 min left".
        var progress = new DownloadProgress(
            DownloadPhase.Downloading,
            BytesReceived: 4_200_000,
            BytesTotal: 10_000_000,
            BytesPerSecond: 3_100_000,
            Eta: TimeSpan.FromMinutes(6));

        Assert.Equal("42% — about 3.1 MB/s, ~6 min left", progress.Human());
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(999, "999 bytes")]
    [InlineData(1_000, "1.0 KB")]
    [InlineData(1_500_000, "1.5 MB")]
    [InlineData(5_880_000_000, "5.9 GB")]
    [InlineData(150_000_000, "150 MB")]
    public void Sizes_are_written_the_way_a_file_manager_writes_them(long bytes, string expected) =>
        Assert.Equal(expected, DownloadProgress.Bytes(bytes));

    [Theory]
    [InlineData(30, "under a minute left")]
    [InlineData(89, "under a minute left")]
    [InlineData(360, "~6 min left")]
    [InlineData(7200, "~2 hours left")]
    public void Time_remaining_is_phrased_as_the_estimate_it_is(int seconds, string expected) =>
        Assert.Equal(expected, DownloadProgress.EtaText(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void An_unknown_time_remaining_says_so_rather_than_guessing() =>
        Assert.Equal("time left unknown", DownloadProgress.EtaText(null));

    [Fact]
    public void Each_phase_says_what_it_is_doing()
    {
        Assert.Equal("Looking for a file list…", new DownloadProgress(DownloadPhase.Manifest).Human());
        Assert.Equal("Working out the download size…", new DownloadProgress(DownloadPhase.Sizing).Human());
        Assert.Equal("Done", new DownloadProgress(DownloadPhase.Done).Human());
    }
}

/// <summary>
/// §3b's 10 s progress log and 30 s stall warning, on a fake clock.
/// </summary>
public class StallMonitorTests
{
    [Fact]
    public void Progress_is_logged_every_ten_seconds_while_bytes_arrive()
    {
        var time = new FakeTimeProvider();
        var log = new RecordingLog();
        using var monitor = new StallMonitor(log, time);

        monitor.Add(1_000_000);
        time.Advance(StallMonitor.LogInterval);

        Assert.Contains(log.Lines, l => l.Contains("1.0 MB received"));
    }

    [Fact]
    public void Nothing_is_said_while_bytes_keep_arriving()
    {
        var time = new FakeTimeProvider();
        var log = new RecordingLog();
        using var monitor = new StallMonitor(log, time);

        for (var i = 0; i < 6; i++)
        {
            monitor.Add(500_000);
            time.Advance(StallMonitor.LogInterval);
        }

        Assert.DoesNotContain(log.Lines, l => l.Contains("stalled"));
    }

    [Fact]
    public void A_warning_appears_after_thirty_seconds_without_a_byte()
    {
        var time = new FakeTimeProvider();
        var log = new RecordingLog();
        using var monitor = new StallMonitor(log, time);

        monitor.Add(1_000);
        time.Advance(StallMonitor.LogInterval);           // progress
        time.Advance(StallMonitor.StallAfter);            // then silence

        Assert.Contains(log.Lines, l => l.Contains("No new data for 30 s"));
    }

    [Fact]
    public void The_warning_is_said_once_not_every_ten_seconds()
    {
        // A warning that repeats forever trains people to ignore it.
        var time = new FakeTimeProvider();
        var log = new RecordingLog();
        using var monitor = new StallMonitor(log, time);

        monitor.Add(1_000);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Single(log.Lines, l => l.Contains("No new data"));
    }

    [Fact]
    public void Bytes_arriving_again_clear_the_warning_so_a_later_stall_is_reported()
    {
        var time = new FakeTimeProvider();
        var log = new RecordingLog();
        using var monitor = new StallMonitor(log, time);

        monitor.Add(1_000);
        time.Advance(TimeSpan.FromMinutes(2));            // stalls, warns once
        monitor.Add(1_000);
        time.Advance(StallMonitor.LogInterval);           // recovers
        time.Advance(TimeSpan.FromMinutes(2));            // stalls again

        Assert.Equal(2, log.Lines.Count(l => l.Contains("No new data")));
    }

    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }
        public void Log(string message) { lock (_lines) _lines.Add(message); }
    }
}
