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
using Avalonia.Media;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// The window itself, rendered without a display.
/// </summary>
/// <remarks>
/// These assert the guarantees §2 makes about the window rather than about the
/// engine — that Generate is replaced rather than disabled, that the inputs go
/// dead while work runs, and above all that Help and the log do not. Those were
/// previously claimed to hold "by construction", which is the kind of claim that
/// stays true only until someone moves a panel.
/// </remarks>
public class MainWindowTests
{
    private static (MainWindow Window, MainViewModel Model, FakeEngine Engine, FakePlayer Player)
        Open(Action<MainViewModel>? arrange = null)
    {
        var engine = new FakeEngine();
        var player = new FakePlayer();
        var model = new MainViewModel(engine, player, new RecordingLog());
        arrange?.Invoke(model);

        var window = new MainWindow { DataContext = model };
        window.Show();
        return (window, model, engine, player);
    }

    private static T Find<T>(Window window, Func<T, bool> predicate) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().First(predicate);

    /// <summary>
    /// Finds a button by name rather than by the words on it — several are
    /// icons now, and a test that keys off user-visible copy breaks every time
    /// the copy changes.
    /// </summary>
    private static Button ButtonWith(Window window, string name) =>
        Find<Button>(window, b => b.Name == name);

    /// <summary>
    /// Finds a button by its text. Only for the example chips, whose label is
    /// the content — they are generated from the examples themselves, so there
    /// is no name to give them.
    /// </summary>
    private static Button ChipLabelled(Window window, string text) =>
        Find<Button>(window, b => b.Content as string == text);

    [AvaloniaFact]
    public void The_window_opens_with_the_app_name()
    {
        var (window, _, _, _) = Open();

        Assert.Equal("Bunyi", window.Title);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void The_brand_accent_resolves_rather_than_falling_back_to_the_system_one()
    {
        // The macOS app shipped for a while rendering in whatever accent the
        // user's System Settings happened to carry, because the colour was
        // never wired up. A resource that fails to resolve looks fine until
        // someone compares two machines.
        var (window, _, _, _) = Open();

        Assert.True(window.TryFindResource("BunyiAccent", out var accent));
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(accent);
        Assert.Equal(Color.Parse("#5C54F5"), brush.Color);
    }

    [AvaloniaFact]
    public void Generate_is_present_and_unavailable_on_an_unused_window()
    {
        var (window, model, _, _) = Open();

        var generate = ButtonWith(window, "GenerateButton");
        Assert.True(generate.IsVisible);
        Assert.False(generate.IsEffectivelyEnabled);

        // §1: it says why on hover.
        Assert.NotNull(model.BlockedReason);
    }

    [AvaloniaFact]
    public void Typing_a_script_makes_Generate_available()
    {
        var (window, model, _, _) = Open();

        model.Script = "Hello there.";

        Assert.True(ButtonWith(window, "GenerateButton").IsEffectivelyEnabled);
        Assert.Null(model.BlockedReason);
    }

    [AvaloniaFact]
    public void The_examples_are_offered_on_an_unused_window_and_fill_the_script()
    {
        // §1: "An unused window suggests something to click." The first frame is
        // otherwise an empty box and a button that does not work.
        var (window, model, _, _) = Open();

        var example = ExamplePrompts.For(TtsMode.PresetVoice)[0];
        var chip = ChipLabelled(window, example);
        Assert.True(chip.IsVisible);

        chip.Command!.Execute(chip.CommandParameter);

        Assert.Equal(example, model.Script);
    }

    [AvaloniaFact]
    public void The_examples_disappear_once_the_script_has_anything_in_it()
    {
        var (window, model, _, _) = Open();
        var example = ExamplePrompts.For(TtsMode.PresetVoice)[0];

        model.Script = "x";

        Assert.False(ChipLabelled(window, example).IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Generate_is_replaced_by_Stop_while_work_is_in_progress()
    {
        // §2 is explicit that it is REPLACED, not merely disabled: a model
        // download runs for minutes, and without Stop the only way out is
        // closing the window.
        var (window, model, engine, _) = Open(m => m.Script = "Hello there.");

        engine.Publish(new EngineStatus(EngineState.Downloading));

        Assert.False(ButtonWith(window, "GenerateButton").IsEffectivelyVisible);
        var stop = ButtonWith(window, "StopButton");
        Assert.True(stop.IsEffectivelyVisible);
        Assert.True(stop.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Stop_reaches_the_engine()
    {
        var (window, _, engine, _) = Open(m => m.Script = "Hello there.");
        engine.Publish(new EngineStatus(EngineState.Generating));

        var stop = ButtonWith(window, "StopButton");
        stop.Command!.Execute(null);

        Assert.Equal(1, engine.StopRequests);
    }

    [AvaloniaFact]
    public void The_inputs_go_dead_while_work_is_in_progress()
    {
        // §2: their values were handed to the engine when the run started, so
        // leaving them editable invites changes that silently do not apply to
        // the audio being produced.
        var (window, _, engine, _) = Open(m => m.Script = "Hello there.");
        var script = Find<TextBox>(window, t => t.AcceptsReturn);
        Assert.True(script.IsEffectivelyEnabled);

        engine.Publish(new EngineStatus(EngineState.Generating));

        Assert.False(script.IsEffectivelyEnabled);
        Assert.False(Find<ComboBox>(window, _ => true).IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Help_and_the_log_stay_reachable_while_work_is_in_progress()
    {
        // The guarantee this whole file exists for. §2: "a long download is
        // exactly when someone wants to read the help or watch the log, and
        // neither touches the running job." macOS gets it from a window
        // toolbar; here it depends on those buttons sitting outside the
        // disabled panel, which is a layout decision one careless edit undoes.
        var (window, _, engine, _) = Open(m => m.Script = "Hello there.");

        engine.Publish(new EngineStatus(EngineState.Downloading, 0.4, "42% — about 3.1 MB/s"));

        Assert.True(ButtonWith(window, "HelpButton").IsEffectivelyEnabled);
        Assert.True(ButtonWith(window, "LogsButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void The_mode_picker_offers_the_three_generation_modes()
    {
        // §1 opens "A segmented picker selects one of three modes".
        var (window, model, _, _) = Open();

        var picker = Find<ListBox>(window, _ => true);
        Assert.Equal(3, picker.ItemCount);
        Assert.Equal(TtsMode.PresetVoice, model.Mode);
    }

    [AvaloniaFact]
    public void Voice_clone_offers_no_style_field()
    {
        // §1: the 12 Hz Base model ignores an instruction, so the field must not
        // be offered. Emotion for a cloned voice comes from the reference clip.
        var (window, model, _, _) = Open();
        Assert.True(model.ShowInstruct);

        model.Mode = TtsMode.VoiceClone;

        Assert.False(model.ShowInstruct);
    }

    [AvaloniaFact]
    public void Voice_design_labels_the_field_as_the_voice_rather_than_a_style()
    {
        // The two are different things and the spec insists they stay
        // distinguishable — the description is who is speaking, the style is how.
        var (_, model, _, _) = Open();

        model.Mode = TtsMode.VoiceDesign;

        Assert.Equal("Voice", model.InstructLabel);
    }

    [AvaloniaFact]
    public void Progress_is_shown_only_while_something_is_running()
    {
        var (window, _, engine, _) = Open(m => m.Script = "Hello there.");
        var bar = Find<ProgressBar>(window, _ => true);
        Assert.False(bar.IsEffectivelyVisible);

        engine.Publish(new EngineStatus(EngineState.Downloading, 0.42, "42% — about 3.1 MB/s"));

        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(0.42, bar.Value, 3);
    }

    [AvaloniaFact]
    public void The_status_line_carries_the_downloads_human_wording()
    {
        var (window, _, engine, _) = Open(m => m.Script = "Hello there.");

        engine.Publish(new EngineStatus(
            EngineState.Downloading, 0.42, "42% — about 3.1 MB/s, ~6 min left"));

        var status = Find<TextBlock>(window, t => t.Text?.Contains("MB/s") == true);
        Assert.Contains("~6 min left", status.Text);
    }

    [AvaloniaFact]
    public void Playback_controls_appear_only_once_there_is_a_result()
    {
        // §2: only this run's result, and nothing offers to play old audio while
        // new audio is being made.
        var (window, model, engine, _) = Open(m => m.Script = "Hello there.");
        Assert.False(ButtonWith(window, "PlayButton").IsEffectivelyVisible);

        engine.Publish(new EngineStatus(EngineState.Generating));
        Assert.False(ButtonWith(window, "PlayButton").IsEffectivelyVisible);

        model.LastOutputPath = "/tmp/out.wav";
        engine.Publish(EngineStatus.Idle);

        Assert.True(ButtonWith(window, "PlayButton").IsEffectivelyVisible);
        Assert.True(ButtonWith(window, "RevealButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_mode_picker_shows_names_a_person_would_read()
    {
        // Without an item template the picker renders the enum — "PresetVoice",
        // "VoiceDesign" — which is what shipped. The names exist once already,
        // because they are also settings keys and part of every filename.
        var (window, _, _, _) = Open();

        var picker = Find<ListBox>(window, _ => true);
        var labels = picker.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList();

        Assert.Contains("Preset voice", labels);
        Assert.Contains("Voice design", labels);
        Assert.Contains("Voice clone", labels);
        Assert.DoesNotContain(labels, l => l!.Contains("PresetVoice"));
    }

    [AvaloniaFact]
    public void Progress_animates_when_there_is_no_fraction_to_show()
    {
        // Generating reports no fraction, and it is the long phase. A
        // determinate bar pinned at zero for half a minute reads as an app that
        // has hung.
        var (window, _, engine, _) = Open(m => m.Script = "Hello there.");
        var bar = Find<ProgressBar>(window, _ => true);

        engine.Publish(new EngineStatus(EngineState.Generating));
        Assert.True(bar.IsIndeterminate);

        // A download does know, so it measures rather than animating.
        engine.Publish(new EngineStatus(EngineState.Downloading, 0.42, "42%"));
        Assert.False(bar.IsIndeterminate);
        Assert.Equal(0.42, bar.Value, 3);
    }

    [AvaloniaFact]
    public void Progress_is_not_shown_at_all_when_nothing_is_running()
    {
        var (window, _, _, _) = Open();

        var bar = Find<ProgressBar>(window, _ => true);

        Assert.False(bar.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Every_icon_only_button_says_what_it_does_on_hover()
    {
        // The risk icons introduce. This audience is non-technical, and an
        // unlabelled glyph is only obvious to whoever chose it — so a button
        // with a picture and no words must carry a tooltip.
        var (window, model, _, _) = Open(m => m.Script = "Hello there.");
        model.LastOutputPath = "/tmp/out.wav";

        var iconButtons = window.GetLogicalDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("icon"))
            .ToList();

        Assert.NotEmpty(iconButtons);
        Assert.All(iconButtons, b =>
        {
            var tip = ToolTip.GetTip(b) as string;
            Assert.False(string.IsNullOrWhiteSpace(tip), $"{b.Name} has no tooltip");
        });
    }

    [AvaloniaFact]
    public void A_speaker_survives_the_model_reporting_its_own_list()
    {
        // Regression: the fallback list is capitalised and the model reports
        // lowercase, so an exact-match check reset the picker the moment a model
        // loaded — the "Preset voice forgot your speaker" defect the macOS app
        // already had once.
        var (_, model, engine, _) = Open();
        model.Speaker = "Ryan";

        engine.Speakers = ["serena", "vivian", "ryan", "aiden"];
        engine.Publish(EngineStatus.Idle);

        Assert.Equal("ryan", model.Speaker, ignoreCase: true);
    }
}
