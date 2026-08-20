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
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core.Diagnostics;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// The Logs window (spec §8).
/// </summary>
public class LogsTests : HeadlessWindows
{
    /// <summary>A timer that only ticks when a test says so.</summary>
    private sealed class ManualTimers : IBatchTimerFactory, IBatchTimer
    {
        private Action? _tick;

        public TimeSpan? Interval { get; private set; }
        public bool Stopped { get; private set; }

        public IBatchTimer Create(TimeSpan interval, Action tick)
        {
            Interval = interval;
            _tick = tick;
            return this;
        }

        public bool Running { get; private set; } = true;

        public void Tick() => _tick?.Invoke();
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Dispose() => Stopped = true;
    }

    private (LogsViewModel Model, LogStore Store, ManualTimers Timers) NewModel()
    {
        var store = new LogStore();
        var timers = new ManualTimers();
        return (new LogsViewModel(store, post: a => a(), timers), store, timers);
    }

    [Fact]
    public void The_window_opens_showing_what_already_happened()
    {
        // Everything notable is logged from launch onwards (§8), and most of it
        // has happened by the time anyone thinks to open the window.
        var store = new LogStore();
        store.Log("Bunyi started.");
        store.Log("Model ready.");

        var model = new LogsViewModel(store, post: a => a(), new ManualTimers());

        Assert.Equal(2, model.Lines.Count);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void An_empty_log_says_so_rather_than_looking_broken()
    {
        var (model, _, _) = NewModel();

        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void A_new_line_does_not_touch_the_list_until_the_batch_lands()
    {
        // The point of batching: a fast run logs constantly, and a dispatcher
        // post per line is what makes the list stutter while scrolling.
        var (model, store, timers) = NewModel();

        store.Log("one");
        store.Log("two");

        Assert.Empty(model.Lines);

        timers.Tick();

        Assert.Equal(2, model.Lines.Count);
    }

    [Fact]
    public void A_burst_arrives_in_the_order_it_was_logged()
    {
        var (model, store, timers) = NewModel();

        store.Log("first");
        store.Log("second");
        store.Log("third");
        timers.Tick();

        Assert.Equal(["first", "second", "third"], model.Lines.Select(l => l.Message));
    }

    [Fact]
    public void A_tick_with_nothing_waiting_does_nothing()
    {
        // Ten times a second forever, so the quiet case has to be free.
        var (model, _, timers) = NewModel();
        var appended = 0;
        model.LinesAppended += (_, _) => appended++;

        timers.Tick();
        timers.Tick();

        Assert.Equal(0, appended);
    }

    [Fact]
    public void The_batch_interval_is_short_enough_to_read_as_live()
    {
        Assert.InRange(LogsViewModel.BatchInterval.TotalMilliseconds, 1, 250);
    }

    [Fact]
    public void The_list_does_not_outgrow_the_store()
    {
        // The store caps itself; a window left open for a long download would
        // otherwise keep every line the store had already dropped.
        var (model, store, timers) = NewModel();

        for (var i = 0; i < LogStore.Capacity + 50; i++) store.Log($"line {i}");
        timers.Tick();

        Assert.Equal(LogStore.Capacity, model.Lines.Count);
        Assert.Equal(store.Snapshot()[^1].Message, model.Lines[^1].Message);
    }

    [Fact]
    public void Clear_empties_the_window_and_the_store_together()
    {
        var (model, store, timers) = NewModel();
        store.Log("something");
        timers.Tick();

        model.ClearCommand.Execute(null);

        Assert.Empty(model.Lines);
        Assert.True(model.IsEmpty);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Clearing_also_drops_what_had_not_arrived_yet()
    {
        // Otherwise the next tick refills a log the user just emptied.
        var (model, store, timers) = NewModel();
        store.Log("pending when cleared");

        model.ClearCommand.Execute(null);
        timers.Tick();

        Assert.Empty(model.Lines);
    }

    [Fact]
    public void Copy_takes_the_whole_log_rather_than_the_selection()
    {
        // §8: what a user is asked to paste into a bug report.
        var (model, store, timers) = NewModel();
        store.Log("first");
        store.Log("second");
        timers.Tick();

        var text = model.Text();

        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_the_window_stops_the_timer_and_lets_go_of_the_store()
    {
        // The store outlives the window — it is the app's singleton — so a
        // window that stays subscribed is a leak that grows a list nobody can
        // see.
        var (model, store, timers) = NewModel();

        model.Dispose();
        store.Log("after closing");
        timers.Tick();

        Assert.True(timers.Stopped);
        Assert.Empty(model.Lines);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var (model, _, _) = NewModel();

        model.Dispose();
        model.Dispose();
    }

    // ---- The window itself ----

    private (LogsWindow Window, LogsViewModel Model, LogStore Store, ManualTimers Timers)
        Open()
    {
        var (model, store, timers) = NewModel();
        var window = Open(new LogsWindow { DataContext = model });
        return (window, model, store, timers);
    }

    /// <summary>The realised text controls, after a layout pass.</summary>
    /// <remarks>
    /// A headless window lays out only when asked. There is one of these — the
    /// log is a single block so a selection can run across lines — and the
    /// tests below say so rather than assuming it.
    /// </remarks>
    private static List<SelectableTextBlock> Rows(LogsWindow window)
    {
        window.UpdateLayout();
        return [.. window.GetLogicalDescendants().OfType<SelectableTextBlock>()];
    }

    /// <summary>The one block the log is shown in.</summary>
    private static SelectableTextBlock Block(LogsWindow window) => Rows(window).Single();

    [AvaloniaFact]
    public void Every_line_is_selectable_and_monospaced()
    {
        // §8 asks for both by name: a log you cannot copy out of is a log you
        // cannot report with, and proportional digits make timestamps hard to
        // compare down a column.
        var (window, _, store, timers) = Open();
        store.Log("a line");
        timers.Tick();

        var line = Rows(window).First(t => t.Text?.Contains("a line") == true);

        Assert.NotEqual(Avalonia.Media.FontFamily.Default.Name, line.FontFamily.Name);
    }

    [AvaloniaFact]
    public void The_line_shown_carries_its_timestamp()
    {
        // §8: timestamped. Time only, not the date — the window shows one
        // session, and a date repeated down every line is column noise. macOS
        // omits it for the same reason. The rolling file keeps the date,
        // because that one does span days.
        var (window, _, store, timers) = Open();
        store.Log("generation finished");
        timers.Tick();

        var shown = Rows(window)
            .Select(t => t.Text ?? string.Empty)
            .First(t => t.Contains("generation finished", StringComparison.Ordinal));

        Assert.Matches(@"^\d{2}:\d{2}:\d{2}  ", shown);
    }

    [AvaloniaFact]
    public void A_log_with_nothing_in_it_shows_the_empty_message()
    {
        var (window, _, _, _) = Open();

        var message = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Text == "Nothing logged yet.");

        Assert.True(message.IsVisible);
    }

    [AvaloniaFact]
    public void A_fresh_window_follows_the_newest_line()
    {
        // Nothing has been scrolled, so there is nothing to interrupt.
        var (window, _, _, _) = Open();

        Assert.True(window.IsAtBottom());
    }

    [AvaloniaFact]
    public void Both_buttons_are_there()
    {
        var (window, _, _, _) = Open();

        var names = window.GetLogicalDescendants().OfType<Button>()
            .Select(b => b.Name).ToList();

        Assert.Contains("CopyButton", names);
        Assert.Contains("ClearButton", names);
    }

    [AvaloniaFact]
    public void A_long_line_wraps_rather_than_scrolling_sideways()
    {
        // A log is read by scanning down it. A horizontal scrollbar makes the
        // right-hand end of every long line — which is where the detail of an
        // error is — reachable only one line at a time, and puts the thing you
        // came to read off-screen by default. §8's Copy still takes the whole
        // untruncated line either way.
        var (window, _, store, timers) = Open();
        store.Log(new string('x', 400));
        timers.Tick();
        window.UpdateLayout();

        var scroller = window.GetLogicalDescendants().OfType<ScrollViewer>()
            .First(s => s.Name == "Scroller");

        // The content is no wider than what is on screen, so there is nothing
        // to scroll to and no bar to show.
        Assert.True(
            scroller.Extent.Width <= scroller.Viewport.Width,
            $"content is {scroller.Extent.Width} wide in a {scroller.Viewport.Width} viewport");
    }

    [AvaloniaFact]
    public void A_selection_can_run_across_lines()
    {
        // The log is one block, not one control per line. A selection cannot
        // cross from one control into the next, so a control per line meant
        // dragging down the log selected nothing past the line it started in -
        // and copying a run of lines is the ordinary reason to select any of it.
        var (window, _, store, timers) = Open();
        store.Log("first thing");
        store.Log("second thing");
        timers.Tick();

        var block = Block(window);
        block.SelectionStart = 0;
        block.SelectionEnd = block.Text!.Length;

        Assert.Contains("first thing", block.SelectedText, StringComparison.Ordinal);
        Assert.Contains("second thing", block.SelectedText, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void An_arriving_line_does_not_clear_a_selection_in_progress()
    {
        // Replacing the text drops the selection in it, and a generation
        // rewrites the log several times a second. Someone selecting a line to
        // paste into a bug report is doing it while the run is going, which is
        // exactly when the log is busiest.
        var (window, _, store, timers) = Open();
        store.Log("the line being copied");
        timers.Tick();

        var block = Block(window);
        block.SelectionStart = 0;
        block.SelectionEnd = 8;
        var selected = block.SelectedText;

        store.Log("an arrival mid-selection");
        timers.Tick();
        window.UpdateLayout();

        Assert.Equal(selected, block.SelectedText);
        Assert.DoesNotContain("an arrival mid-selection", block.Text!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void The_held_back_lines_appear_once_the_selection_is_released()
    {
        // Held back, not dropped. A log that stopped updating because of a
        // stray click would be worse than one that clears a selection.
        var (window, _, store, timers) = Open();
        store.Log("the line being copied");
        timers.Tick();

        var block = Block(window);
        block.SelectionStart = 0;
        block.SelectionEnd = 8;

        store.Log("an arrival mid-selection");
        timers.Tick();

        // Letting go: the caret collapses and the log catches up.
        block.SelectionEnd = block.SelectionStart;

        Assert.Contains("an arrival mid-selection", block.Text!, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Clearing_while_a_selection_is_held_still_empties_the_window()
    {
        // Clear is the one update that must not wait for a selection: §8 offers
        // it as the way to get rid of what is there, and text that outlived it
        // would be the log claiming lines the store no longer has.
        var (window, _, store, timers) = Open();
        store.Log("something to select");
        timers.Tick();

        var block = Block(window);
        block.SelectionStart = 0;
        block.SelectionEnd = 5;

        store.Clear();
        window.UpdateLayout();

        Assert.Equal(string.Empty, block.Text ?? string.Empty);
    }

    [AvaloniaFact]
    public void Clear_from_the_window_empties_the_store()
    {
        var (window, _, store, timers) = Open();
        store.Log("something");
        timers.Tick();

        window.GetLogicalDescendants().OfType<Button>()
            .First(b => b.Name == "ClearButton")
            .Command!.Execute(null);

        Assert.Equal(0, store.Count);
    }
}
