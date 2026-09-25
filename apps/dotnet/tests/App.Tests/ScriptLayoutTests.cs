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
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

public sealed class ScriptLayoutTests : HeadlessWindows
{
    [AvaloniaTheory]
    [InlineData(TtsMode.PresetVoice)]
    [InlineData(TtsMode.VoiceDesign)]
    public async Task Instructions_accept_newlines_and_resize_without_changing_generation_text(TtsMode mode)
    {
        var engine = new FakeEngine();
        using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog())
            { Mode = mode, Script = "Hello.", Instruct = "Calm delivery" };
        var window = Open(new MainWindow { DataContext = model, Width = 620, Height = 580 });
        var editor = window.FindControl<TextBox>("InstructBox")!;
        var handle = window.FindControl<Thumb>("InstructionResizeHandle")!;
        window.UpdateLayout();
        Assert.Equal(52, editor.Bounds.Height);
        Assert.Equal(22, editor.Padding.Right);
        var compactHeight = editor.Bounds.Height;
        AssertGrabberClear(editor, handle);
        var scroll = editor.GetVisualDescendants().OfType<ScrollViewer>().Single();
        model.Instruct = "First line\nSecond line";
        window.UpdateLayout();
        Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5);
        model.Instruct += "\nThird line";
        window.UpdateLayout();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        model.Instruct = "Calm delivery";
        editor.Focus();
        editor.CaretIndex = editor.Text!.Length;
        Press(PhysicalKey.Enter);
        Assert.Equal("Calm delivery" + Environment.NewLine, model.Instruct);
        Assert.Null(engine.LastRequest);
        Press(PhysicalKey.Tab);
        Assert.Same(handle, window.FocusManager!.GetFocusedElement());
        Press(PhysicalKey.ArrowDown);
        Assert.Equal(compactHeight + 20, editor.Bounds.Height);
        Press(PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Same(editor, window.FocusManager.GetFocusedElement());

        var form = window.FindControl<ScrollViewer>("GenerationScroller")!;
        form.ScrollToEnd();
        window.UpdateLayout();
        var start = handle.TranslatePoint(new Point(14, 8), window)!.Value;
        var beforeDrag = editor.Bounds.Height;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Vector(0, 30));
        window.UpdateLayout();
        Assert.Equal(beforeDrag + 30, editor.Bounds.Height);
        window.MouseMove(start + new Vector(0, 60));
        window.UpdateLayout();
        Assert.Equal(beforeDrag + 60, editor.Bounds.Height);
        AssertGrabberClear(editor, handle);
        window.MouseUp(start + new Vector(0, 60), MouseButton.Left);

        var instruction = string.Join('\n', Enumerable.Repeat("Warm narrator, with clear and measured delivery.", 30));
        model.Instruct = instruction;
        window.UpdateLayout();
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
        scroll.ScrollToEnd();
        window.UpdateLayout();
        Assert.True(scroll.Offset.Y > 0);
        AssertGrabberClear(editor, handle);

        var action = window.FindControl<Button>("GenerateButton")!;
        var actionPosition = action.TranslatePoint(default, window);
        form.ScrollToEnd();
        window.UpdateLayout();
        var handleBottom = handle.TranslatePoint(new Point(0, handle.Bounds.Height), form)!.Value.Y;
        Assert.InRange(handleBottom, handle.Bounds.Height, form.Viewport.Height + 0.5);
        Assert.Equal(actionPosition, action.TranslatePoint(default, window));
        handle.Focus();
        for (var i = 0; i < 20; i++) Press(PhysicalKey.ArrowDown);
        Assert.Equal(320, editor.Bounds.Height);
        for (var i = 0; i < 20; i++) Press(PhysicalKey.ArrowUp);
        Assert.Equal(compactHeight, editor.Bounds.Height);
        Assert.Equal(instruction, model.Instruct);

        var pending = model.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(instruction, engine.LastRequest!.Instruct);
        engine.Publish(new(EngineState.Generating));
        Assert.False(editor.IsEffectivelyEnabled);
        Assert.False(handle.IsEffectivelyEnabled);
        engine.Complete("output.wav");
        await pending;

        void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPressQwerty(key, modifiers);
            window.KeyReleaseQwerty(key, modifiers);
            window.UpdateLayout();
        }
    }

    [AvaloniaTheory]
    [InlineData(TtsMode.PresetVoice, 580)]
    [InlineData(TtsMode.PresetVoice, 1000)]
    [InlineData(TtsMode.VoiceDesign, 580)]
    [InlineData(TtsMode.VoiceClone, 580)]
    public async Task Script_resizes_independently_in_every_mode_and_preserves_generation_text(TtsMode mode, int windowHeight)
    {
        var engine = new FakeEngine();
        using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog())
        {
            Mode = mode, Instruct = "Warm narrator", ReferenceAudioPath = "reference.wav",
            ReferenceTranscript = "Reference words",
            Script = string.Join('\n', Enumerable.Repeat("A paragraph of script text.", 80))
        };
        var original = model.Script;
        var window = Open(new MainWindow { DataContext = model, Width = 620, Height = windowHeight });
        var editor = window.FindControl<TextBox>("ScriptBox")!;
        var handle = window.FindControl<Thumb>("ScriptResizeHandle")!;
        var form = window.FindControl<ScrollViewer>("GenerationScroller")!;
        window.UpdateLayout();
        AssertGrabberClear(editor, handle);
        Assert.Equal(4, editor.Padding.Right);
        editor.Focus();
        Press(PhysicalKey.Tab);
        Assert.Same(handle, window.FocusManager!.GetFocusedElement());
        var initialHeight = editor.Bounds.Height;
        var initialTop = editor.TranslatePoint(default, form)!.Value.Y;
        Press(PhysicalKey.ArrowDown);
        Assert.Equal(initialHeight + 20, editor.Bounds.Height);
        Assert.Equal(initialTop, editor.TranslatePoint(default, form)!.Value.Y);
        Press(PhysicalKey.ArrowUp);
        Assert.Equal(initialHeight, editor.Bounds.Height);
        Press(PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Same(editor, window.FocusManager.GetFocusedElement());

        form.ScrollToHome();
        window.UpdateLayout();
        var start = handle.TranslatePoint(new Point(8, 8), window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Vector(0, 30));
        window.UpdateLayout();
        Assert.Equal(initialHeight + 30, editor.Bounds.Height);
        window.MouseMove(start + new Vector(0, 60));
        window.UpdateLayout();
        Assert.Equal(initialHeight + 60, editor.Bounds.Height);
        window.MouseUp(start + new Vector(0, 60), MouseButton.Left);
        AssertGrabberClear(editor, handle);
        Assert.Equal(52, model.InstructionEditorHeight);
        foreach (var other in new[] { TtsMode.PresetVoice, TtsMode.VoiceDesign, TtsMode.VoiceClone })
        {
            model.Mode = other;
            window.UpdateLayout();
            Assert.Equal(initialHeight + 60, editor.Bounds.Height);
            AssertGrabberClear(editor, handle);
        }
        model.Mode = mode;
        handle.Focus();
        for (var i = 0; i < 45; i++) Press(PhysicalKey.ArrowDown);
        Assert.Equal(800, editor.Bounds.Height);
        handle.BringIntoView();
        window.UpdateLayout();
        var bottom = handle.TranslatePoint(new Point(0, handle.Bounds.Height), form)!.Value.Y;
        Assert.InRange(bottom, handle.Bounds.Height, form.Viewport.Height);
        for (var i = 0; i < 45; i++) Press(PhysicalKey.ArrowUp);
        Assert.Equal(140, editor.Bounds.Height);
        Assert.Equal(original, model.Script);
        var pending = model.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(original, engine.LastRequest!.Text);
        engine.Publish(new(EngineState.Generating));
        Assert.False(editor.IsEffectivelyEnabled);
        Assert.False(handle.IsEffectivelyEnabled);
        engine.Complete("output.wav");
        await pending;

        void Press(PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
        {
            window.KeyPressQwerty(key, modifiers);
            window.KeyReleaseQwerty(key, modifiers);
            window.UpdateLayout();
        }
    }

    [AvaloniaFact]
    public void Script_gutter_stays_clear_when_scrollbar_appears_and_disappears()
    {
        using var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog());
        var window = Open(new MainWindow { DataContext = model, Width = 620, Height = 580 });
        var editor = window.FindControl<TextBox>("ScriptBox")!;
        var handle = window.FindControl<Thumb>("ScriptResizeHandle")!;
        window.UpdateLayout();
        var viewport = editor.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var bar = viewport.GetVisualDescendants().OfType<ScrollBar>()
            .Single(b => b.Orientation == Orientation.Vertical);
        var width = viewport.Viewport.Width;
        foreach (var text in new[] { "", string.Join('\n', Enumerable.Repeat("Long script", 100)), "Short script", "" })
        {
            model.Script = text;
            window.UpdateLayout();
            Assert.Equal(text.Contains('\n'), bar.IsVisible);
            Assert.Equal(width, viewport.Viewport.Width);
            AssertGrabberClear(editor, handle);
            if (text.Length == 0)
            {
                var placeholder = editor.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Name == "PART_Placeholder");
                var right = placeholder.TranslatePoint(new Point(placeholder.Bounds.Width, 0), editor)!.Value.X;
                Assert.True(right <= handle.TranslatePoint(default, editor)!.Value.X - 2);
            }
        }
    }

    private static void AssertGrabberClear(TextBox editor, Thumb handle)
    {
        var corner = handle.TranslatePoint(default, editor)!.Value;
        Assert.InRange(corner.X, 0, editor.Bounds.Width - handle.Bounds.Width - 2);
        Assert.InRange(corner.Y, 0, editor.Bounds.Height - handle.Bounds.Height - 2);
        var presenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        var textRight = presenter.TranslatePoint(new Point(presenter.Bounds.Width, 0), editor)!.Value.X;
        Assert.True(textRight <= corner.X - 2, $"Text ends at {textRight}; grabber starts at {corner.X}.");
        var viewport = editor.GetVisualDescendants().OfType<ScrollViewer>().Single();
        if (editor.Name == "ScriptBox")
        {
            var bar = viewport.GetVisualDescendants().OfType<ScrollBar>()
                .Single(b => b.Orientation == Orientation.Vertical);
            if (bar.IsVisible)
            {
                var barTop = bar.TranslatePoint(default, editor)!.Value;
                Assert.InRange(Math.Abs(barTop.X - corner.X), 0, 1);
                Assert.Equal(handle.Bounds.Width, bar.Bounds.Width);
                Assert.True(barTop.Y + bar.Bounds.Height <= corner.Y - 2,
                    "The scrollbar must end above the grabber.");
                Assert.True(viewport.Viewport.Height > bar.Bounds.Height + 12,
                    "Only the scrollbar should shrink, not the text viewport.");
            }
        }
        else
        {
            var scrollRight = viewport.TranslatePoint(new Point(viewport.Bounds.Width, 0), editor)!.Value.X;
            Assert.True(scrollRight <= corner.X, "Scrollbar overlaps the grabber.");
        }
        var grip = handle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        Assert.NotNull(grip.Stroke);
        Assert.InRange(grip.Opacity, 0.2, 0.4);
    }


    [AvaloniaTheory]
    [InlineData(TtsMode.PresetVoice, 760, 680)]
    [InlineData(TtsMode.VoiceDesign, 760, 680)]
    [InlineData(TtsMode.VoiceClone, 760, 680)]
    [InlineData(TtsMode.PresetVoice, 620, 580)]
    [InlineData(TtsMode.VoiceDesign, 620, 580)]
    [InlineData(TtsMode.VoiceClone, 620, 580)]
    public void Script_heading_and_scrollbar_fit_inside_the_card(TtsMode mode, int width, int height)
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            Mode = mode,
            Script = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"Line {i}: a long script to scroll.")),
            ReferenceAudioPath = "reference.wav",
            ReferenceTranscript = "Allow me to introduce myself. I am Jarvis, a virtual artificial intelligence, importing all preferences from home interface.",
            LastOutputPath = "output.wav",
        };
        model.SavedVoices.Add(new SavedVoice(Guid.NewGuid(), "Jarvis", "reference.wav", model.ReferenceTranscript, DateTimeOffset.UtcNow));
        var window = Open(new MainWindow { DataContext = model, Width = width, Height = height });
        window.UpdateLayout();

        var label = window.FindControl<TextBlock>("ScriptLabel")!;
        var script = window.FindControl<TextBox>("ScriptBox")!;
        var card = script.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
        var labelBottom = label.TranslatePoint(new Point(0, label.Bounds.Height), card)!.Value.Y;
        var scriptTop = script.TranslatePoint(default, card)!.Value.Y;
        Assert.True(scriptTop >= labelBottom, $"Script starts at {scriptTop}, overlapping the heading ending at {labelBottom}.");
        Assert.True(scriptTop + script.Bounds.Height <= card.Bounds.Height - card.Padding.Bottom + 0.5,
            $"Script extends to {scriptTop + script.Bounds.Height} in a card {card.Bounds.Height} high.");

        AssertGrabberClear(script, window.FindControl<Thumb>("ScriptResizeHandle")!);
        var scroller = script.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
        Assert.True(bar.IsVisible);
        Assert.True(scroller.Viewport.Height > 0);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
        var barTop = bar.TranslatePoint(default, card)!.Value;
        Assert.True(barTop.Y >= labelBottom);
        Assert.True(barTop.Y + bar.Bounds.Height <= card.Bounds.Height - card.Padding.Bottom + 0.5);
        Assert.True(barTop.X + bar.Bounds.Width <= card.Bounds.Width - card.Padding.Right + 0.5);
        var text = script.GetVisualDescendants().OfType<TextPresenter>().Single();
        var textRight = text.TranslatePoint(new Point(text.Bounds.Width, 0), card)!.Value.X;
        Assert.True(barTop.X - textRight >= 8 - 0.5,
            $"Only {barTop.X - textRight} pixels separate the text from the scrollbar.");
        scroller.ScrollToEnd();
        window.UpdateLayout();
        Assert.True(scroller.Offset.Y > 0);
        Assert.Equal(scroller.Extent.Height - scroller.Viewport.Height, scroller.Offset.Y, 1);
        Assert.False(scroller.AllowAutoHide);

        var form = window.FindControl<ScrollViewer>("GenerationScroller")!;
        var picker = window.FindControl<ListBox>("ModePicker")!;
        var generate = window.FindControl<Button>("GenerateButton")!;
        var pickerPosition = picker.TranslatePoint(default, window);
        var generatePosition = generate.TranslatePoint(default, window);
        if (mode == TtsMode.VoiceClone)
        {
            // A large transcript must remain reachable without stealing the editor's height.
            model.ReferenceTranscript = string.Join(' ', Enumerable.Repeat(model.ReferenceTranscript, 10));
            window.UpdateLayout();
            Assert.True(form.Extent.Height > form.Viewport.Height);
            form.ScrollToEnd();
            window.UpdateLayout();
            var save = form.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save voice");
            var saveBottom = save.TranslatePoint(new Point(0, save.Bounds.Height), form)!.Value.Y;
            Assert.InRange(saveBottom, save.Bounds.Height, form.Viewport.Height + 0.5);
            Assert.Equal(pickerPosition, picker.TranslatePoint(default, window));
            Assert.Equal(generatePosition, generate.TranslatePoint(default, window));
            model.ReferenceTranscript = "A short reference transcript.";
        }

        // Restoring a large window should give spare room back to the editor.
        window.Width = 1000;
        window.Height = 1200;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(script.Bounds.Height > 140, $"Editor {script.Bounds}, form {form.Bounds}, extent {form.Extent}, viewport {form.Viewport}, window {window.Bounds}.");
        Assert.Equal(form.Extent.Height, form.Viewport.Height, 1);
    }
}
