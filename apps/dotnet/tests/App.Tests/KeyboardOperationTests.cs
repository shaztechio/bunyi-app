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
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Bunyi.App.Infrastructure;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Everything a pointer can do, the keyboard can do (spec §12).
/// </summary>
/// <remarks>
/// <para>
/// #159 listed the questions; these are the answers, measured on the real
/// windows rather than read off the markup. Each test names what it found so
/// a regression reads as "the Tab order changed", not as an assertion failure.
/// </para>
/// <para>
/// Focus order is asserted as a <b>relative</b> order — this before that —
/// rather than an exact list, so adding a control does not fail the test
/// unless it lands somewhere a reader would not expect it.
/// </para>
/// <para>
/// <b>What layer this file pins, and in which screen-reader mode.</b> These
/// drive Avalonia's own input pipeline, so unlike the peer tests there is no
/// bridge in the way — a key that works here works in the app. What they cannot
/// speak for is a screen reader that takes the keys first: Narrator's scan mode
/// (Caps Lock + Space) captures the arrow keys, Home and End for its own
/// cursor, so these hold with scan mode <i>off</i>. That is not a defect in the
/// app, but a keyboard claim that does not say which mode it holds in is not a
/// claim (#192).
/// </para>
/// </remarks>
public sealed class KeyboardOperationTests : HeadlessWindows
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public KeyboardOperationTests() => Directory.CreateDirectory(_folder);

    protected override void DisposeCore()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // ---- Helpers ----

    /// <summary>A full press: down and up.</summary>
    /// <remarks>
    /// Both halves, because a Button clicks on Space at key-up. A helper that
    /// only sent key-down left the button pressed and the dialog open, and the
    /// test awaiting the dialog's answer hung until the run was aborted.
    /// </remarks>
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);

        // Escape and Return close a dialog on key-down; the window is gone by
        // the time the key comes up, and a closed window refuses input.
        try
        {
            window.KeyReleaseQwerty(key, modifiers);
            window.UpdateLayout();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static Control? Focused(Window window) =>
        TopLevel.GetTopLevel(window)!.FocusManager!.GetFocusedElement() as Control;

    /// <summary>Tabs through the window and records what gets focus, in order.</summary>
    private static List<Control> TabOrder(Window window, int presses)
    {
        var seen = new List<Control>();
        for (var i = 0; i < presses; i++)
        {
            Press(window, PhysicalKey.Tab);
            if (Focused(window) is { } c) seen.Add(c);
        }

        return seen;
    }

    /// <summary>The nearest ancestor-or-self carrying a name, for readable failures.</summary>
    private static string Describe(Control c)
    {
        foreach (var v in c.GetSelfAndVisualAncestors().OfType<Control>())
        {
            if (!string.IsNullOrEmpty(v.Name)) return $"{c.GetType().Name} in {v.Name}";
        }

        return c is ContentControl { Content: string s } ? $"{c.GetType().Name}(\"{s}\")" : c.GetType().Name;
    }

    private static int IndexOf(List<Control> order, Func<Control, bool> match)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (match(order[i]) || order[i].GetVisualAncestors().OfType<Control>().Any(match)) return i;
        }

        return -1;
    }

    private static void AssertBefore(List<Control> order, string firstName, Func<Control, bool> first,
        string secondName, Func<Control, bool> second)
    {
        var a = IndexOf(order, first);
        var b = IndexOf(order, second);
        var trail = string.Join(" → ", order.Select(Describe));

        Assert.True(a >= 0, $"{firstName} never received focus. Order seen: {trail}");
        Assert.True(b >= 0, $"{secondName} never received focus. Order seen: {trail}");
        Assert.True(a < b, $"{firstName} (#{a}) should come before {secondName} (#{b}). Order seen: {trail}");
    }

    private static Func<Control, bool> Named(string name) => c => c.Name == name;

    private static int Segment(MainViewModel model) => model.AllSegments.ToList().IndexOf(model.SelectedSegment);

    // ---- Main window ----

    private (MainWindow Window, MainViewModel Model) OpenMain()
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog(), () => _folder);
        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();
        return (window, model);
    }

    [AvaloniaFact]
    public void Tab_walks_the_main_window_the_way_a_reader_does()
    {
        // §12: focus order follows the visual order. Top to bottom: the header
        // buttons, the mode picker, the script box, then the example prompts
        // that sit beneath it, the options, and Generate last.
        var (window, _) = OpenMain();

        var order = TabOrder(window, 30);

        AssertBefore(order, "Settings", Named("SettingsButton"), "Doctor", Named("DoctorButton"));
        AssertBefore(order, "Doctor", Named("DoctorButton"), "Logs", Named("LogsButton"));
        AssertBefore(order, "Logs", Named("LogsButton"), "Help", Named("HelpButton"));
        AssertBefore(order, "Help", Named("HelpButton"), "the mode picker", Named("ModePicker"));
        AssertBefore(order, "the mode picker", Named("ModePicker"), "the script box", Named("ScriptBox"));

        // The examples are drawn UNDER the script box, so they come after it.
        AssertBefore(order, "the script box", Named("ScriptBox"),
            "an example prompt", c => c is Button && c.GetVisualAncestors().OfType<Control>().Any(a => a.Name == "Examples"));

        AssertBefore(order, "an example prompt",
            c => c is Button && c.GetVisualAncestors().OfType<Control>().Any(a => a.Name == "Examples"),
            "Language", c => c is ComboBox && c.GetVisualAncestors().OfType<Control>().Any(a => a.Name == "LanguageRow"));
        AssertBefore(order, "Language",
            c => c is ComboBox && c.GetVisualAncestors().OfType<Control>().Any(a => a.Name == "LanguageRow"),
            "Speaker", c => c is ComboBox && c.GetVisualAncestors().OfType<Control>().Any(a => a.Name == "SpeakerRow"));
        AssertBefore(order, "Speaker",
            c => c is ComboBox && c.GetVisualAncestors().OfType<Control>().Any(a => a.Name == "SpeakerRow"),
            "Style", Named("InstructBox"));
        AssertBefore(order, "Style", Named("InstructBox"), "Generate", Named("GenerateButton"));
    }

    [AvaloniaFact]
    public void The_mode_changes_with_the_arrow_keys()
    {
        // macOS's segmented control moves with the arrows; §12 asks for the
        // same reach here. Tab to the picker, then Right and Left.
        var (window, model) = OpenMain();

        for (var i = 0; i < 30 && !(Focused(window)?.GetSelfAndVisualAncestors().OfType<Control>().Any(a => a.Name == "ModePicker") ?? false); i++)
        {
            Press(window, PhysicalKey.Tab);
        }

        Assert.True(Focused(window)?.GetSelfAndVisualAncestors().OfType<Control>().Any(a => a.Name == "ModePicker"),
            $"Tab never reached the mode picker; focus ended on {(Focused(window) is { } f ? Describe(f) : "nothing")}");

        var start = Segment(model);
        Assert.Equal(0, start);

        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, Segment(model));

        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(2, Segment(model));

        Press(window, PhysicalKey.ArrowLeft);
        Assert.Equal(1, Segment(model));
    }

    // ---- Settings ----

    private SettingsWindow OpenSettings()
    {
        var log = new RecordingLog();
        var store = new SettingsStore(log, Path.Combine(_folder, "settings.json"));
        var configs = new ModelConfigLibrary(log, Path.Combine(_folder, "configs.json"));
        var model = new SettingsViewModel(store, configs, log, _ => { }, mode => mode switch
        {
            TtsMode.PresetVoice => "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
            TtsMode.VoiceDesign => "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX",
            _ => "wavekat/Qwen3-TTS-0.6B-Base-ONNX",
        });

        var window = Open(new SettingsWindow { DataContext = model });
        window.UpdateLayout();
        return window;
    }

    [AvaloniaFact]
    public void The_settings_tabs_switch_from_the_keyboard()
    {
        var window = OpenSettings();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();

        // Tab until a tab header has focus, then walk the headers with Right.
        for (var i = 0; i < 20 && Focused(window) is not TabItem; i++) Press(window, PhysicalKey.Tab);

        Assert.IsType<TabItem>(Focused(window));
        Assert.Equal(0, tabs.SelectedIndex);

        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(1, tabs.SelectedIndex);

        Press(window, PhysicalKey.ArrowRight);
        Assert.Equal(2, tabs.SelectedIndex);

        Press(window, PhysicalKey.ArrowLeft);
        Assert.Equal(1, tabs.SelectedIndex);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_control_on_every_settings_tab_is_reachable_by_tab(int tab)
    {
        // §12: reachable and operable. For each tab, every enabled, visible
        // interactive control inside it gets focus from Tab within a bounded
        // number of presses.
        var window = OpenSettings();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = tab;
        window.UpdateLayout();

        // A tab's content is presented by the TabControl, not parented under
        // its TabItem, so look in the content host.
        var header = (TabItem)tabs.ContainerFromIndex(tab)!;
        var content = tabs.GetVisualDescendants().OfType<ContentPresenter>()
            .Single(p => p.Name == "PART_SelectedContentHost");
        var interactive = content.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c is Button or TextBox or ComboBox or CheckBox or SelectableTextBlock)
            // A scrollbar's line and page buttons are chrome, not controls a
            // person is offered; the wheel and the keys scroll for them.
            .Where(c => c is not RepeatButton && !c.GetVisualAncestors().OfType<ScrollBar>().Any())
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
            .ToList();

        Assert.NotEmpty(interactive);

        var reached = new HashSet<Control>(TabOrder(window, 60));
        var missed = interactive.Where(c => !reached.Contains(c)).Select(Describe).ToList();

        Assert.True(missed.Count == 0,
            $"tab {tab} ({header.Header}): {missed.Count} of {interactive.Count} controls never got focus: {string.Join(", ", missed)}");
    }

    // ---- Logs ----

    private (LogsWindow Window, LogStore Store) OpenLogs()
    {
        var store = new LogStore();
        store.Log("first line");
        store.Log("second line");

        var model = new LogsViewModel(store, post: a => a(), new ImmediateTimers());
        var window = Open(new LogsWindow { DataContext = model });
        window.UpdateLayout();
        return (window, store);
    }

    private sealed class ImmediateTimers : IBatchTimerFactory, IBatchTimer
    {
        private Action? _tick;
        public IBatchTimer Create(TimeSpan interval, Action tick) { _tick = tick; _tick(); return this; }
        public bool Running => true;
        public void Start() => _tick?.Invoke();
        public void Stop() { }
        public void Dispose() { }
    }

    [AvaloniaFact]
    public void Copy_and_Clear_are_reachable_and_the_log_text_can_be_selected()
    {
        var (window, _) = OpenLogs();
        var text = window.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.Name == "LogText");

        var order = TabOrder(window, 12);

        Assert.Contains(order, c => c.Name == "CopyButton");
        Assert.Contains(order, c => c.Name == "ClearButton");
        Assert.Contains(order, c => ReferenceEquals(c, text));

        // Select all from the keyboard, the way a person copies a run of lines.
        text.Focus();
        Press(window, PhysicalKey.A, RawInputModifiers.Control);

        Assert.True(text.SelectionEnd - text.SelectionStart > 0, "Ctrl+A selected nothing");
        Assert.Equal(text.Text?.Length ?? 0, Math.Abs(text.SelectionEnd - text.SelectionStart));
    }

    // ---- Dialogs (spec §9, §12) ----

    private static Window OwnedDialog(Window owner)
    {
        owner.UpdateLayout();
        var dialog = owner.OwnedWindows.LastOrDefault();
        Assert.NotNull(dialog);
        dialog!.UpdateLayout();
        return dialog;
    }

    [AvaloniaFact]
    public async Task Escape_on_the_busy_close_prompt_keeps_working()
    {
        // §12: Escape dismisses without acting. §9: Keep Working is the safe
        // default. Escape must therefore mean Keep Working — never Stop.
        var (window, _) = OpenMain();

        var pending = window.ConfirmAsync();
        var dialog = OwnedDialog(window);

        Press(dialog, PhysicalKey.Escape);

        Assert.True(await pending, "Escape stopped the job instead of keeping it");
    }

    [AvaloniaFact]
    public async Task Return_on_the_busy_close_prompt_keeps_working()
    {
        var (window, _) = OpenMain();

        var pending = window.ConfirmAsync();
        var dialog = OwnedDialog(window);

        Press(dialog, PhysicalKey.Enter);

        Assert.True(await pending, "Return took the destructive choice");
    }

    [AvaloniaFact]
    public async Task Escape_on_the_delete_model_prompt_keeps_the_model()
    {
        var window = OpenSettings();

        var pending = window.AskAsync("Delete this model?", "Several gigabytes.", confirm: "Delete", cancel: "Keep");
        var dialog = OwnedDialog(window);

        Press(dialog, PhysicalKey.Escape);

        Assert.False(await pending, "Escape deleted the model");
    }

    [AvaloniaFact]
    public async Task Return_on_the_delete_model_prompt_keeps_the_model()
    {
        var window = OpenSettings();

        var pending = window.AskAsync("Delete this model?", "Several gigabytes.", confirm: "Delete", cancel: "Keep");
        var dialog = OwnedDialog(window);

        Press(dialog, PhysicalKey.Enter);

        Assert.False(await pending, "Return deleted the model");
    }

    [AvaloniaFact]
    public async Task The_destructive_choice_still_works_when_chosen_on_purpose()
    {
        // The other half: Escape and Return both being safe must not make the
        // real choice unreachable. Tab to it and press Space.
        var window = OpenSettings();

        var pending = window.AskAsync("Delete this model?", "Several gigabytes.", confirm: "Delete", cancel: "Keep");
        var dialog = OwnedDialog(window);

        for (var i = 0; i < 6; i++)
        {
            Press(dialog, PhysicalKey.Tab);
            if (Focused(dialog) is Button { Content: "Delete" }) break;
        }

        Assert.True(Focused(dialog) is Button { Content: "Delete" }, "Tab never reached Delete");
        Press(dialog, PhysicalKey.Space);

        Assert.True(await pending);
    }

    [AvaloniaFact]
    public async Task Escape_closes_a_Doctor_report()
    {
        var (window, _) = OpenMain();
        var report = new DoctorReport(TtsMode.PresetVoice,
            [new DoctorFinding("Model present", "Yes.", DoctorSeverity.Ok)]);

        var pending = window.ShowReportAsync(report, "Doctor");
        var dialog = OwnedDialog(window);

        Press(dialog, PhysicalKey.Escape);
        await pending;

        Assert.DoesNotContain(dialog, window.OwnedWindows);
    }
}
