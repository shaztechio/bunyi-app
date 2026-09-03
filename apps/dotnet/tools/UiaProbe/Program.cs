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
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Bunyi.UiaProbe;

/// <summary>
/// Reads the Windows UI Automation tree of a running Bunyi, on the far side of
/// the bridge from the automation peers the headless tests assert on (#192).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>ScreenReaderTests</c>, <c>AccessibleNameTests</c>
/// and their neighbours assert what Avalonia's <c>AutomationPeer</c> objects
/// return. Narrator reads the Windows UIA tree. Those are different layers, and
/// a green test at the first says nothing about the second. This walks the
/// second, on a real window, in a real desktop session.
/// </para>
/// <para>
/// It still is not Narrator. It shows the tree Narrator walks and the values
/// Narrator reads; what Narrator chooses to speak is its own, and stays a
/// manual pass (#159). What this rules out is the failure mode #192 was filed
/// for: a property set, asserted, and never crossing the bridge at all.
/// </para>
/// </remarks>
internal static partial class Program
{
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    private static int Main(string[] args)
    {
        var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "check";

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "This reads the Windows UI Automation tree and only runs on Windows. "
                + "Orca on Linux is a manual pass; see tools/UiaProbe/README.md.");
            return 2;
        }

        var pidArg = args.FirstOrDefault(a => a.StartsWith("--pid=", StringComparison.Ordinal));
        var process = FindBunyi(pidArg?["--pid=".Length..]);
        if (process is null) return 2;

        var windows = Tree.WindowsOf(process.Id);
        if (windows.Count == 0)
        {
            Console.Error.WriteLine(
                $"Bunyi (pid {process.Id}) is running but shows no window UI Automation can see. "
                + "If it was started elevated, run this elevated too.");
            return 2;
        }

        Console.WriteLine($"Bunyi UIA probe — {process.ProcessName}, pid {process.Id}, "
            + $"{windows.Count} window(s)\n");

        switch (command)
        {
            case "tree":
                foreach (var window in windows) Tree.Print(window);
                return 0;

            case "live":
                var seconds = int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 60;
                return WatchLiveRegion(process.MainWindowHandle, seconds);

            case "watch":
                var watchFor = int.TryParse(args.ElementAtOrDefault(1), out var w) ? w : 60;
                return WatchProperties(process.MainWindowHandle, watchFor);

            case "check":
                return Check(windows, process.MainWindowHandle);

            default:
                Console.Error.WriteLine(
                    $"Unknown command '{command}'. Use: tree | check | live [seconds] | watch [seconds]");
                return 2;
        }
    }

    // ---- Finding the app ----

    private static Process? FindBunyi(string? pid)
    {
        if (pid is not null)
        {
            try
            {
                return Process.GetProcessById(int.Parse(pid));
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                Console.Error.WriteLine($"No process with pid {pid}.");
                return null;
            }
        }

        var found = Process.GetProcessesByName("Bunyi.App");
        switch (found.Length)
        {
            case 0:
                Console.Error.WriteLine(
                    "Bunyi is not running. Start it first — this reads a live window, which is the "
                    + "whole point — then run this again.");
                return null;

            case 1:
                return found[0];

            default:
                Console.Error.WriteLine(
                    $"{found.Length} copies of Bunyi are running. Choose one with "
                    + $"--pid=<{string.Join('|', found.Select(p => p.Id))}>.");
                return null;
        }
    }

    // ---- The checks ----

    private static int Check(List<AutomationElement> windows, nint mainWindow)
    {
        var main = windows.FirstOrDefault(w => w.Current.Name == "Bunyi") ?? windows[0];

        CheckHeaderButtons(main);
        CheckDropdowns(main);
        CheckLiveRegion(mainWindow);
        CheckHistory(main);
        CheckDoctor(windows, main);

        Console.WriteLine($"\n{_passed} passed, {_failed} failed, {_skipped} skipped");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The four header buttons carry a name and the chord that opens them (#185, #188).
    /// </summary>
    private static void CheckHeaderButtons(AutomationElement main)
    {
        Console.WriteLine("Header buttons — a name, and the chord as an accelerator");

        var expected = new (string Starts, string Accelerator)[]
        {
            ("Settings", "Ctrl+,"),
            ("Doctor", "Ctrl+D"),
            ("Logs", "Ctrl+L"),
            ("Help", "F1"),
        };

        var buttons = Tree.Descendants(main)
            .Where(e => e.Current.ControlType == ControlType.Button)
            .ToList();

        foreach (var (starts, accelerator) in expected)
        {
            var button = buttons.FirstOrDefault(b =>
                b.Current.Name.StartsWith(starts, StringComparison.Ordinal));

            if (button is null)
            {
                Fail($"no button named '{starts}…'");
                continue;
            }

            var name = button.Current.Name;

            // The failure this replaces: a name of "Avalonia.Controls.Shapes.Path",
            // which is a namespace read aloud rather than a label.
            if (name.Contains("Avalonia.", StringComparison.Ordinal))
                Fail($"'{starts}' is named after its content type: '{name}'");
            else if (button.Current.AcceleratorKey != accelerator)
                Fail($"'{name}' advertises accelerator '{button.Current.AcceleratorKey}', expected '{accelerator}'");
            else
                Pass($"{name}  (accel={accelerator})");
        }
    }

    /// <summary>
    /// Every dropdown says what it is, and what it is set to.
    /// </summary>
    /// <remarks>
    /// Reported from using the app with Narrator: arrowing through a dropdown
    /// changed the value and said nothing. Both halves of that are checked
    /// here — the box was also unnamed, so it announced as a bare "combo box".
    /// <para>
    /// ItemStatus carrying a value is necessary but not sufficient; that it
    /// <i>changes</i> is what a reader acts on, and that is <c>watch</c>.
    /// </para>
    /// </remarks>
    private static void CheckDropdowns(AutomationElement main)
    {
        Console.WriteLine("\nDropdowns — named, and saying what they are set to");

        var combos = Tree.Descendants(main)
            .Where(e => e.Current.ControlType == ControlType.ComboBox)
            .ToList();

        if (combos.Count == 0)
        {
            Skip("no dropdown is on screen in this mode.");
            return;
        }

        foreach (var combo in combos)
        {
            var name = combo.Current.Name;
            var status = combo.GetCurrentPropertyValue(AutomationElement.ItemStatusProperty) as string;

            if (string.IsNullOrWhiteSpace(name))
                Fail($"an unnamed combo box (set to '{status}') — a reader announces it as just \"combo box\"");
            else if (string.IsNullOrWhiteSpace(status))
                Fail($"'{name}' serves no ItemStatus, so a selection change announces nothing");
            else
                Pass($"{name} — ItemStatus='{status}'");
        }
    }

    /// <summary>
    /// The status line's <c>LiveSetting</c>, read back over UIA (#192, defect 1).
    /// </summary>
    /// <remarks>
    /// This is the assertion the headless tests cannot make. They can show the
    /// property reached the peer; only a UIA client can show it crossed into
    /// the tree a screen reader reads, as <c>UIA_LiveSettingPropertyId</c>.
    /// </remarks>
    private static void CheckLiveRegion(nint window)
    {
        Console.WriteLine("\nStatus line — a live region on the far side of the bridge");

        var live = LiveRegion.Read(window);

        if (live.Count == 0)
        {
            Fail("no element in the window serves UIA_LiveSettingPropertyId with anything but Off. "
                + "Either the property is not set, or Avalonia is not bridging it — and a live "
                + "region that is not bridged is a status line no screen reader will ever announce");
            return;
        }

        foreach (var (name, setting) in live) Pass($"'{name}' — LiveSetting={setting}");

        Console.WriteLine(
            "      A value, though, only says the app asked to be announced. Whether the toolkit\n"
            + "      raises LiveRegionChanged when the text changes is the other half, and that is\n"
            + "      what `live` measures — it needs a generation running, so it is its own command.");
    }

    /// <summary>A History row is one item, with its buttons as children and no loose text.</summary>
    private static void CheckHistory(AutomationElement main)
    {
        Console.WriteLine("\nHistory — one item per row");

        // Not every ListItem in the window is a History row: the mode picker is
        // a List of four, and an earlier version of this check failed on those.
        // A History row is the one that carries its five per-clip buttons, and
        // its name is the composed sentence rather than a single mode word — so
        // match on the separator that sentence is built with.
        var rows = Tree.Descendants(main)
            .Where(e => e.Current.ControlType == ControlType.ListItem)
            .Where(e => e.Current.Name.Contains(" · ", StringComparison.Ordinal))
            .ToList();

        if (rows.Count == 0)
        {
            Skip("History is not showing, or holds no clips. Switch to History and run again.");
            return;
        }

        foreach (var row in rows.Take(3))
        {
            var children = Tree.Children(row).ToList();
            var text = children.Where(c => c.Current.ControlType == ControlType.Text).ToList();
            var buttons = children.Where(c => c.Current.ControlType == ControlType.Button).ToList();

            if (text.Count > 0)
                Fail($"row '{row.Current.Name}' still announces {text.Count} loose text element(s): "
                    + string.Join(", ", text.Select(t => $"'{t.Current.Name}'")));
            else if (buttons.Count != 5)
                Fail($"row '{row.Current.Name}' has {buttons.Count} buttons, expected 5");
            else
                Pass($"{row.Current.Name}  (5 buttons, no loose text)");
        }
    }

    /// <summary>
    /// Every Doctor finding says its severity in the same breath as its detail
    /// (#192, defect 2).
    /// </summary>
    private static void CheckDoctor(List<AutomationElement> windows, AutomationElement main)
    {
        Console.WriteLine("\nDoctor — severity in words, with the finding it belongs to");

        // Both places, because Avalonia's owned dialogs are not where a Win32
        // habit expects them: the report shows up as a Window *inside* the main
        // window's UIA subtree, not as another child of the desktop. Looking
        // only at the top level found nothing while the dialog was plainly open.
        var report = windows.Concat(Tree.Descendants(main))
            .FirstOrDefault(w => w.Current.ControlType == ControlType.Window
                && w.Current.Name.StartsWith("Doctor", StringComparison.Ordinal));

        if (report is null)
        {
            Skip("no Doctor report is open. Press Ctrl+D, wait for it, and run again.");
            return;
        }

        var findings = Tree.Descendants(report)
            .Where(e => e.Current.ControlType == ControlType.Text)
            .Select(e => e.Current.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        if (findings.Count == 0)
        {
            Fail("the report announces no text at all");
            return;
        }

        foreach (var finding in findings)
        {
            // The failure: a detail announced on its own, so a reader can say
            // "the model is downloaded and ready" and never say "OK".
            if (Severity().IsMatch(finding)) Pass(finding);
            else Fail($"announced without a severity: '{finding}'");
        }
    }

    // ---- The live-region event ----

    /// <summary>
    /// Watches for <c>LiveRegionChanged</c>, which is the half of a live region
    /// that a property value cannot prove.
    /// </summary>
    private static int WatchLiveRegion(nint window, int seconds)
    {
        Console.WriteLine(
            $"Listening for LiveRegionChanged (UIA event 20024) for {seconds}s.\n"
            + "Do something in the app that moves the status line — generating is the case that\n"
            + "matters, but any change to it will do.\n");

        var heard = LiveRegion.Watch(
            window,
            TimeSpan.FromSeconds(seconds),
            name => Console.WriteLine($"  LiveRegionChanged  '{name}'"));

        Console.WriteLine();

        if (heard.Count > 0)
        {
            Console.WriteLine(
                $"{heard.Count} LiveRegionChanged event(s). The status line does announce: Avalonia's\n"
                + "Win32 bridge raised the event a screen reader listens for, on the real window.");
            return 0;
        }

        Console.WriteLine(
            "No LiveRegionChanged event arrived.\n"
            + "That is a failure only if the status actually changed while this was listening. If\n"
            + "nothing moved it, nothing had anything to announce — run it again and make it move.");
        return 1;
    }

    // ---- Property changes ----

    /// <summary>
    /// Reports the property changes a screen reader would act on, as they happen.
    /// </summary>
    /// <remarks>
    /// For "I changed it and nothing was spoken". A value read back at rest
    /// cannot tell a control that announces itself from one that updates in
    /// silence; only the event can, and a ComboBox whose selection moved
    /// without one is the case this was written for.
    /// </remarks>
    private static int WatchProperties(nint window, int seconds)
    {
        Console.WriteLine(
            $"Watching {string.Join(", ", PropertyWatch.Interesting.Select(p => p.ToString().Replace("UIA_", "", StringComparison.Ordinal)))}\n"
            + $"for {seconds}s. Move something — arrow through a dropdown, switch mode, start a run.\n");

        var heard = PropertyWatch.Watch(
            window,
            PropertyWatch.Interesting,
            TimeSpan.FromSeconds(seconds),
            line => Console.WriteLine($"  {line}"));

        Console.WriteLine();

        if (heard.Count > 0)
        {
            Console.WriteLine($"{heard.Count} property change(s) reached the UIA tree.");
            return 0;
        }

        Console.WriteLine(
            "Nothing changed in the UIA tree.\n"
            + "If you did move a control, that is the bug: it updated on screen and told\n"
            + "assistive technology nothing.");
        return 1;
    }

    // ---- Reporting ----

    private static void Pass(string what)
    {
        _passed++;
        Console.WriteLine($"  PASS  {what}");
    }

    private static void Fail(string what)
    {
        _failed++;
        Console.WriteLine($"  FAIL  {what}");
    }

    private static void Skip(string what)
    {
        _skipped++;
        Console.WriteLine($"  SKIP  {what}");
    }

    [GeneratedRegex("^(Blocker|Warning|OK): ")]
    private static partial Regex Severity();
}
