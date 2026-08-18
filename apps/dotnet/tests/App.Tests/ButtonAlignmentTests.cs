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
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Text sits in the middle of the buttons it labels.
/// </summary>
/// <remarks>
/// Reported from using the app: "Stop" sat off to one side of its button. Both
/// it and Generate are 110 wide so the pair does not jump when they swap, but
/// "Generate" nearly fills that width while "Stop" uses a third of it — so the
/// same wrong alignment is invisible on one and obvious on the other.
/// </remarks>
public class ButtonAlignmentTests : HeadlessWindows
{
    private (MainWindow Window, MainViewModel Model) Show()
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            Script = "Hello there.",
        };

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();
        return (window, model);
    }

    private static Button ButtonNamed(Window window, string name) =>
        window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name);

    [AvaloniaTheory]
    [InlineData("GenerateButton")]
    [InlineData("StopButton")]
    public void The_label_is_centred_across_the_button(string name)
    {
        var (window, model) = Show();

        // Stop only exists while work is running.
        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        var button = ButtonNamed(window, name);

        Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
    }

    [AvaloniaTheory]
    [InlineData("GenerateButton")]
    [InlineData("StopButton")]
    public void The_rendered_words_sit_in_the_middle(string name)
    {
        // The property is one thing; where the glyphs land is another, and only
        // the second is what the eye judges.
        //
        // Measured on the TextBlock rather than the ContentPresenter, which was
        // the mistake the first version of this test made: the presenter
        // stretches to the full width whatever the alignment, so it looked
        // centred while the words inside it sat hard left.
        var (window, model) = Show();

        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        var button = ButtonNamed(window, name);
        var text = button.GetVisualDescendants().OfType<TextBlock>().First();

        var left = text.Bounds.X;
        var right = button.Bounds.Width - (text.Bounds.X + text.Bounds.Width);

        Assert.True(Math.Abs(left - right) < 2,
            $"'{text.Text}' sits {left:F1} from the left and {right:F1} from the right");
    }

    [AvaloniaFact]
    public void The_apps_own_control_styles_are_loaded()
    {
        // The gap that let this ship. The headless tests ran against bare
        // Fluent, so anything the app restyles was invisible to them — which is
        // how a short label against the left edge, and square corners on the
        // dialog buttons before it, both passed a green suite.
        var (window, _) = Show();

        var button = ButtonNamed(window, "GenerateButton");

        Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
        Assert.NotEqual(default, button.CornerRadius);
    }

    [AvaloniaFact]
    public void Stop_is_red_and_Generate_is_the_brand_colour()
    {
        // macOS fills Stop with the system red (Theme.swift, ActionButtonStyle
        // role .destructive) and Generate with the brand gradient. The colours
        // are what tell them apart at a glance, and they occupy the same place
        // in the row one after the other.
        var (window, model) = Show();
        window.UpdateLayout();

        var generate = ButtonNamed(window, "GenerateButton");
        Assert.Contains("primary", generate.Classes);

        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        var stop = ButtonNamed(window, "StopButton");
        Assert.Contains("danger", stop.Classes);
    }

    [AvaloniaTheory]
    [InlineData("BunyiDanger")]
    [InlineData("BunyiAccent")]
    public void The_action_colours_resolve(string key)
    {
        // A DynamicResource naming nothing resolves to nothing, silently, and a
        // Stop button drawn in the default grey is exactly what this change is
        // fixing.
        Assert.True(Avalonia.Application.Current!.TryFindResource(key, out var value), $"missing {key}");
        Assert.IsAssignableFrom<Avalonia.Media.IBrush>(value);
    }

    [AvaloniaFact]
    public void Stop_is_never_disabled()
    {
        // It only exists while there is something to stop, so it is always the
        // way out — macOS says the same in Theme.swift, and a greyed-out Stop
        // during a long download would strand the user.
        var (window, model) = Show();

        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        var stop = ButtonNamed(window, "StopButton");

        Assert.True(stop.IsVisible);
        Assert.True(stop.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Both_buttons_are_the_same_width_so_the_row_does_not_jump()
    {
        // Why they share a width: §2 replaces one with the other,
        // and a swap that resizes the row draws the eye to the wrong thing.
        var (window, model) = Show();
        window.UpdateLayout();

        var generate = ButtonNamed(window, "GenerateButton").Bounds.Width;

        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        var stop = ButtonNamed(window, "StopButton").Bounds.Width;

        Assert.Equal(generate, stop, 1);
    }
}
