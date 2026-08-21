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

    // ---- Reading the pre-Main span from /proc (Linux) ----

    /// <summary>
    /// A /proc/self/stat line shaped like the real thing.
    /// </summary>
    /// <remarks>
    /// Field 22 is the start time in USER_HZ. The fields before it are real
    /// enough to be parsed wrongly by anything that splits the whole line on
    /// spaces, which is the point of having them.
    /// </remarks>
    private static string Stat(string startTicks, string name = "Bunyi.App") =>
        $"1234 ({name}) S 1200 1234 1234 0 -1 4194304 5000 0 0 0 120 30 0 0 20 0 12 0 {startTicks} "
        + "60000000 4000 18446744073709551615 1 1 0 0 0 0 0 0 0";

    private const string Uptime = "9000.42 71000.10\n";

    [Fact]
    public void The_time_before_Main_is_the_gap_between_the_two_proc_clocks()
    {
        // Both are counted from boot, so subtracting one from the other cancels
        // the boot time out — which is the whole reason to read them rather
        // than ask Process.StartTime, whose answer goes through /proc/stat's
        // btime and is therefore rounded to a whole second.
        // 9000.42 s up, started at 899_000 ticks = 8990.00 s: 10.42 s old.
        var age = StartupTimeline.AgeFromProc(Stat("899000"), Uptime);

        Assert.NotNull(age);
        Assert.Equal(10.42, age.Value.TotalSeconds, precision: 2);
    }

    [Fact]
    public void The_resolution_is_better_than_the_second_that_btime_gives()
    {
        // The reading this replaced said "41 ms" for a phase that could have
        // been anything up to a second. Two starts a hundredth of a second
        // apart have to come out a hundredth of a second apart.
        var earlier = StartupTimeline.AgeFromProc(Stat("899000"), Uptime);
        var later = StartupTimeline.AgeFromProc(Stat("899001"), Uptime);

        Assert.NotNull(earlier);
        Assert.NotNull(later);
        // To the millisecond, not beyond it: the arithmetic is in doubles and
        // the claim is about a hundredth of a second against a whole one.
        Assert.Equal(10, (earlier.Value - later.Value).TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void An_executable_name_with_spaces_and_brackets_is_not_read_as_fields()
    {
        // The second field is the executable's name, and a name may contain
        // both spaces and brackets. Counting from the start of the line — the
        // classic way to read this file wrong — would take a piece of the name
        // as the start time here.
        var age = StartupTimeline.AgeFromProc(Stat("899000", "My App (beta) 2"), Uptime);

        Assert.NotNull(age);
        Assert.Equal(10.42, age.Value.TotalSeconds, precision: 2);
    }

    [Theory]
    [InlineData("", Uptime)]
    [InlineData("1234 (Bunyi.App) S 1 2 3", Uptime)]
    [InlineData("no brackets here at all", Uptime)]
    public void An_unreadable_stat_line_says_nothing_rather_than_guessing(
        string stat, string uptime)
    {
        // A phase that is wrong without looking wrong is worse than a phase
        // that is missing: the summary line reports a missing one as a lower
        // bound, and a bad one as fact.
        Assert.Null(StartupTimeline.AgeFromProc(stat, uptime));
    }

    [Fact]
    public void A_process_that_reads_as_older_than_the_machine_is_rejected()
    {
        // Started 9000 seconds before a boot 9000.42 seconds ago is not a
        // process, it is a misread.
        Assert.Null(StartupTimeline.AgeFromProc(Stat("-100"), Uptime));
        Assert.Null(StartupTimeline.AgeFromProc(Stat("899000"), "5.00 1.00"));
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
