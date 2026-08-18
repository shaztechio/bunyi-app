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
using Bunyi.Core.Diagnostics;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Playback progress on the main window (spec §2).
/// </summary>
/// <remarks>
/// The main window had a play button and nothing else: no progress, and a
/// glyph that stayed on Play while the clip ran. History had both from the
/// start, which is what made the gap easy to miss.
/// </remarks>
public class PlaybackBarTests : HeadlessWindows
{
    /// <summary>A timer a test drives by hand.</summary>
    private sealed class ManualTimers : IBatchTimerFactory, IBatchTimer
    {
        private Action? _tick;

        public bool Running { get; private set; } = true;

        public bool Created { get; private set; }

        public IBatchTimer Create(TimeSpan interval, Action tick)
        {
            Created = true;
            _tick = tick;
            return this;
        }

        public void Tick() => _tick?.Invoke();
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Dispose() { }
    }

    private (MainViewModel Model, FakePlayer Player, ManualTimers Timers) NewModel()
    {
        var player = new FakePlayer();
        var timers = new ManualTimers();
        var model = new MainViewModel(new FakeEngine(), player, new RecordingLog(), null, timers)
        {
            LastOutputPath = "clip.wav",
        };
        return (model, player, timers);
    }

    [Fact]
    public void A_view_model_can_be_built_off_the_UI_thread()
    {
        // The bug this exists for. A DispatcherTimer belongs to the UI thread
        // from the moment it is constructed, so building one in the view
        // model's constructor made a view model that could only be created
        // there. Plain unit tests build one on an ordinary thread and paid for
        // it with "the calling thread cannot access this object" during
        // cleanup — an intermittent CI failure that landed on whichever test
        // happened to run next, which is what made it hard to attribute.
        //
        // Run on a thread of this test's own, because xunit's own threads have
        // no dispatcher either and the failure was never reproducible locally.
        Exception? thrown = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var model = new MainViewModel(
                    new FakeEngine(), new FakePlayer(), new RecordingLog());

                Assert.False(model.IsPlaying);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the constructor hung");
        Assert.Null(thrown);
    }

    [Fact]
    public void The_ticker_is_not_made_until_something_plays()
    {
        // Which is what keeps the constructor free of the UI thread.
        var (model, player, timers) = NewModel();

        Assert.False(timers.Created);

        model.PlayCommand.Execute(null);

        Assert.True(timers.Created);
    }

    [Fact]
    public void Nothing_is_playing_to_begin_with()
    {
        var (model, _, timers) = NewModel();

        Assert.False(model.IsPlaying);
        Assert.Equal(0, model.PlayProgress);

        // Not "the ticker is stopped": there is no ticker yet, which is the
        // stronger statement and the one that keeps the constructor off the UI
        // thread.
        Assert.False(timers.Created);
    }

    [Fact]
    public void The_bar_follows_the_player_rather_than_the_clock()
    {
        // A clip that stalls must not leave the bar advancing over audio that
        // is not moving, so the fraction is read from the player each tick.
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.FromSeconds(10);

        model.PlayCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(2.5);
        timers.Tick();

        Assert.Equal(0.25, model.PlayProgress, 3);
    }

    [Fact]
    public void The_fraction_never_runs_past_the_end_of_the_track()
    {
        // Position can overshoot Duration on the last tick.
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.FromSeconds(5);

        model.PlayCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(5.02);
        timers.Tick();

        Assert.Equal(1, model.PlayProgress);
    }

    [Fact]
    public void A_clip_of_unknown_length_shows_no_progress_rather_than_a_wrong_one()
    {
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.Zero;

        model.PlayCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(1);
        timers.Tick();

        Assert.Equal(0, model.PlayProgress);
    }

    [Fact]
    public void The_times_read_as_minutes_and_seconds()
    {
        Assert.Equal("0:00", MainViewModel.Clock(TimeSpan.Zero));
        Assert.Equal("0:05", MainViewModel.Clock(TimeSpan.FromSeconds(5)));
        Assert.Equal("1:03", MainViewModel.Clock(TimeSpan.FromSeconds(63)));
        Assert.Equal("12:34", MainViewModel.Clock(TimeSpan.FromSeconds(754)));
    }

    [Fact]
    public void The_elapsed_time_never_reads_a_second_the_clip_has_not_reached()
    {
        // Rounded down, not to nearest: rounding up shows 0:01 before the first
        // second has passed, and briefly shows a total the clip never hits.
        Assert.Equal("0:00", MainViewModel.Clock(TimeSpan.FromSeconds(0.9)));
        Assert.Equal("0:01", MainViewModel.Clock(TimeSpan.FromSeconds(1.99)));
    }

    [Fact]
    public void A_negative_position_reads_as_zero()
    {
        Assert.Equal("0:00", MainViewModel.Clock(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void The_times_update_as_the_clip_runs()
    {
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.FromSeconds(65);

        model.PlayCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(30);
        timers.Tick();

        Assert.Equal("0:30", model.ElapsedText);
        Assert.Equal("1:05", model.DurationText);
    }

    [Fact]
    public void A_clip_that_reaches_its_end_puts_the_button_back_to_Play()
    {
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.FromSeconds(4);

        model.PlayCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(4);
        player.IsPlaying = false;   // the device has finished with it
        timers.Tick();

        Assert.False(model.IsPlaying);
        Assert.Equal(0, model.PlayProgress);
        Assert.False(timers.Running);
    }

    [Fact]
    public void Pressing_stop_clears_the_bar()
    {
        // Otherwise the bar is left frozen part way, which reads as a clip
        // still playing.
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.FromSeconds(10);

        model.PlayCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(5);
        timers.Tick();
        model.PlayCommand.Execute(null);

        Assert.False(model.IsPlaying);
        Assert.Equal(0, model.PlayProgress);
        Assert.Equal("0:00", model.ElapsedText);
    }

    [Fact]
    public void The_ticker_runs_only_while_something_is_playing()
    {
        // Ten times a second forever behind an idle window would be a cost with
        // nothing to show for it.
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.FromSeconds(3);

        model.PlayCommand.Execute(null);
        Assert.True(timers.Running);

        model.PlayCommand.Execute(null);
        Assert.False(timers.Running);
    }

    [AvaloniaFact]
    public void A_clip_finishing_on_its_own_resets_everything()
    {
        // The player raises Finished from its own thread; the window must end
        // in the same state as if Stop had been pressed.
        //
        // An AvaloniaFact rather than a Fact, because this is the one path that
        // goes through UiThread.Post — which needs a dispatcher to drain it.
        // Without one the test passes alone and fails in the suite, which is
        // the worst way for a test to be wrong.
        var (model, player, timers) = NewModel();
        player.Duration = TimeSpan.FromSeconds(3);

        model.PlayCommand.Execute(null);
        player.Position = TimeSpan.FromSeconds(1.5);
        timers.Tick();

        player.RaiseFinished();

        Assert.False(model.IsPlaying);
        Assert.Equal(0, model.PlayProgress);
    }

    // ---- The window ----

    private static Control Find(Window window, string name) =>
        window.GetLogicalDescendants().OfType<Control>().First(c => c.Name == name);

    private (MainWindow Window, MainViewModel Model, FakePlayer Player, ManualTimers Timers)
        Open()
    {
        var (model, player, timers) = NewModel();
        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();
        return (window, model, player, timers);
    }

    [AvaloniaFact]
    public void The_bar_is_hidden_until_something_plays()
    {
        // A bar sitting at zero beside a finished clip reads as a stalled one.
        var (window, _, _, _) = Open();

        Assert.False(Find(window, "PlaybackBar").IsVisible);
    }

    [AvaloniaFact]
    public void The_bar_appears_while_a_clip_runs()
    {
        var (window, model, player, timers) = Open();
        player.Duration = TimeSpan.FromSeconds(5);

        model.PlayCommand.Execute(null);
        window.UpdateLayout();

        Assert.True(Find(window, "PlaybackBar").IsVisible);
    }

    [AvaloniaFact]
    public void The_button_shows_Stop_while_a_clip_runs()
    {
        // The gap that started this: a picture that never changes gives no way
        // to tell a playing clip from a finished one.
        var (window, model, player, timers) = Open();
        player.Duration = TimeSpan.FromSeconds(5);

        var paths = Find(window, "PlayButton").GetSelfAndLogicalDescendants()
            .OfType<Avalonia.Controls.Shapes.Path>().ToList();
        Assert.Equal(2, paths.Count);

        Assert.True(paths[0].IsVisible);    // play
        Assert.False(paths[1].IsVisible);   // stop

        model.PlayCommand.Execute(null);
        window.UpdateLayout();

        Assert.False(paths[0].IsVisible);
        Assert.True(paths[1].IsVisible);
    }

    [AvaloniaFact]
    public void The_stop_square_is_centred_in_its_button()
    {
        Assert.True(Application.Current!.TryFindResource("IconStopRound32", out var value));
        var bounds = Assert.IsAssignableFrom<Avalonia.Media.Geometry>(value).Bounds;

        Assert.Equal(16, bounds.Center.X, 1);
        Assert.Equal(16, bounds.Center.Y, 1);
    }

    [Fact]
    public void The_filled_width_is_the_fraction_of_the_track()
    {
        var convert = (double fraction) =>
            (double)BarWidth.Instance.Convert(fraction, typeof(double), "130", null!);

        Assert.Equal(0, convert(0));
        Assert.Equal(65, convert(0.5));
        Assert.Equal(130, convert(1));

        // Never past the end of the track, whatever it is handed.
        Assert.Equal(130, convert(1.4));
        Assert.Equal(0, convert(-0.2));
    }

    [Fact]
    public void The_tooltip_says_what_the_button_will_do()
    {
        Assert.Equal("Stop", PlayTip.Instance.Convert(true, typeof(string), null, null!));
        Assert.Equal("Play the result again",
            PlayTip.Instance.Convert(false, typeof(string), null, null!));
    }
}
