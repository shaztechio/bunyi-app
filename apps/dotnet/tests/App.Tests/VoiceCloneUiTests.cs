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

    private static MainViewModel New(FakeEngine? engine = null) =>
        new(engine ?? new FakeEngine(), new FakePlayer(), new RecordingLog());
}
