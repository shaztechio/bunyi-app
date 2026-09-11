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
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Xunit;

namespace Bunyi.App.Tests;

public sealed class ScriptLayoutTests : HeadlessWindows
{
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

        var scroller = script.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
        Assert.True(bar.IsVisible);
        Assert.True(scroller.Viewport.Height > 0);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
        var barTop = bar.TranslatePoint(default, card)!.Value;
        Assert.True(barTop.Y >= labelBottom);
        Assert.True(barTop.Y + bar.Bounds.Height <= card.Bounds.Height - card.Padding.Bottom + 0.5);
        Assert.True(barTop.X + bar.Bounds.Width <= card.Bounds.Width - card.Padding.Right + 0.5);
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
