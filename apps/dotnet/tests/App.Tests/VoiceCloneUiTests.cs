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
using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Clone mode in the window (spec §1, §4).
/// </summary>
public sealed class VoiceCloneUiTests : HeadlessWindows
{
    // ---- Which rows belong to which mode ----

    [Fact]
    public void The_recording_row_belongs_to_clone_mode_alone()
    {
        var model = New();

        model.Mode = TtsMode.PresetVoice;
        Assert.False(model.ShowReference);

        model.Mode = TtsMode.VoiceDesign;
        Assert.False(model.ShowReference);

        model.Mode = TtsMode.VoiceClone;
        Assert.True(model.ShowReference);
    }

    [Fact]
    public void Clone_mode_shows_no_style_field_and_no_speakers()
    {
        // §1 forbids the style field here, and the voice comes from the
        // recording rather than a list.
        var model = New();
        model.Mode = TtsMode.VoiceClone;

        Assert.False(model.ShowInstruct);
        Assert.False(model.ShowSpeakers);
    }

    [Fact]
    public void Clone_mode_no_longer_calls_itself_unimplemented()
    {
        var model = New();
        model.Mode = TtsMode.VoiceClone;

        Assert.True(model.ModeIsAvailable);
        Assert.DoesNotContain("not implemented", model.ModeSubtitle, StringComparison.Ordinal);
    }

    // ---- Choosing a recording ----

    [Fact]
    public void Before_one_is_chosen_it_says_so_rather_than_showing_a_blank()
    {
        var model = New();
        model.Mode = TtsMode.VoiceClone;

        Assert.False(model.HasReference);
        Assert.Equal("No recording chosen", model.ReferenceName);
    }

    [Fact]
    public async Task Choosing_one_shows_its_name_rather_than_its_path()
    {
        // The path is long, mostly uninteresting, and on a narrow window pushes
        // everything else off screen.
        var model = New();
        model.ChooseReference = () => Task.FromResult<string?>(
            Path.Combine("C:", "somewhere", "deep", "my voice.wav"));

        await model.PickReferenceCommand.ExecuteAsync(null);

        Assert.True(model.HasReference);
        Assert.Equal("my voice.wav", model.ReferenceName);
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var model = New();
        model.ReferenceAudioPath = "already.wav";
        model.ChooseReference = () => Task.FromResult<string?>(null);

        await model.PickReferenceCommand.ExecuteAsync(null);

        Assert.Equal("already.wav", model.ReferenceAudioPath);
    }

    // ---- The transcript (spec §4) ----

    [Fact]
    public async Task Choosing_a_recording_listens_to_it()
    {
        // §4: a blank transcript is filled in on-device. It happens on choosing
        // rather than on Generate so there is still something to edit it with.
        var model = New();
        model.ChooseReference = () => Task.FromResult<string?>("clip.wav");
        model.Transcribe = (_, _) => Task.FromResult("This is what it says.");

        await model.PickReferenceCommand.ExecuteAsync(null);

        Assert.Equal("This is what it says.", model.ReferenceTranscript);
    }

    [Fact]
    public async Task A_transcript_already_typed_is_never_overwritten()
    {
        // §4 is explicit: a typed transcript always wins over auto-detection.
        var listened = false;
        var model = New();
        model.ReferenceTranscript = "I typed this myself.";
        model.ChooseReference = () => Task.FromResult<string?>("clip.wav");
        model.Transcribe = (_, _) => { listened = true; return Task.FromResult("Something else."); };

        await model.PickReferenceCommand.ExecuteAsync(null);

        Assert.Equal("I typed this myself.", model.ReferenceTranscript);
        Assert.False(listened, "it listened over a transcript the user had typed");
    }

    [Fact]
    public async Task Listening_again_does_replace_it()
    {
        // The one way to get a fresh transcript over a wrong one, which is why
        // the button exists.
        var model = New();
        model.ReferenceAudioPath = "clip.wav";
        model.ReferenceTranscript = "misheard";
        model.Transcribe = (_, _) => Task.FromResult("heard properly");

        await model.ListenAgainCommand.ExecuteAsync(null);

        Assert.Equal("heard properly", model.ReferenceTranscript);
    }

    [Fact]
    public async Task A_transcription_that_fails_leaves_the_field_typeable()
    {
        // §4 makes this a convenience. Losing it should not stop the work, and
        // a dialog would.
        var model = New();
        model.ChooseReference = () => Task.FromResult<string?>("clip.wav");
        model.Transcribe = (_, _) => throw new InvalidOperationException("no model");

        await model.PickReferenceCommand.ExecuteAsync(null);

        Assert.True(model.HasReference);
        Assert.Equal(string.Empty, model.ReferenceTranscript);
        Assert.Contains("type what it says", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_happens_when_there_is_nothing_to_listen_to()
    {
        var listened = false;
        var model = New();
        model.Transcribe = (_, _) => { listened = true; return Task.FromResult("x"); };

        await model.ListenAgainCommand.ExecuteAsync(null);

        Assert.False(listened);
    }

    // ---- Generate ----

    [Fact]
    public void Generate_waits_for_a_recording()
    {
        // §1 wants the button to say why it cannot be pressed.
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";

        Assert.False(model.CanGenerate);
        Assert.Contains("recording", model.BlockedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_a_recording_and_text_it_can_run()
    {
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";
        model.ChooseReference = () => Task.FromResult<string?>("clip.wav");
        model.Transcribe = (_, _) => Task.FromResult("what it says");

        await model.PickReferenceCommand.ExecuteAsync(null);

        Assert.True(model.CanGenerate);
    }

    [Fact]
    public async Task The_run_carries_the_recording_and_its_transcript()
    {
        var engine = new FakeEngine();
        var model = New(engine);
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";
        model.ChooseReference = () => Task.FromResult<string?>("clip.wav");
        model.Transcribe = (_, _) => Task.FromResult("what it says");

        await model.PickReferenceCommand.ExecuteAsync(null);

        // The fake holds a run open so tests can watch the busy window; this one
        // only cares what was asked for, so let it finish immediately.
        engine.Pending.SetResult(new GenerateResult("out.wav", default, 0, default));
        await model.GenerateCommand.ExecuteAsync(null);

        Assert.Equal("clip.wav", engine.LastRequest?.ReferenceAudioPath);
        Assert.Equal("what it says", engine.LastRequest?.ReferenceTranscript);
    }

    [Fact]
    public async Task The_other_modes_carry_neither()
    {
        // A leftover recording from a previous clone must not follow the user
        // into design mode, where nothing would use it but the metadata would
        // record it.
        var engine = new FakeEngine();
        var model = New(engine);
        model.ChooseReference = () => Task.FromResult<string?>("clip.wav");
        model.Transcribe = (_, _) => Task.FromResult("what it says");
        await model.PickReferenceCommand.ExecuteAsync(null);

        model.Mode = TtsMode.VoiceDesign;
        model.Script = "Something to say.";
        model.Instruct = "a warm older voice";

        engine.Pending.SetResult(new GenerateResult("out.wav", default, 0, default));
        await model.GenerateCommand.ExecuteAsync(null);

        Assert.Null(engine.LastRequest?.ReferenceAudioPath);
        Assert.Null(engine.LastRequest?.ReferenceTranscript);
    }

    // ---- Layout ----

    [AvaloniaFact]
    public void The_transcript_stays_inside_the_window()
    {
        // It did not. A horizontal StackPanel measures its children against
        // infinite width, so a wrapping TextBox never wraps — it grew to the
        // length of one line of transcript, ran off the right edge, and pushed
        // the Listen again button out of the window entirely.
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.ReferenceAudioPath = "clip.wav";
        model.ReferenceTranscript =
            "The sun rose slowly over the mountains, casting long golden shadows "
            + "across the valley below. Birds began to sing in the tall pine trees, "
            + "and a gentle breeze carried the scent of wildflowers.";

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();

        var field = window.GetLogicalDescendants().OfType<TextBox>()
            .First(t => t.Text == model.ReferenceTranscript);

        var right = field.TranslatePoint(new Point(field.Bounds.Width, 0), window);

        Assert.NotNull(right);
        Assert.True(right!.Value.X <= window.Bounds.Width,
            $"the transcript reaches {right.Value.X} in a window {window.Bounds.Width} wide");
    }

    [AvaloniaFact]
    public void The_listen_again_button_stays_on_screen()
    {
        // The half of the same bug that was easiest to miss: the button was not
        // clipped, it was pushed out of the window.
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.ReferenceAudioPath = "clip.wav";
        model.ReferenceTranscript = new string('a', 400);

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();

        var button = window.GetLogicalDescendants().OfType<Button>()
            .First(b => (b.Content as string) == "Listen again");

        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window);

        Assert.NotNull(right);
        Assert.True(right!.Value.X <= window.Bounds.Width,
            $"Listen again reaches {right.Value.X} in a window {window.Bounds.Width} wide");
    }

    // ---- While it is listening ----

    [Fact]
    public void The_transcript_cannot_be_typed_into_while_it_listens()
    {
        // Seconds of work about to overwrite the field. Leaving it editable
        // invites typing that is then thrown away.
        var model = New();

        Assert.True(model.CanEditTranscript);

        model.IsTranscribing = true;
        Assert.False(model.CanEditTranscript);
    }

    [Fact]
    public void The_spinner_turns_while_it_listens()
    {
        // The status line says "Listening to the recording…" for several
        // seconds. A status that changes with nothing moving beside it reads as
        // stuck.
        var model = New();

        Assert.False(model.ShowSpinner);

        model.IsTranscribing = true;
        Assert.True(model.ShowSpinner);

        model.IsTranscribing = false;
        Assert.False(model.ShowSpinner);
    }

    // ---- Saved voices (spec §5) ----

    [Fact]
    public void A_window_without_a_library_offers_no_saved_voices()
    {
        // And touches nobody's library on the way. The app supplies the real
        // one; anything else gets none rather than reading the user's folder.
        var model = New();

        Assert.False(model.HasSavedVoices);
        Assert.Empty(model.SavedVoices);
        Assert.False(model.CanSaveVoice);
    }

    [Fact]
    public void Saved_voices_appear_once_there_are_some()
    {
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Eric", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);

        Assert.True(model.HasSavedVoices);
        Assert.Equal("Eric", Assert.Single(model.SavedVoices).Name);
    }

    [Fact]
    public void Choosing_a_saved_voice_fills_the_recording_and_the_transcript()
    {
        // §5: selecting it fills reference + transcript. They were saved as a
        // pair and only mean anything as a pair — half of one is the state that
        // makes a clone finish the recording instead of speaking.
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        var saved = library.Save("Eric", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.SelectedVoice = model.SavedVoices[0];

        Assert.Equal(library.ClipPath(saved), model.ReferenceAudioPath);
        Assert.Equal("He shoots, he scores.", model.ReferenceTranscript);
    }

    [Fact]
    public void A_saved_voice_is_enough_to_generate_from()
    {
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Eric", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";
        model.SelectedVoice = model.SavedVoices[0];

        Assert.True(model.CanGenerate);
    }

    [Fact]
    public void Saving_needs_a_name_a_recording_and_a_transcript()
    {
        using var folder = new TempFolder();
        var model = WithLibrary(new VoiceLibrary(new RecordingLog(), folder.Path));

        Assert.False(model.CanSaveVoice);

        model.ReferenceAudioPath = folder.Clip();
        Assert.False(model.CanSaveVoice);

        model.ReferenceTranscript = "He shoots, he scores.";
        Assert.False(model.CanSaveVoice);

        model.NewVoiceName = "Eric";
        Assert.True(model.CanSaveVoice);
    }

    [Fact]
    public void Saving_while_it_is_still_listening_is_refused()
    {
        // The transcript is about to be replaced, so what would be saved is not
        // what the user is looking at.
        using var folder = new TempFolder();
        var model = WithLibrary(new VoiceLibrary(new RecordingLog(), folder.Path));

        model.ReferenceAudioPath = folder.Clip();
        model.ReferenceTranscript = "He shoots, he scores.";
        model.NewVoiceName = "Eric";
        model.IsTranscribing = true;

        Assert.False(model.CanSaveVoice);
    }

    [Fact]
    public void Saving_adds_it_and_clears_the_name_box()
    {
        using var folder = new TempFolder();
        var model = WithLibrary(new VoiceLibrary(new RecordingLog(), folder.Path));

        model.ReferenceAudioPath = folder.Clip();
        model.ReferenceTranscript = "He shoots, he scores.";
        model.NewVoiceName = "Eric";
        model.SaveVoiceCommand.Execute(null);

        Assert.Equal("Eric", Assert.Single(model.SavedVoices).Name);
        Assert.Equal(string.Empty, model.NewVoiceName);
        Assert.True(model.HasSavedVoices);
    }

    [Fact]
    public void Saving_points_the_fields_at_the_copy()
    {
        // Not at the file the user picked. From here on the library's copy is
        // the recording, and it is the one that survives them tidying up.
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        var model = WithLibrary(library);
        var original = folder.Clip();

        model.ReferenceAudioPath = original;
        model.ReferenceTranscript = "He shoots, he scores.";
        model.NewVoiceName = "Eric";
        model.SaveVoiceCommand.Execute(null);

        Assert.NotEqual(original, model.ReferenceAudioPath);
        Assert.Equal(library.ClipPath(model.SavedVoices[0]), model.ReferenceAudioPath);
    }

    [Fact]
    public void Deleting_removes_it_from_the_list()
    {
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Eric", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.SelectedVoice = model.SavedVoices[0];
        model.DeleteVoiceCommand.Execute(null);

        Assert.Empty(model.SavedVoices);
        Assert.False(model.HasSavedVoices);
        Assert.Null(model.SelectedVoice);
    }

    [Fact]
    public void Deleting_the_voice_in_use_empties_the_fields()
    {
        // Reported from using the app: after deleting, the Recording row showed
        // the library's copy by its id — a GUID, for a file that no longer
        // existed. Generate would have failed on it.
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Test01", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.SelectedVoice = model.SavedVoices[0];
        Assert.True(model.HasReference);

        model.DeleteVoiceCommand.Execute(null);

        Assert.False(model.HasReference);
        Assert.Null(model.ReferenceAudioPath);
        Assert.Equal(string.Empty, model.ReferenceTranscript);
        Assert.Equal("No recording chosen", model.ReferenceName);
    }

    [Fact]
    public async Task Deleting_a_different_voice_leaves_the_chosen_recording_alone()
    {
        // Only the recording that went. A file the user picked themselves has
        // nothing to do with the library.
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Test01", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.ChooseReference = () => Task.FromResult<string?>("my own.wav");
        model.Transcribe = (_, _) => Task.FromResult("something else");
        await model.PickReferenceCommand.ExecuteAsync(null);

        model.SelectedVoice = model.SavedVoices[0];
        model.ReferenceAudioPath = "my own.wav";
        model.DeleteVoiceCommand.Execute(null);

        Assert.Equal("my own.wav", model.ReferenceAudioPath);
    }

    [Fact]
    public async Task Choosing_a_recording_by_hand_stops_claiming_a_saved_voice()
    {
        // Otherwise the picker still reads "Test01" beside a file that has
        // nothing to do with it.
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Test01", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.SelectedVoice = model.SavedVoices[0];

        model.ChooseReference = () => Task.FromResult<string?>("my own.wav");
        model.Transcribe = (_, _) => Task.FromResult("something else");
        await model.PickReferenceCommand.ExecuteAsync(null);

        Assert.Null(model.SelectedVoice);
        Assert.Equal("my own.wav", model.ReferenceName);
    }

    [Fact]
    public void Deleting_needs_something_selected()
    {
        using var folder = new TempFolder();
        var model = WithLibrary(new VoiceLibrary(new RecordingLog(), folder.Path));

        Assert.False(model.CanDeleteVoice);
    }

    [Fact]
    public void A_saved_voice_shows_its_name_rather_than_a_guid()
    {
        // The library names its copies after the entry's id, so the file name
        // is a GUID and says nothing about what was picked.
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Test01", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.SelectedVoice = model.SavedVoices[0];

        Assert.Contains("Test01", model.ReferenceName, StringComparison.Ordinal);
        Assert.DoesNotContain(".wav", model.ReferenceName, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recording_chosen_by_hand_still_shows_its_file_name()
    {
        var model = New();
        model.ReferenceAudioPath = Path.Combine("C:", "somewhere", "my voice.wav");

        Assert.Equal("my voice.wav", model.ReferenceName);
    }

    [AvaloniaFact]
    public void No_option_label_is_clipped()
    {
        // "Saved voice" did not fit a fixed 76px column. The labels share a
        // size group now, so the column is as wide as its widest — a test
        // rather than a bigger number, because the next long label would clip
        // just as silently.
        using var folder = new TempFolder();
        var library = new VoiceLibrary(new RecordingLog(), folder.Path);
        library.Save("Test01", folder.Clip(), "He shoots, he scores.");

        var model = WithLibrary(library);
        model.Mode = TtsMode.VoiceClone;
        model.SelectedVoice = model.SavedVoices[0];

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();

        var labels = window.GetLogicalDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("rowLabel") && t.IsEffectivelyVisible)
            .ToList();

        Assert.NotEmpty(labels);

        foreach (var label in labels)
        {
            // The control's own text layout, so this is the real font at the
            // real size. Measuring a stand-in TextBlock gets a fallback font
            // and overstates every width; reading DesiredSize is worse still,
            // because an explicit Width makes it report that width and the
            // clipping happens inside, where layout cannot see it.
            var needed = label.TextLayout.Width;

            Assert.True(label.Bounds.Width >= needed - 0.5,
                $"“{label.Text}” is {label.Bounds.Width:0.#} wide but its text needs {needed:0.#}");
        }
    }

    // ---- Saying what is missing (spec §1) ----

    [Fact]
    public void Nothing_is_marked_before_generate_is_pressed()
    {
        // A form that shows errors for fields nobody has reached yet is telling
        // the user off for not having finished.
        var model = New();
        model.Mode = TtsMode.VoiceClone;

        Assert.False(model.HasMissing);
        Assert.False(model.NeedsText);
        Assert.False(model.NeedsReference);
    }

    [Fact]
    public async Task Pressing_it_early_points_at_the_first_thing_missing()
    {
        var model = New();
        model.Mode = TtsMode.VoiceClone;

        await model.GenerateCommand.ExecuteAsync(null);

        Assert.True(model.NeedsText);
        Assert.Contains("text to speak", model.MissingReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(model.MissingReason, model.Status);
    }

    [Fact]
    public async Task With_text_it_points_at_the_recording()
    {
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";

        await model.GenerateCommand.ExecuteAsync(null);

        Assert.True(model.NeedsReference);
        Assert.False(model.NeedsText);
    }

    [Fact]
    public async Task Design_mode_points_at_the_description()
    {
        var model = New();
        model.Mode = TtsMode.VoiceDesign;
        model.Script = "Something to say.";

        await model.GenerateCommand.ExecuteAsync(null);

        Assert.True(model.NeedsInstruction);
    }

    [Fact]
    public async Task It_asks_the_window_to_put_the_cursor_there()
    {
        // Focus is the half that works without sight of the outline.
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";

        RequiredInput? asked = null;
        model.FocusRequested += (_, input) => asked = input;

        await model.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(RequiredInput.Reference, asked);
    }

    [Fact]
    public async Task The_mark_clears_the_moment_it_is_filled_in()
    {
        // A mark that outlives its problem is worse than none.
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";

        await model.GenerateCommand.ExecuteAsync(null);
        Assert.True(model.NeedsReference);

        model.ReferenceAudioPath = "clip.wav";
        model.ReferenceTranscript = "what it says";

        Assert.False(model.HasMissing);
    }

    [Fact]
    public async Task A_run_that_can_start_marks_nothing()
    {
        var engine = new FakeEngine();
        var model = New(engine);
        model.Script = "Something to say.";

        engine.Pending.SetResult(new GenerateResult("out.wav", default, 0, default));
        await model.GenerateCommand.ExecuteAsync(null);

        Assert.False(model.HasMissing);
        Assert.NotNull(engine.LastRequest);
    }

    [AvaloniaFact]
    public void Generate_is_pressable_even_when_the_form_is_incomplete()
    {
        // The whole point. A disabled button cannot be hovered for the tooltip
        // that explains it, and a screen reader skips it.
        var model = New();
        model.Mode = TtsMode.VoiceClone;

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();

        var button = window.GetLogicalDescendants().OfType<Button>()
            .First(b => b.Name == "GenerateButton");

        Assert.False(model.CanGenerate);
        Assert.True(button.IsEffectivelyEnabled, "Generate cannot be pressed, so it cannot explain itself");
    }

    // ---- Switching tabs ----

    [Fact]
    public void Generate_wakes_up_when_a_recording_is_chosen()
    {
        // It did not. Readiness was satisfied and nothing told the button, so
        // it stayed disabled until some unrelated change refreshed it — which
        // is why it seemed to depend on switching tabs.
        var model = New();
        model.Mode = TtsMode.VoiceClone;
        model.Script = "Something to say.";

        Assert.False(model.CanGenerate);

        var seen = false;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.CanGenerate)) seen = true;
        };

        model.ReferenceAudioPath = "clip.wav";
        model.ReferenceTranscript = "what it says";

        Assert.True(model.CanGenerate);
        Assert.True(seen, "nothing announced that Generate had become usable");
    }

    [Fact]
    public void Returning_to_the_tab_you_left_still_refreshes_it()
    {
        // Preset → History → Preset sets the segment without changing Mode, so
        // OnModeChanged never fires. That path has to refresh anyway or the
        // button keeps whatever it last computed.
        var model = New();
        model.Script = "Something to say.";

        model.SelectedSegment = HistorySegment.Instance;

        var seen = false;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.CanGenerate)) seen = true;
        };

        model.SelectedSegment = TtsMode.PresetVoice;

        Assert.True(seen, "coming back from History never refreshed Generate");
        Assert.True(model.CanGenerate);
    }

    [Fact]
    public void Switching_tabs_puts_the_player_away()
    {
        // The clip belongs to the tab that made it. Carried across, preset
        // voice's result sat under clone mode's controls with nothing saying
        // where it came from.
        var model = New();
        model.LastOutputPath = "out.wav";

        Assert.True(model.HasResult);

        model.SelectedSegment = TtsMode.VoiceDesign;

        Assert.False(model.HasResult);
        Assert.Null(model.LastOutputPath);
    }

    [Fact]
    public void Switching_tabs_stops_the_audio()
    {
        // Otherwise it keeps playing with no visible way to stop it.
        var player = new FakePlayer();
        var model = new MainViewModel(new FakeEngine(), player, new RecordingLog())
        {
            LastOutputPath = "out.wav",
        };

        model.PlayCommand.Execute(null);
        Assert.True(model.IsPlaying);

        model.SelectedSegment = TtsMode.VoiceDesign;

        Assert.False(model.IsPlaying);
    }

    [Fact]
    public void Re_selecting_the_same_tab_leaves_a_finished_clip_alone()
    {
        // Avalonia re-sets the same segment during layout. Treating that as a
        // tab change would make a clip vanish while nobody touched anything.
        var model = New();
        model.Mode = TtsMode.PresetVoice;
        model.LastOutputPath = "out.wav";

        model.SelectedSegment = TtsMode.PresetVoice;

        Assert.Equal("out.wav", model.LastOutputPath);
        Assert.True(model.HasResult);
    }

    private static MainViewModel WithLibrary(VoiceLibrary library) =>
        new(new FakeEngine(), new FakePlayer(), new RecordingLog(), voices: library);

    private static MainViewModel New(FakeEngine? engine = null) =>
        new(engine ?? new FakeEngine(), new FakePlayer(), new RecordingLog());

    /// <summary>A scratch Voices folder that cleans up after itself.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

        public TempFolder() => Directory.CreateDirectory(Path);

        public string Clip()
        {
            var file = System.IO.Path.Combine(Path, $"source-{Guid.NewGuid():N}.wav");
            var pcm = new short[24_000];
            for (var i = 0; i < pcm.Length; i++) pcm[i] = (short)(Math.Sin(i * 0.05) * 8000);

            Bunyi.Core.Audio.WavWriter.Write(file, pcm, 24_000);
            return file;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
