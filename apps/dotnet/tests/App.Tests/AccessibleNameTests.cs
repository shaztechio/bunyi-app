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
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// What a screen reader is told a button is called (#159, first question).
/// </summary>
/// <remarks>
/// <para>
/// <b>A tooltip is not an accessible name in Avalonia.</b> Measured here rather
/// than assumed: with nothing but <c>ToolTip.Tip</c> set, a Button's automation
/// peer reported the tooltip as <i>help text</i> and, for its <i>name</i>, the
/// type of its content — <c>Avalonia.Controls.Shapes.Path</c> for Settings,
/// Doctor, Logs and Help; <c>Avalonia.Controls.StackPanel</c> for Stop. Not
/// anonymous. Worse: a screen reader reading a namespace aloud.
/// </para>
/// <para>
/// The fix is one style in <c>Controls.axaml</c> binding every Button's
/// <c>AutomationProperties.Name</c> to its own tooltip, with a local name
/// winning where a control sets one. These tests pin the result on the real
/// window, so a button added with a tooltip is named on the day it is added.
/// </para>
/// <para>
/// <b>What layer this file pins.</b> Avalonia's automation peers, headless.
/// Narrator reads the Windows UI Automation tree and Orca reads AT-SPI, one
/// bridge further out — so a green run says the names exist where the bridge
/// will look for them, and says nothing about what either reader speaks. #192
/// is why that sentence is written here. <c>tools/UiaProbe</c> reads these same
/// names back off a running window on the far side of the Windows bridge.
/// </para>
/// </remarks>
public class AccessibleNameTests : HeadlessWindows
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

    private static string? NameOf(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control)?.GetName();

    [AvaloniaFact]
    public void Every_button_with_a_tooltip_is_named_by_it()
    {
        var (window, model) = Show();
        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        var tipped = window.GetLogicalDescendants().OfType<Button>()
            .Where(b => ToolTip.GetTip(b) is string { Length: > 0 })
            .ToList();

        Assert.NotEmpty(tipped);

        foreach (var button in tipped)
        {
            var tip = (string)ToolTip.GetTip(button)!;
            var name = NameOf(button);

            // Either the tooltip, or a name the control chose for itself.
            Assert.False(string.IsNullOrWhiteSpace(name), $"{button.Name ?? tip} has no accessible name");
            Assert.True(
                name == tip || name == AutomationProperties.GetName(button),
                $"{button.Name ?? tip} is named '{name}', not its tooltip");
        }
    }

    [AvaloniaFact]
    public void No_button_is_named_after_the_type_of_its_content()
    {
        // The failure this replaces, pinned so it cannot come back: "Avalonia
        // dot Controls dot Shapes dot Path, button" is not a name.
        var (window, model) = Show();
        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        foreach (var button in window.GetLogicalDescendants().OfType<Button>())
        {
            var name = NameOf(button) ?? string.Empty;
            Assert.DoesNotContain("Avalonia.", name, StringComparison.Ordinal);
        }
    }

    [AvaloniaTheory]
    [InlineData("SettingsButton", "Settings — models, storage and appearance")]
    [InlineData("DoctorButton", "Doctor — can this machine generate right now?")]
    [InlineData("LogsButton", "Logs — what Bunyi has been doing")]
    [InlineData("HelpButton", "Help — how to use Bunyi")]
    [InlineData("RevealButton", "Show the file on disk")]
    public void The_icon_buttons_announce_what_they_do(string buttonName, string expected)
    {
        var (window, model) = Show();
        // Reveal only exists once there is a result.
        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Idle));
        window.UpdateLayout();

        var button = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == buttonName);

        Assert.Equal(expected, NameOf(button));
    }

    [AvaloniaFact]
    public void Generate_and_Stop_keep_their_labels_as_their_names()
    {
        // Their tooltips are hints — "Stop the current operation", or the
        // reason Generate is blocked, or nothing at all when it is not — and
        // the word on the button is the better name. A local value outranks the
        // style, which is the mechanism this relies on.
        var (window, model) = Show();

        var generate = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "GenerateButton");
        Assert.Equal("Generate", NameOf(generate));

        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        var stop = window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == "StopButton");
        Assert.Equal("Stop", NameOf(stop));
    }
}
