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

using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// What a screen reader is told beyond a control's name (spec §12, #159).
/// </summary>
/// <remarks>
/// <para>
/// Measured on the automation peers of the real windows, the way
/// <see cref="AccessibleNameTests"/> does for names. Four things: decorative
/// glyphs are not announced, a running generation is announced, Doctor's
/// severities are words rather than a symbol, and a History row is one item
/// rather than three loose pieces of text before five buttons.
/// </para>
/// <para>
/// A peer is what UI Automation sees; whether Narrator or Orca then say the
/// right thing is a question for a real desktop session, which a headless test
/// cannot answer. These pin the tree those readers will walk.
/// </para>
/// </remarks>
public class ScreenReaderTests : HeadlessWindows
{
    private static AutomationPeer PeerOf(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control);

    private static IEnumerable<AutomationPeer> Descendants(AutomationPeer peer) =>
        peer.GetChildren().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    private (MainWindow Window, MainViewModel Model) Show(Func<string>? outputs = null)
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog(), outputs)
        {
            Script = "Hello there.",
        };

        var window = Open(new MainWindow { DataContext = model });
        window.UpdateLayout();
        return (window, model);
    }

    // ---- Decorative glyphs ----

    [AvaloniaFact]
    public void No_button_announces_the_picture_inside_it()
    {
        // The Path glyphs are drawn for the eye. They have no automation peer
        // of their own — measured: NoneAutomationPeer, not a control element,
        // not a content element — so a reader hears "Generate, button" and not
        // "Generate, Path, button". Pinned, because a glyph that grew a name
        // or a control type would start being read.
        var (window, model) = Show();
        ((FakeEngine)model.Engine).Publish(new EngineStatus(EngineState.Generating));
        window.UpdateLayout();

        foreach (var button in window.GetLogicalDescendants().OfType<Button>())
        {
            var announced = Descendants(PeerOf(button))
                .Where(p => p.IsControlElement() || p.IsContentElement())
                .ToList();

            // Only a text label may surface inside a button, and only its words.
            Assert.All(announced, p =>
            {
                Assert.Equal(AutomationControlType.Text, p.GetAutomationControlType());
                Assert.DoesNotContain("Path", p.GetClassName(), StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(p.GetName()), $"{button.Name}: an unnamed element is announced");
            });
        }
    }

    // ---- A running generation ----

    [AvaloniaFact]
    public void The_status_line_is_a_live_region()
    {
        // Off by default, which means a reader says the status only when focus
        // lands on it — and focus never lands on it during a run. Polite: it
        // waits for the reader to finish what it was saying.
        var (window, model) = Show();
        ((FakeEngine)model.Engine).Publish(
            new EngineStatus(EngineState.Generating, Detail: "49 frames · 3.9s of speech so far", Frames: 49));
        window.UpdateLayout();

        var status = window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Text == model.Status);

        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.Contains("3.9s of speech", PeerOf(status).GetName(), StringComparison.Ordinal);
    }

    // ---- Doctor ----

    [AvaloniaTheory]
    [InlineData(DoctorSeverity.Blocker, "Blocker")]
    [InlineData(DoctorSeverity.Warning, "Warning")]
    [InlineData(DoctorSeverity.Ok, "OK")]
    public void A_finding_says_its_severity_in_a_word(DoctorSeverity severity, string word)
    {
        // The glyph beside a finding — ✕, !, ✓ — reached a reader as the symbol
        // or as nothing. §12: severity in words. The word goes into the
        // title's accessible name, and the glyph leaves the tree.
        var panel = MainWindow.BuildFindings(new DoctorReport(TtsMode.PresetVoice, [
            new DoctorFinding("Output folder", "Cannot write there.", severity),
        ]));
        var (window, _) = Show();
        window.Content = panel;
        window.UpdateLayout();

        var announced = Descendants(PeerOf(panel))
            .Where(p => p.IsControlElement() || p.IsContentElement())
            .Select(p => p.GetName())
            .ToList();

        Assert.Equal([$"{word}: Output folder", "Cannot write there."], announced);
    }

    // ---- History rows ----

    private static string Populate()
    {
        var outputs = Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputs);

        var path = Path.Combine(outputs, "Preset-voice-20260101T000001.wav");
        WavWriter.Write(path, new short[2_400]);
        WavMetadata.TryWrite(path, new OutputMetadata
        {
            Mode = TtsMode.PresetVoice.DisplayName(),
            Text = "Hello there",
            Language = "english",
            Speaker = "ryan",
            ModelRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
            AppVersion = "0.1.0",
            Created = DateTimeOffset.UtcNow,
        });

        return outputs;
    }

    [AvaloniaFact]
    public void A_history_row_is_one_item_with_its_buttons_as_children()
    {
        // Before: the row's Border had no peer, and a reader walking the list
        // met "Preset voice", "Hello there" and "Ryan · date · size" as three
        // unrelated pieces of text, then five buttons. After: one list item,
        // named with all three, and the five buttons inside it.
        var outputs = Populate();
        try
        {
            var (window, model) = Show(() => outputs);
            model.ShowingHistory = true;
            window.UpdateLayout();

            var list = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "List");
            var row = list.GetVisualDescendants().OfType<Border>().First(b => ToolTip.GetTip(b) is string);
            var peer = PeerOf(row);

            Assert.True(peer.IsControlElement(), "the row is not in the control view");
            Assert.Equal(AutomationControlType.ListItem, peer.GetAutomationControlType());
            Assert.StartsWith("Preset voice: Hello there. Ryan", peer.GetName(), StringComparison.Ordinal);

            var announced = Descendants(peer).Where(p => p.IsControlElement() || p.IsContentElement()).ToList();

            // The five buttons, and nothing else: the text is in the row's name.
            Assert.Equal(5, announced.Count);
            Assert.All(announced, p => Assert.Equal(AutomationControlType.Button, p.GetAutomationControlType()));
            Assert.Contains(announced, p => p.GetName() == "Move this clip to the Trash");
        }
        finally
        {
            Directory.Delete(outputs, recursive: true);
        }
    }

    [AvaloniaFact]
    public void The_rows_sentence_puts_the_category_first()
    {
        var row = new HistoryRow(new GeneratedOutput(
            @"C:\out\Voice-design-20260101T000001.wav",
            new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero),
            5_000,
            new OutputMetadata
            {
                Mode = "Voice design",
                Text = "Hello there",
                Language = "english",
                ModelRepo = "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX",
                AppVersion = "0.1.0",
                Created = new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero),
            }));

        Assert.StartsWith("Voice design: Hello there.", row.AccessibleName, StringComparison.Ordinal);
    }
}
