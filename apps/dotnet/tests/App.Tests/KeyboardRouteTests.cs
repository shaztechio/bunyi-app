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

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Spec §12: every window has a keyboard route.
/// </summary>
/// <remarks>
/// <para>
/// "Settings, Logs, Help and Doctor are each reachable without clicking a
/// toolbar." The app declared no key binding at all before #159, so a person
/// without a pointer could reach none of the four. macOS has ⌘, ⌘L and ⌘?;
/// the chords here are the platform's, and the spec pins the requirement
/// rather than the keys.
/// </para>
/// <para>
/// Each chord runs the same method its header button does, so the tests that
/// matter are the ones about where and when the chord works: with focus in the
/// script box, which is where focus usually is, and while a generation is
/// running, which is when Doctor and Logs are wanted most.
/// </para>
/// </remarks>
public class KeyboardRouteTests : HeadlessWindows
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public KeyboardRouteTests() => Directory.CreateDirectory(_folder);

    protected override void DisposeCore()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private (MainWindow Window, MainViewModel Model) Show(
        Func<TtsMode, bool, CancellationToken, Task<DoctorReport>>? doctor = null)
    {
        var log = new RecordingLog();
        var store = new SettingsStore(log, Path.Combine(_folder, "settings.json"));
        var configs = new ModelConfigLibrary(log, Path.Combine(_folder, "configs.json"));

        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), log)
        {
            Script = "Hello there.",
            Doctor = doctor,
            Logs = new LogsViewModel(new LogStore(), post: a => a()),
            Settings = new SettingsViewModel(store, configs, log, _ => { }, _ => "org/repo"),
        };

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();
        return (window, model);
    }

    private static void Press(MainWindow window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) =>
        window.KeyPressQwerty(key, modifiers);

    private static IEnumerable<T> Opened<T>(Window window) where T : Window =>
        window.OwnedWindows.OfType<T>();

    [AvaloniaFact]
    public void Ctrl_L_opens_Logs()
    {
        var (window, _) = Show();

        Press(window, PhysicalKey.L, RawInputModifiers.Control);

        Assert.Single(Opened<LogsWindow>(window));
    }

    [AvaloniaFact]
    public void F1_opens_Help()
    {
        var (window, _) = Show();

        Press(window, PhysicalKey.F1);

        Assert.Single(Opened<HelpWindow>(window));
    }

    [AvaloniaFact]
    public void Ctrl_comma_opens_Settings()
    {
        var (window, _) = Show();

        Press(window, PhysicalKey.Comma, RawInputModifiers.Control);

        Assert.Single(Opened<SettingsWindow>(window));
    }

    [AvaloniaFact]
    public void Ctrl_D_runs_Doctor()
    {
        var asked = false;
        var (window, _) = Show((_, _, _) =>
        {
            asked = true;
            return Task.FromResult(new DoctorReport(TtsMode.PresetVoice, []));
        });

        Press(window, PhysicalKey.D, RawInputModifiers.Control);

        Assert.True(asked, "Ctrl+D did not run Doctor");
    }

    [AvaloniaFact]
    public void The_chord_works_with_focus_in_the_script_box()
    {
        // Where focus usually is. A TextBox handles a good many control chords
        // of its own, and a route that only works once focus has been tabbed
        // elsewhere is not much of a route.
        var (window, _) = Show();
        var script = window.GetLogicalDescendants().OfType<TextBox>().First(t => t.Name == "ScriptBox");
        script.Focus();
        Assert.True(script.IsFocused);

        Press(window, PhysicalKey.L, RawInputModifiers.Control);

        Assert.Single(Opened<LogsWindow>(window));
        // And the chord was not also typed into the script.
        Assert.Equal("Hello there.", script.Text);
    }

    [AvaloniaFact]
    public void The_chord_works_while_a_generation_runs()
    {
        // The header buttons stay live during work by construction (they sit
        // outside the disabled scope); the chords have to as well, because a
        // run behaving strangely is exactly when Logs are wanted.
        var (window, model) = Show();
        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        Press(window, PhysicalKey.L, RawInputModifiers.Control);

        Assert.Single(Opened<LogsWindow>(window));
    }

    [AvaloniaFact]
    public void Pressing_the_chord_again_brings_the_open_window_forward_rather_than_opening_two()
    {
        var (window, _) = Show();

        Press(window, PhysicalKey.L, RawInputModifiers.Control);
        Press(window, PhysicalKey.L, RawInputModifiers.Control);

        Assert.Single(Opened<LogsWindow>(window));
    }

    [AvaloniaFact]
    public void The_four_routes_are_declared_on_the_window_and_nowhere_narrower()
    {
        // On the window, so they hold whatever has focus. A binding on the
        // header would work only once the header had focus, which it never
        // does.
        var (window, _) = Show();

        var gestures = window.KeyBindings.Select(b => b.Gesture.ToString()).ToList();

        Assert.Contains("Ctrl+OemComma", gestures);
        Assert.Contains("Ctrl+D", gestures);
        Assert.Contains("Ctrl+L", gestures);
        Assert.Contains("F1", gestures);
    }

    [AvaloniaTheory]
    [InlineData("SettingsButton", "Ctrl+,")]
    [InlineData("DoctorButton", "Ctrl+D")]
    [InlineData("LogsButton", "Ctrl+L")]
    [InlineData("HelpButton", "F1")]
    public void Each_header_button_tells_assistive_technology_its_key(string name, string chord)
    {
        // The chord goes in the accelerator property, not in the name: a name
        // is read aloud as a name, and "open bracket control L close bracket"
        // is not one. The tooltip carries it for sighted users.
        var (window, _) = Show();
        var button = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name);

        Assert.Equal(chord, AutomationProperties.GetAcceleratorKey(button));
        Assert.Contains(chord, Assert.IsType<string>(ToolTip.GetTip(button)), StringComparison.Ordinal);
        Assert.DoesNotContain(chord, AutomationProperties.GetName(button), StringComparison.Ordinal);
    }
}
