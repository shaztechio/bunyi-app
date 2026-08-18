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
using Avalonia.VisualTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Doctor as the user meets it (spec §11).
/// </summary>
public class DoctorUiTests
{
    private static DoctorReport Report(params DoctorFinding[] findings) =>
        new(TtsMode.PresetVoice, findings);

    private static (MainWindow Window, MainViewModel Model) Open(
        Func<TtsMode, bool, CancellationToken, Task<DoctorReport>>? doctor = null,
        RecordingLog? log = null)
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), log ?? new RecordingLog())
        {
            Doctor = doctor,
        };

        var window = new MainWindow { DataContext = model };
        window.Show();
        return (window, model);
    }

    private static Button DoctorButton(Window window) =>
        window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "DoctorButton");

    [AvaloniaFact]
    public void Doctor_is_offered_before_the_first_generation()
    {
        // §11 is a preflight: the answer is worth having on a machine that has
        // never run anything, which is exactly when the disk check matters.
        var (window, _) = Open();

        Assert.True(DoctorButton(window).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Doctor_stays_available_while_a_generation_runs()
    {
        // §10: Doctor, Logs and Help sit outside the scope that goes dead while
        // work runs. Losing them during a long download is losing them exactly
        // when someone would reach for them.
        var (window, model) = Open();

        model.Status = "Generating";
        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));

        Assert.True(DoctorButton(window).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Doctor_Logs_and_Help_sit_together_in_that_order()
    {
        // §11 orders them Doctor, Logs, Help — by how far the answer is from
        // the app: whether it can run at all, what it did, what it is. Asserted
        // as three adjacent buttons rather than three in relative order,
        // because "one group" is the part that a later button dropped into the
        // header would quietly break.
        var (window, _) = Open();

        var header = window.GetLogicalDescendants().OfType<Button>()
            .Select(b => b.Name)
            .Where(n => n is not null)
            .ToList();

        var doctor = header.IndexOf("DoctorButton");

        Assert.True(doctor >= 0, "no Doctor button in the header");
        Assert.Equal(
            ["DoctorButton", "LogsButton", "HelpButton"],
            header.Skip(doctor).Take(3));
    }

    [AvaloniaFact]
    public void The_button_carries_a_tooltip_because_the_glyph_alone_is_not_obvious()
    {
        var tip = ToolTip.GetTip(DoctorButton(Open().Window));

        Assert.Contains("Doctor", Assert.IsType<string>(tip));
    }

    [AvaloniaFact]
    public async Task An_on_demand_run_asks_for_the_slow_check_too()
    {
        // The one place §11 wants integrity verified: asked for, not on every
        // generation.
        var deep = false;
        var (_, model) = Open((_, d, _) =>
        {
            deep = d;
            return Task.FromResult(Report());
        });

        await model.RunDoctorAsync();

        Assert.True(deep);
    }

    [AvaloniaFact]
    public async Task An_on_demand_run_reports_on_the_mode_on_screen()
    {
        TtsMode? asked = null;
        var (_, model) = Open((m, _, _) =>
        {
            asked = m;
            return Task.FromResult(Report());
        });

        model.Mode = TtsMode.VoiceClone;
        await model.RunDoctorAsync();

        Assert.Equal(TtsMode.VoiceClone, asked);
    }

    [AvaloniaFact]
    public async Task The_findings_also_go_to_the_log()
    {
        // So they can be copied into a bug report without the dialog open (§8).
        var log = new RecordingLog();
        var (_, model) = Open(
            (_, _, _) => Task.FromResult(Report(
                new DoctorFinding("Disk space", "No room.", DoctorSeverity.Blocker))),
            log);

        await model.RunDoctorAsync();

        Assert.Contains(log.Lines, l => l.Contains("No room."));
    }

    [AvaloniaFact]
    public void The_report_puts_blockers_first()
    {
        // What stops the run is what the reader needs, whatever order the checks
        // happen to run in.
        var panel = MainWindow.BuildFindings(Report(
            new DoctorFinding("Model", "Present.", DoctorSeverity.Ok),
            new DoctorFinding("Memory", "Tight.", DoctorSeverity.Warning),
            new DoctorFinding("Disk space", "No room.", DoctorSeverity.Blocker)));

        var titles = panel.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => t is "Model" or "Memory" or "Disk space")
            .ToList();

        Assert.Equal(["Disk space", "Memory", "Model"], titles);
    }

    [AvaloniaFact]
    public void Every_finding_is_marked_with_what_it_means()
    {
        var panel = MainWindow.BuildFindings(Report(
            new DoctorFinding("Model", "Present.", DoctorSeverity.Ok),
            new DoctorFinding("Memory", "Tight.", DoctorSeverity.Warning),
            new DoctorFinding("Disk space", "No room.", DoctorSeverity.Blocker)));

        var marks = panel.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => t is "✕" or "!" or "✓")
            .ToList();

        Assert.Equal(["✕", "!", "✓"], marks);
    }

    [AvaloniaFact]
    public void Passes_are_shown_and_not_only_problems()
    {
        // "Everything is fine" is the most common useful answer, and a dialog
        // that appears only on trouble cannot give it.
        var panel = MainWindow.BuildFindings(Report(
            new DoctorFinding("Model", "Present.", DoctorSeverity.Ok)));

        Assert.Contains(panel.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.Text == "Present.");
    }

    [AvaloniaFact]
    public void The_detail_of_a_finding_can_be_selected()
    {
        // A path or a size is worth copying out of, and §10 asks that messages
        // be actionable rather than merely readable.
        var panel = MainWindow.BuildFindings(Report(
            new DoctorFinding("Disk space", @"Free up 4 GB on C:\.", DoctorSeverity.Blocker)));

        Assert.Contains(panel.GetLogicalDescendants().OfType<SelectableTextBlock>(),
            t => t.Text!.Contains("Free up"));
    }

    [AvaloniaFact]
    public async Task Clicking_Doctor_with_none_wired_does_nothing_rather_than_throwing()
    {
        // The headless and design-time cases both have no Doctor.
        var (_, model) = Open(doctor: null);

        Assert.Null(await model.RunDoctorAsync());
    }
}
