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
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// A History row keeps its text inside the row (spec §2a).
/// </summary>
/// <remarks>
/// Reported from using the app: a clone's subtitle carries the reference
/// transcript, which is a sentence rather than a speaker name, and it ran
/// straight under the buttons on the right.
///
/// The cause is the same one the clone transcript field had. A horizontal
/// StackPanel measures its children against infinite width, so
/// <c>TextTrimming</c> never fires — the text does not overflow a box, it makes
/// the box the size of the text.
/// </remarks>
public sealed class HistoryOverflowTests : HeadlessWindows
{
    private readonly string _outputs =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public HistoryOverflowTests() => Directory.CreateDirectory(_outputs);

    protected override void DisposeCore()
    {
        if (Directory.Exists(_outputs)) Directory.Delete(_outputs, recursive: true);
    }

    private const string LongTranscript =
        "The sun rose slowly over the mountains, casting long golden shadows across "
        + "the valley below. Birds began to sing in the tall pine trees, and a gentle "
        + "breeze carried the scent of wildflowers through the crisp morning air.";

    [AvaloniaFact]
    public void A_long_subtitle_ends_in_an_ellipsis()
    {
        // Deliberately not a bounds assertion. The subtitle sits in a column
        // that was already bounded, so it never overflowed — it was clipped
        // mid-word, hard against the buttons, with nothing to say it had been
        // cut. Measuring where it ends cannot tell those two apart; only the
        // trimming can, which is what was missing.
        var (window, row) = ShowRow();

        var subtitle = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Text == row.Subtitle);

        Assert.Equal(TextTrimming.CharacterEllipsis, subtitle.TextTrimming);
        AssertEndsInsideTheRow(window, subtitle, "the subtitle");
    }

    [AvaloniaFact]
    public void A_long_summary_stops_before_the_buttons()
    {
        var (window, row) = ShowRow();

        var summary = window.GetLogicalDescendants().OfType<TextBlock>()
            .First(t => t.Text == row.Summary);

        AssertEndsInsideTheRow(window, summary, "the summary");
    }

    [AvaloniaFact]
    public void The_buttons_are_still_reachable()
    {
        // The half that matters most: the text pushing them out of the window
        // takes Download, Copy, Reveal and Trash with it.
        var (window, _) = ShowRow();

        var buttons = window.GetLogicalDescendants().OfType<HistoryView>().Single()
            .GetLogicalDescendants().OfType<Button>().ToList();

        Assert.NotEmpty(buttons);

        foreach (var button in buttons)
        {
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window);
            Assert.NotNull(right);
            Assert.True(right!.Value.X <= window.Bounds.Width,
                $"a row button reaches {right.Value.X} in a window {window.Bounds.Width} wide");
        }
    }

    private static void AssertEndsInsideTheRow(Window window, TextBlock text, string what)
    {
        var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), window);

        Assert.NotNull(right);
        Assert.True(right!.Value.X <= window.Bounds.Width,
            $"{what} reaches {right.Value.X} in a window {window.Bounds.Width} wide");
    }

    /// <summary>One clone row, with the longest text the app can produce.</summary>
    private (Window Window, HistoryRow Row) ShowRow()
    {
        var path = Path.Combine(_outputs, "Voice-clone-20260819T120000.wav");
        WavWriter.Write(path, new short[2_400]);

        WavMetadata.TryWrite(path, new OutputMetadata
        {
            Mode = TtsMode.VoiceClone.DisplayName(),
            Text = "Hello how are you today, and what shall we talk about this morning?",
            Language = "english",

            // What a clone records as its voice: the transcript of the clip it
            // was taken from, which is a sentence rather than a name.
            ReferenceTranscript = LongTranscript,
            ModelRepo = "wavekat/Qwen3-TTS-0.6B-Base-ONNX",
            AppVersion = "0.1.0",
            Created = DateTimeOffset.UtcNow,
        });

        var model = new MainViewModel(
            new FakeEngine(), new FakePlayer(), new RecordingLog(), () => _outputs)
        {
            ShowingHistory = true,
        };

        model.History.Refresh();

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();

        return (window, Assert.Single(model.History.Rows));
    }
}
