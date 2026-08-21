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

using System.Diagnostics;
using System.Globalization;

namespace Bunyi.Core.Diagnostics;

/// <summary>
/// How long the app took to put a window on screen, phase by phase.
/// </summary>
/// <remarks>
/// <para>
/// One line in the log (spec §8), so "it is slow to start" arrives in a bug
/// report as a number with the slow phase named. The phases are chosen to tell
/// apart the things that actually differ between machines: the runtime and JIT
/// before <c>Main</c>, the windowing and rendering subsystems Avalonia brings
/// up before the app sees control — X11, GL, DBus and the font manager, which
/// is where Linux diverges from Windows — the theme and XAML load, and this
/// app's own composition root, which does almost nothing by comparison.
/// </para>
/// <para>
/// The clock is a <see cref="Stopwatch"/> rather than wall time, so a clock
/// adjustment mid-start cannot produce a negative phase. Only the span before
/// <c>Main</c> is wall time, because there is no monotonic reading from before
/// the process existed.
/// </para>
/// </remarks>
public sealed class StartupTimeline
{
    private readonly object _gate = new();
    private readonly List<(string Name, TimeSpan Took)> _phases = [];
    private readonly Func<TimeSpan> _elapsed;
    private readonly TimeSpan? _beforeMain;
    private TimeSpan _mark;
    private bool _reported;

    /// <summary>Creates a timeline over an explicit clock.</summary>
    /// <param name="beforeMain">
    /// How long the process existed before the timeline started, or null when
    /// that cannot be read. Null is reported honestly rather than as zero: a
    /// missing phase makes the total a lower bound, and saying so is the point.
    /// </param>
    /// <param name="elapsed">Time since the timeline started. Must not go backwards.</param>
    public StartupTimeline(TimeSpan? beforeMain, Func<TimeSpan> elapsed)
    {
        ArgumentNullException.ThrowIfNull(elapsed);
        _beforeMain = beforeMain;
        _elapsed = elapsed;
    }

    /// <summary>
    /// Starts a timeline now, measuring back to when the process started.
    /// </summary>
    /// <remarks>
    /// The process start time is read before the stopwatch starts, so the cost
    /// of reading it — a /proc file on Linux — is counted against the runtime
    /// phase it belongs to rather than against the phase being measured next.
    /// </remarks>
    public static StartupTimeline FromProcessStart()
    {
        var beforeMain = SinceProcessStart();
        var stopwatch = Stopwatch.StartNew();
        return new StartupTimeline(beforeMain, () => stopwatch.Elapsed);
    }

    /// <summary>Closes off a phase under the given name.</summary>
    public void Mark(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var now = _elapsed();

        lock (_gate)
        {
            if (_reported) return;
            _phases.Add((name, now - _mark));
            _mark = now;
        }
    }

    /// <summary>
    /// Closes off the last phase and writes the one summary line, once.
    /// </summary>
    /// <remarks>
    /// Once, because a window can be shown more than once in a process — a
    /// second line would describe a reopen while claiming to describe a start.
    /// </remarks>
    public void Report(ILogSink log, string finalPhase = "first frame")
    {
        ArgumentNullException.ThrowIfNull(log);

        Mark(finalPhase);

        string summary;
        lock (_gate)
        {
            if (_reported) return;
            _reported = true;
            summary = Summarise();
        }

        log.Log(summary);
    }

    /// <summary>The line <see cref="Report"/> writes, for tests to assert on.</summary>
    public string Summary()
    {
        lock (_gate) return Summarise();
    }

    /// <summary>Builds the summary. Callers hold <see cref="_gate"/>.</summary>
    private string Summarise()
    {
        var parts = new List<string>(_phases.Count + 1);
        if (_beforeMain is { } before) parts.Add(Describe("runtime", before));
        foreach (var (name, took) in _phases) parts.Add(Describe(name, took));

        var total = (_beforeMain ?? TimeSpan.Zero) + _mark;

        // Without the pre-Main span the total is missing a phase, and reporting
        // it as if it were the whole start would understate exactly the machine
        // where the runtime is slowest to come up.
        var bound = _beforeMain is null ? "at least " : string.Empty;

        return parts.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Startup: {bound}{Milliseconds(total)}.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Startup: {bound}{Milliseconds(total)} to {_phases[^1].Name} — {string.Join(", ", parts)}.");
    }

    private static string Describe(string name, TimeSpan took) =>
        string.Create(CultureInfo.InvariantCulture, $"{name} {Milliseconds(took)}");

    /// <summary>
    /// Whole milliseconds: startup is a millisecond-scale thing, and integers
    /// are what makes two runs comparable at a glance.
    /// </summary>
    private static string Milliseconds(TimeSpan span) =>
        string.Create(CultureInfo.InvariantCulture, $"{(long)Math.Round(span.TotalMilliseconds)} ms");

    /// <summary>
    /// How long this process has been running, or null if that cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Linux is read from <c>/proc</c> rather than through
    /// <see cref="Process.StartTime"/>, which on that platform is the boot time
    /// from <c>/proc/stat</c>'s <c>btime</c> plus the process's own offset.
    /// <c>btime</c> is a whole number of seconds, so the answer carries up to a
    /// second of quantisation — which on a start that takes about a second
    /// and a half is not a rounding error, it is the measurement. The first
    /// Linux reading this produced said 41 ms for a phase that could have been
    /// anything up to a second.
    /// </para>
    /// <para>
    /// Defensive throughout: this is a diagnostic, and a container or a
    /// hardened host that will not report a start time must cost the app a
    /// phase of a log line, not its ability to start. UTC on both sides of the
    /// fallback, so a start either side of a daylight-saving change cannot
    /// produce an hour of imaginary startup.
    /// </para>
    /// </remarks>
    private static TimeSpan? SinceProcessStart()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var fromProc = AgeFromProc(
                    File.ReadAllText("/proc/self/stat"),
                    File.ReadAllText("/proc/uptime"));

                if (fromProc is not null) return fromProc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through: an unreadable /proc is not worth a second
                // failure mode when the portable path is right there.
            }
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            var since = DateTime.UtcNow - process.StartTime.ToUniversalTime();
            return since >= TimeSpan.Zero ? since : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// How long a process has been running, from the two <c>/proc</c> files
    /// that answer it without going through the boot clock.
    /// </summary>
    /// <param name="selfStat">The contents of <c>/proc/self/stat</c>.</param>
    /// <param name="uptime">The contents of <c>/proc/uptime</c>.</param>
    /// <remarks>
    /// <para>
    /// Both are measured from boot, so subtracting one from the other cancels
    /// the boot time out. <c>/proc/uptime</c> is given to a hundredth of a
    /// second, which is the resolution this ends up with — two orders
    /// better than the second that <c>btime</c> is rounded to.
    /// </para>
    /// <para>
    /// The starting field is counted from the last <c>)</c> rather than from
    /// the start of the line, because the second field is the executable's name
    /// and a name may contain both spaces and brackets. Splitting the whole
    /// line on spaces is the classic way to read this file wrong.
    /// </para>
    /// <para>
    /// The unit is <c>USER_HZ</c>, which the kernel fixes at 100 for everything
    /// it exports here regardless of its own tick rate — it is an ABI
    /// constant, not a property of the machine.
    /// </para>
    /// </remarks>
    internal static TimeSpan? AgeFromProc(string selfStat, string uptime)
    {
        const double UserHz = 100.0;

        if (selfStat is null || uptime is null) return null;

        var afterName = selfStat.LastIndexOf(')');
        if (afterName < 0) return null;

        // Field 22 is the process start time. The split below begins at field
        // 3, the state, so the one wanted is nineteen along from it.
        var fields = selfStat[(afterName + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length <= 19) return null;

        if (!double.TryParse(fields[19], NumberStyles.Float, CultureInfo.InvariantCulture, out var startedTicks)) return null;

        var secondsField = uptime.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!double.TryParse(secondsField, NumberStyles.Float, CultureInfo.InvariantCulture, out var upSeconds)) return null;

        var age = upSeconds - (startedTicks / UserHz);

        // A negative age, or one longer than the machine has been up, means
        // something was misread. Saying nothing is better than a phase that is
        // wrong without looking wrong.
        return age >= 0 && age <= upSeconds ? TimeSpan.FromSeconds(age) : null;
    }
}
