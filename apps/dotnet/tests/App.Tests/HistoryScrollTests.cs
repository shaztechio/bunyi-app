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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core.Audio;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// History scrolls when there is more than fits (spec §2a).
/// </summary>
/// <remarks>
/// <para>
/// Reported from using the app: with enough clips the ones at the bottom could
/// not be reached at all. There was a ScrollViewer around the list the whole
/// time — the fault was above it. The content region was a StackPanel, which
/// measures its children with <b>infinite</b> height in the stacking direction,
/// so the ScrollViewer was never given a height to fit into. It grew to whatever
/// its rows needed and the window clipped the rest.
/// </para>
/// <para>
/// So the thing worth asserting is not "there is a ScrollViewer" — there always
/// was — but that its viewport is <b>bounded by the window</b>. That is the
/// property a StackPanel silently takes away.
/// </para>
/// </remarks>
public sealed class HistoryScrollTests : HeadlessWindows
{
    private readonly string _outputs =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public HistoryScrollTests() => Directory.CreateDirectory(_outputs);

    protected override void DisposeCore()
    {
        if (Directory.Exists(_outputs)) Directory.Delete(_outputs, recursive: true);
    }

    /// <summary>Writes enough clips that no window could show them all.</summary>
    private void WriteClips(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(_outputs, $"Preset-voice-2026010{i / 10}T0000{i % 10:00}.wav");
            WavWriter.Write(path, new short[2400], 24_000);
        }
    }

    private (MainWindow Window, MainViewModel Model) ShowHistory()
    {
        var model = new MainViewModel(
            new FakeEngine(), new FakePlayer(), new RecordingLog(), () => _outputs)
        {
            ShowingHistory = true,
        };

        model.History.Refresh();

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();
        return (window, model);
    }

    private static ScrollViewer HistoryScroller(Window window) =>
        window.GetLogicalDescendants().OfType<HistoryView>().Single()
            .GetLogicalDescendants().OfType<ScrollViewer>().First();

    [AvaloniaFact]
    public void The_scrollbar_does_not_sit_on_top_of_the_row_buttons()
    {
        // Reported from using the app: with enough clips to scroll, the bar
        // covered the Trash button at the end of every row. The button was
        // still there and still clickable, which is the worst version of this
        // — it looked broken without being broken.
        WriteClips(40);
        var (window, model) = ShowHistory();
        model.History.Refresh();
        window.UpdateLayout();

        var scroller = HistoryScroller(window);

        Assert.False(scroller.AllowAutoHide,
            "the scrollbar floats over the content instead of taking its own space");

        var trash = window.GetLogicalDescendants().OfType<HistoryView>().Single()
            .GetLogicalDescendants().OfType<Button>()
            .Last(b => b.Classes.Contains("icon"));

        var right = trash.TranslatePoint(new Point(trash.Bounds.Width, 0), scroller);

        Assert.NotNull(right);
        Assert.True(right!.Value.X <= scroller.Viewport.Width + 0.5,
            $"a row button reaches {right.Value.X:0} in a viewport {scroller.Viewport.Width:0} wide");
    }

    [AvaloniaFact]
    public void The_list_reads_every_clip_on_disk()
    {
        // The rows exist; whether they can be reached is the next test.
        var (_, model) = ShowHistory();
        WriteClips(40);
        model.History.Refresh();

        Assert.Equal(40, model.History.Rows.Count);
    }

    [AvaloniaFact]
    public void The_viewport_is_bounded_by_the_window()
    {
        // The bug, stated as a property. Under a StackPanel the viewport grew to
        // the content's full height, so Extent == Viewport and there was nothing
        // to scroll — the overflow was simply outside the window.
        WriteClips(40);
        var (window, model) = ShowHistory();
        model.History.Refresh();
        window.UpdateLayout();

        var scroller = HistoryScroller(window);

        Assert.True(scroller.Viewport.Height > 0, "the list has no height at all");
        Assert.True(scroller.Viewport.Height < window.Height,
            $"the viewport is {scroller.Viewport.Height:F0} tall inside a {window.Height:F0} window, "
            + "so it is not being constrained");
    }

    [AvaloniaFact]
    public void There_is_something_to_scroll_when_there_is_more_than_fits()
    {
        WriteClips(40);
        var (window, model) = ShowHistory();
        model.History.Refresh();
        window.UpdateLayout();

        var scroller = HistoryScroller(window);

        Assert.True(scroller.Extent.Height > scroller.Viewport.Height,
            $"extent {scroller.Extent.Height:F0} does not exceed viewport "
            + $"{scroller.Viewport.Height:F0}, so nothing can be scrolled to");
    }

    [AvaloniaFact]
    public void The_last_clip_can_be_scrolled_to()
    {
        // The user's complaint in one assertion: the bottom of the list is
        // reachable.
        WriteClips(40);
        var (window, model) = ShowHistory();
        model.History.Refresh();
        window.UpdateLayout();

        var scroller = HistoryScroller(window);
        scroller.ScrollToEnd();
        window.UpdateLayout();

        var room = scroller.Extent.Height - scroller.Viewport.Height;

        Assert.True(scroller.Offset.Y > 0, "scrolling moved nothing");
        Assert.Equal(room, scroller.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void A_few_clips_need_no_scrolling()
    {
        // The other direction: a short list should not invent a scrollbar or
        // stretch itself to fill the card.
        WriteClips(2);
        var (window, model) = ShowHistory();
        model.History.Refresh();
        window.UpdateLayout();

        var scroller = HistoryScroller(window);

        Assert.True(scroller.Extent.Height <= scroller.Viewport.Height + 1,
            "a two-item list should fit without scrolling");
    }

    [AvaloniaFact]
    public void The_script_card_still_fills_the_same_space_in_a_mode()
    {
        // The fix moved both cards into one Grid row. The mode view must not
        // have lost its height to the change.
        var model = new MainViewModel(
            new FakeEngine(), new FakePlayer(), new RecordingLog(), () => _outputs);

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();

        var script = window.GetLogicalDescendants().OfType<TextBox>()
            .First(t => t.AcceptsReturn);

        Assert.True(script.Bounds.Height > 80,
            $"the script box is only {script.Bounds.Height:F0} tall");
    }
}
