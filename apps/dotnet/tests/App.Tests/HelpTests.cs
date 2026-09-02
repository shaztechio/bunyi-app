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
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Help;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// In-app help (spec §10).
/// </summary>
public class HelpTests : HeadlessWindows
{
    private HelpWindow Open()
    {
        var window = Open(new HelpWindow());
        window.UpdateLayout();
        return window;
    }

    /// <summary>Every word a control draws, including its inline runs.</summary>
    /// <remarks>
    /// A <see cref="TextBlock" /> built from Inlines reports a null
    /// <c>Text</c> — the words live in the runs. Reading only <c>Text</c> sees
    /// the bullet markers and none of the help.
    /// </remarks>
    private static string TextOf(Control control) =>
        string.Concat(control.GetSelfAndLogicalDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Concat(
                (t.Inlines ?? []).OfType<Run>().Select(r => r.Text))));

    [AvaloniaFact]
    public void The_help_text_ships_inside_the_app()
    {
        // A portable build is a folder someone copies about. Help beside the
        // executable is help that goes missing, and the failure — an empty
        // window — looks like a broken app rather than a broken copy.
        var text = HelpWindow.LoadText();

        Assert.Contains("# Bunyi Help", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shipped without its help text", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void The_shipped_help_parses_into_something_worth_reading()
    {
        // Guards the real document rather than a sample: an edit to HELP.md
        // that trips the parser fails here rather than in front of a user.
        var blocks = HelpDocument.Parse(HelpWindow.LoadText());

        Assert.True(blocks.Count > 40, $"only {blocks.Count} blocks parsed");
        Assert.Contains(blocks, b => b is { Kind: HelpBlockKind.Heading, Level: 1 });
        Assert.True(blocks.Count(b => b.Kind == HelpBlockKind.Heading) > 8);
    }

    [AvaloniaFact]
    public void No_block_of_the_shipped_help_renders_empty()
    {
        // An empty block is text the parser ate. It is invisible in the window,
        // which is exactly why it needs a test.
        var blocks = HelpDocument.Parse(HelpWindow.LoadText());

        Assert.All(blocks, b => Assert.False(
            string.IsNullOrWhiteSpace(b.PlainText),
            $"a {b.Kind} rendered as nothing"));
    }

    [AvaloniaFact]
    public void The_help_covers_every_mode_the_app_has()
    {
        // This assertion used to be its own opposite: it required the help to
        // say two of the three modes were "not here yet", which was true when
        // it was written and stopped being true three milestones later. The
        // help went on claiming voice clone, saved voices and backup were
        // missing while the app shipped all of them, and the test held it
        // there. Pinned to the mode list instead, it fails when a mode is added
        // without being written up rather than when one stops being missing.
        var text = HelpWindow.LoadText();

        foreach (var mode in Enum.GetValues<TtsMode>())
        {
            Assert.Contains(mode.DisplayName(), text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [AvaloniaFact]
    public void The_help_no_longer_says_the_style_instruction_does_nothing_in_preset_voice()
    {
        // The inverse of the test that used to sit here. For a while this was
        // the one thing the Windows and Linux app genuinely could not do that
        // the Mac app could, and the help said so; #178 closed the gap by
        // driving the preset export through our own pipeline, where the
        // instruction is text conditioning like any other. The old sentence
        // was accurate then and would be a false warning now, so its absence
        // is what is pinned — together with the claim that replaced it.
        var text = HelpWindow.LoadText();

        Assert.DoesNotContain("has no effect", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reaches the model", text, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void The_help_does_not_tell_this_user_to_do_macOS_things()
    {
        // Adapted rather than copied: a Windows user told to press Command-L,
        // or to look in the Finder, has been given help for a different app.
        // Naming macOS is fine and this deliberately allows it — saying which
        // features the Mac version has is the honest way to explain what is
        // missing here.
        var text = HelpWindow.LoadText();

        foreach (var mac in new[] { "Finder", "⌘", "your Mac", "this Mac", "a Mac" })
        {
            Assert.DoesNotContain(mac, text, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void The_window_shows_the_document()
    {
        var window = Open();

        var body = window.GetLogicalDescendants().OfType<StackPanel>()
            .First(p => p.Name == "Body");

        Assert.NotEmpty(body.Children);
        Assert.Contains("Bunyi Help", TextOf(body), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Everything_in_the_window_can_be_selected_and_copied()
    {
        // Help is where the folder paths and the download command live, and
        // both exist to be copied out.
        var window = Open();
        var body = window.GetLogicalDescendants().OfType<StackPanel>()
            .First(p => p.Name == "Body");

        // The list markers are the exception: a bullet is decoration, and
        // dragging through it to reach the words would be worse.
        var texts = body.GetSelfAndLogicalDescendants().OfType<TextBlock>()
            .Where(t => t.Inlines?.Count > 0 || !string.IsNullOrWhiteSpace(t.Text))
            .Where(t => t.Text is null || !t.Text.EndsWith('.') && t.Text != "•")
            .ToList();

        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.IsAssignableFrom<SelectableTextBlock>(t));
    }

    [AvaloniaFact]
    public void A_heading_is_bigger_than_the_prose_under_it()
    {
        var heading = HelpWindow.Build(
            new HelpBlock(HelpBlockKind.Heading, [new HelpRun("Logs")], Level: 2));
        var paragraph = HelpWindow.Build(
            new HelpBlock(HelpBlockKind.Paragraph, [new HelpRun("Some prose.")]));

        Assert.True(
            ((TextBlock)heading).FontSize > ((TextBlock)paragraph).FontSize,
            "a heading that matches its body text is not a heading");
    }

    [AvaloniaFact]
    public void Headings_get_smaller_as_they_get_deeper()
    {
        double Size(int level) => ((TextBlock)HelpWindow.Build(
            new HelpBlock(HelpBlockKind.Heading, [new HelpRun("x")], Level: level))).FontSize;

        Assert.True(Size(1) > Size(2));
        Assert.True(Size(2) > Size(3));
    }

    [AvaloniaFact]
    public void A_bullet_shows_a_bullet_and_a_numbered_item_shows_its_number()
    {
        Assert.Contains("•",
            TextOf(HelpWindow.Build(new HelpBlock(HelpBlockKind.Bullet, [new HelpRun("Play")]))),
            StringComparison.Ordinal);

        Assert.Contains("2.",
            TextOf(HelpWindow.Build(
                new HelpBlock(HelpBlockKind.Numbered, [new HelpRun("Click Generate.")], Number: 2))),
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Bold_text_is_drawn_bold()
    {
        var inlines = HelpWindow.Inlines(new HelpBlock(HelpBlockKind.Paragraph, [
            new HelpRun("Press "),
            new HelpRun("Generate", HelpStyle.Bold),
        ]));

        var runs = inlines.OfType<Run>().ToList();
        Assert.Equal(FontWeight.Normal, runs[0].FontWeight);
        Assert.Equal(FontWeight.Bold, runs[1].FontWeight);
    }

    [AvaloniaFact]
    public void Inline_code_is_drawn_monospaced()
    {
        var inlines = HelpWindow.Inlines(new HelpBlock(HelpBlockKind.Paragraph, [
            new HelpRun(@"%LOCALAPPDATA%\Bunyi", HelpStyle.Code),
        ]));

        var run = Assert.Single(inlines.OfType<Run>());
        Assert.NotEqual(FontFamily.Default.Name, run.FontFamily!.Name);
    }

    [AvaloniaFact]
    public void A_command_is_not_re_wrapped_into_one_that_no_longer_runs()
    {
        // The download command in the Storage section is there to be pasted.
        var block = new HelpBlock(HelpBlockKind.Code, [
            new HelpRun("huggingface-cli download org/repo --local-dir models", HelpStyle.Code),
        ]);

        var text = Assert.IsType<SelectableTextBlock>(
            Assert.IsType<Border>(HelpWindow.Build(block)).Child);

        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
    }

    [AvaloniaFact]
    public void Prose_wraps_rather_than_running_off_the_edge()
    {
        var paragraph = (TextBlock)HelpWindow.Build(new HelpBlock(
            HelpBlockKind.Paragraph, [new HelpRun(new string('x', 400))]));

        Assert.Equal(TextWrapping.Wrap, paragraph.TextWrapping);
    }

    [AvaloniaFact]
    public void A_build_with_no_help_text_says_so_instead_of_showing_a_blank_page()
    {
        var blocks = HelpDocument.Parse(
            "# Help unavailable\n\nThis build shipped without its help text.");

        var window = Open();
        window.Render(blocks);

        var body = window.GetLogicalDescendants().OfType<StackPanel>()
            .First(p => p.Name == "Body");

        Assert.Contains("Help unavailable", TextOf(body), StringComparison.Ordinal);
    }
}
