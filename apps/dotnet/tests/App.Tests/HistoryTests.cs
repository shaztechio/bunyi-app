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
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>History, in the window (spec §2a).</summary>
public sealed class HistoryTests : HeadlessWindows
{
    private readonly string _outputs =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public HistoryTests() => Directory.CreateDirectory(_outputs);

    protected override void DisposeCore()
    {
        if (Directory.Exists(_outputs)) Directory.Delete(_outputs, recursive: true);
    }

    private string WriteClip(string name, string text = "Hello there.")
    {
        var path = Path.Combine(_outputs, name);
        WavWriter.Write(path, new short[2_400]);
        WavMetadata.TryWrite(path, new OutputMetadata
        {
            Mode = TtsMode.PresetVoice.DisplayName(),
            Text = text,
            Language = "english",
            Speaker = "ryan",
            ModelRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
            AppVersion = "0.1.0",
            Created = DateTimeOffset.UtcNow,
        });
        return path;
    }

    private (MainWindow Window, MainViewModel Model, FakeEngine Engine, FakePlayer Player) Open()
    {
        var engine = new FakeEngine();
        var player = new FakePlayer();
        var model = new MainViewModel(engine, player, new RecordingLog(), () => _outputs);
        var window = Open(new MainWindow { DataContext = model });
        return (window, model, engine, player);
    }

    /// <summary>
    /// Finds a button by name rather than by the words on it — several are
    /// icons now, and a test that keys off user-visible copy breaks every time
    /// the copy changes.
    /// </summary>
    private static Button ButtonWith(Window window, string name) =>
        window.GetLogicalDescendants().OfType<Button>().First(b => b.Name == name);

    [AvaloniaFact]
    public void History_lists_what_is_in_the_folder_when_it_is_shown()
    {
        WriteClip("Preset-voice-20260101T000000.wav", "The first clip.");
        var (_, model, _, _) = Open();

        model.ShowingHistory = true;

        var row = Assert.Single(model.History.Rows);
        Assert.Equal("The first clip.", row.Summary);
        Assert.False(model.History.IsEmpty);
    }

    [AvaloniaFact]
    public void The_folder_is_re_read_every_time_History_is_shown()
    {
        // §2a: "The folder is the record", not an in-app database — so a file
        // that appears or vanishes outside the app is reflected without any
        // state to invalidate.
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        Assert.True(model.History.IsEmpty);

        WriteClip("Preset-voice-20260101T000000.wav");
        model.ShowingHistory = false;
        model.ShowingHistory = true;

        Assert.Single(model.History.Rows);
    }

    [AvaloniaFact]
    public void An_empty_folder_says_so_rather_than_showing_nothing()
    {
        var (window, model, _, _) = Open();

        model.ShowingHistory = true;

        Assert.True(model.History.IsEmpty);
        var empty = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Text?.Contains("Nothing generated yet") == true);
        Assert.True(empty.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void There_is_no_Generate_button_in_History_but_Stop_stays()
    {
        // §2a: no Generate, because there is no text on screen to speak — it
        // would either do nothing or silently act on a mode that is not
        // visible. Stop stays, because a run can still be in progress and
        // hiding it would strand the user.
        WriteClip("Preset-voice-20260101T000000.wav");
        var (window, model, engine, _) = Open();
        model.Script = "Hello there.";

        model.ShowingHistory = true;
        Assert.False(ButtonWith(window, "GenerateButton").IsEffectivelyVisible);

        engine.Publish(new EngineStatus(EngineState.Generating));
        Assert.True(ButtonWith(window, "StopButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void The_single_file_player_is_hidden_in_History()
    {
        // §2a: History has its own per-row player, and two players on screen
        // can play over each other.
        WriteClip("Preset-voice-20260101T000000.wav");
        var (window, model, _, _) = Open();
        model.LastOutputPath = "/tmp/out.wav";
        Assert.True(ButtonWith(window, "PlayButton").IsEffectivelyVisible);

        model.ShowingHistory = true;

        Assert.False(ButtonWith(window, "PlayButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void History_stays_usable_while_a_generation_runs()
    {
        // §2a: it only reads the folder. The generation modes do not, because
        // switching one evicts the model the running job is using — which is
        // why the mode picker is disabled and this is not.
        WriteClip("Preset-voice-20260101T000000.wav");
        var (window, model, engine, _) = Open();
        model.ShowingHistory = true;

        engine.Publish(new EngineStatus(EngineState.Generating));

        var history = window.GetLogicalDescendants().OfType<HistoryView>().Single();
        Assert.True(history.IsEffectivelyEnabled);

        // From History, every segment is live — including the modes, which is
        // the only way back to the run that is going. This test used to assert
        // the reverse and was asserting a bug: it had the modes dead and
        // History live, so from a mode tab you could enter History mid-run and
        // then had no way out but stopping the work.
        var containers = window.GetLogicalDescendants().OfType<ListBox>().First()
            .GetRealizedContainers().OfType<ListBoxItem>().ToList();
        Assert.All(containers, c => Assert.True(c.IsEffectivelyEnabled));
    }

    [AvaloniaFact]
    public void Playing_a_row_starts_that_clip_and_marks_the_row()
    {
        var path = WriteClip("Preset-voice-20260101T000000.wav");
        var (_, model, _, player) = Open();
        model.ShowingHistory = true;
        var row = model.History.Rows[0];

        model.History.TogglePlayCommand.Execute(row);

        Assert.Equal(path, Assert.Single(player.Played));
        Assert.True(row.IsPlaying);
        Assert.Same(row, model.History.Playing);
    }

    [AvaloniaFact]
    public void Playing_the_same_row_again_stops_it_because_there_is_no_pause()
    {
        // §2a: play/stop, and deliberately no pause — a paused row is a third
        // state to explain for something a user would nearly always just play
        // again.
        WriteClip("Preset-voice-20260101T000000.wav");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        var row = model.History.Rows[0];

        model.History.TogglePlayCommand.Execute(row);
        model.History.TogglePlayCommand.Execute(row);

        Assert.False(row.IsPlaying);
        Assert.Null(model.History.Playing);
    }

    [AvaloniaFact]
    public void Playing_a_second_row_stops_the_first()
    {
        WriteClip("Preset-voice-20260101T000000.wav", "one");
        WriteClip("Preset-voice-20260102T000000.wav", "two");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        var first = model.History.Rows[0];
        var second = model.History.Rows[1];

        model.History.TogglePlayCommand.Execute(first);
        model.History.TogglePlayCommand.Execute(second);

        Assert.False(first.IsPlaying);
        Assert.True(second.IsPlaying);
    }

    [AvaloniaFact]
    public void Trash_does_nothing_without_a_confirmation()
    {
        // §2a insists the delete is confirmed. Refusing to act when nothing can
        // confirm means a misconfigured host cannot silently delete audio that
        // may be the user's only copy.
        var path = WriteClip("Preset-voice-20260101T000000.wav");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        model.History.ConfirmTrash = null;

        model.History.TrashCommand.Execute(model.History.Rows[0]);

        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public void Declining_the_confirmation_keeps_the_file()
    {
        var path = WriteClip("Preset-voice-20260101T000000.wav");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        model.History.ConfirmTrash = _ => Task.FromResult(false);

        model.History.TrashCommand.Execute(model.History.Rows[0]);

        Assert.True(File.Exists(path));
        Assert.Single(model.History.Rows);
    }

    [AvaloniaFact]
    public async Task Confirming_removes_the_file_and_the_row()
    {
        var path = WriteClip("Preset-voice-20260101T000000.wav");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        model.History.ConfirmTrash = _ => Task.FromResult(true);

        await model.History.TrashCommand.ExecuteAsync(model.History.Rows[0]);

        Assert.False(File.Exists(path));
        Assert.Empty(model.History.Rows);
    }

    [AvaloniaFact]
    public async Task Saving_a_copy_leaves_the_original_where_it_was()
    {
        // §2a: Download opens a save panel so the user chooses the
        // destination. It is a copy, not a move.
        var path = WriteClip("Preset-voice-20260101T000000.wav");
        var destination = Path.Combine(_outputs, "elsewhere.wav");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        model.History.ChooseSaveLocation = _ => Task.FromResult<string?>(destination);

        await model.History.DownloadCommand.ExecuteAsync(model.History.Rows[0]);

        Assert.True(File.Exists(destination));
        Assert.True(File.Exists(path));
    }

    [AvaloniaFact]
    public async Task Cancelling_the_save_panel_writes_nothing()
    {
        WriteClip("Preset-voice-20260101T000000.wav");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;
        model.History.ChooseSaveLocation = _ => Task.FromResult<string?>(null);

        await model.History.DownloadCommand.ExecuteAsync(model.History.Rows[0]);

        Assert.Single(Directory.GetFiles(_outputs, "*.wav"));
    }

    [AvaloniaFact]
    public void The_examples_never_appear_over_History()
    {
        var (_, model, _, _) = Open();
        Assert.True(model.ShowExamples);

        model.ShowingHistory = true;

        Assert.False(model.ShowExamples);
    }

    [AvaloniaFact]
    public void A_row_shows_the_mode_as_a_tag_and_the_details_on_hover()
    {
        WriteClip("Preset-voice-20260101T000000.wav", "Speak this.");
        var (_, model, _, _) = Open();
        model.ShowingHistory = true;

        var row = model.History.Rows[0];
        Assert.Equal("Preset voice", row.Mode);
        Assert.Contains("Text: Speak this.", row.Details);
        Assert.Contains("Model: elbruno/", row.Details);
        // "Ryan", not "ryan": the stored identifier is the model's, and the
        // row is for a person.
        Assert.Contains("Ryan", row.Subtitle, StringComparison.Ordinal);
    }
}
