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
using Xunit;

namespace Bunyi.Core.Tests;

public class StartupTimelineTests
{
    [Fact]
    public void Each_phase_is_reported_as_its_own_span_not_a_running_total()
    {
        // The whole point of the line: "platform 900 ms" has to mean that phase
        // took 900 ms, not that it ended 900 ms in. A running total would name
        // the wrong phase as the slow one on every machine.
        var clock = new FakeClock();
        var timeline = new StartupTimeline(TimeSpan.FromMilliseconds(400), () => clock.Elapsed);

        clock.Advance(900);
        timeline.Mark("platform");
        clock.Advance(120);
        timeline.Mark("theme");
        clock.Advance(30);
        timeline.Mark("app");

        var summary = timeline.Summary();

        Assert.Contains("runtime 400 ms", summary, StringComparison.Ordinal);
        Assert.Contains("platform 900 ms", summary, StringComparison.Ordinal);
        Assert.Contains("theme 120 ms", summary, StringComparison.Ordinal);
        Assert.Contains("app 30 ms", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_total_covers_the_time_before_Main_as_well()
    {
        var clock = new FakeClock();
        var timeline = new StartupTimeline(TimeSpan.FromMilliseconds(400), () => clock.Elapsed);

        clock.Advance(600);
        timeline.Mark("platform");

        Assert.StartsWith("Startup: 1000 ms to platform", timeline.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_total_missing_the_time_before_Main_is_reported_as_a_lower_bound()
    {
        // A host that will not report a process start time costs the line one
        // phase. Printing the remainder as if it were the whole start would
        // understate exactly the machine whose runtime is slowest to come up.
        var clock = new FakeClock();
        var timeline = new StartupTimeline(beforeMain: null, () => clock.Elapsed);

        clock.Advance(600);
        timeline.Mark("platform");

        var summary = timeline.Summary();

        Assert.StartsWith("Startup: at least 600 ms to platform", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("runtime", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Reporting_closes_off_a_final_phase_and_writes_one_line()
    {
        var clock = new FakeClock();
        var log = new RecordingLog();
        var timeline = new StartupTimeline(TimeSpan.FromMilliseconds(100), () => clock.Elapsed);

        clock.Advance(200);
        timeline.Mark("platform");
        clock.Advance(50);
        timeline.Report(log);

        var line = Assert.Single(log.Lines);
        Assert.Equal(
            "Startup: 350 ms to first frame — runtime 100 ms, platform 200 ms, first frame 50 ms.",
            line);
    }

    [Fact]
    public void A_window_shown_a_second_time_does_not_write_a_second_startup_line()
    {
        // Opened can fire again in a long-lived process. A second line would
        // describe a reopen while claiming to describe a start.
        var clock = new FakeClock();
        var log = new RecordingLog();
        var timeline = new StartupTimeline(TimeSpan.Zero, () => clock.Elapsed);

        clock.Advance(300);
        timeline.Report(log);

        clock.Advance(5_000);
        timeline.Report(log);
        timeline.Mark("late");

        var line = Assert.Single(log.Lines);
        Assert.DoesNotContain("late", line, StringComparison.Ordinal);
        Assert.DoesNotContain("5000 ms", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_reads_the_same_wherever_the_app_is_run()
    {
        // §8 lines get copied into bug reports. A locale that writes 1 234 ms,
        // or a comma for the decimal point, makes two reports incomparable.
        using var culture = new CultureSwap("de-DE");

        var clock = new FakeClock();
        var timeline = new StartupTimeline(TimeSpan.FromMilliseconds(1234.6), () => clock.Elapsed);

        clock.Advance(2000);
        timeline.Mark("platform");

        Assert.Equal(
            "Startup: 3235 ms to platform — runtime 1235 ms, platform 2000 ms.",
            timeline.Summary());
    }

    private sealed class FakeClock
    {
        public TimeSpan Elapsed { get; private set; }

        public void Advance(double milliseconds) =>
            Elapsed += TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed class CultureSwap : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous =
            System.Globalization.CultureInfo.CurrentCulture;

        public CultureSwap(string name) =>
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo(name);

        public void Dispose() =>
            System.Globalization.CultureInfo.CurrentCulture = _previous;
    }

    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;

        public void Log(string message) => _lines.Add(message);
    }
}
