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
/// glyphs are not announced, a running generation is announceable, Doctor's
/// severities are words rather than a symbol, and a History row is one item
/// rather than three loose pieces of text before five buttons.
/// </para>
/// <para>
/// <b>What layer this file pins, and what it does not.</b> Every assertion
/// here reads an Avalonia <see cref="AutomationPeer"/> in a headless harness.
/// Narrator reads the Windows UI Automation tree and Orca reads AT-SPI, each a
/// bridge away from a peer. So a green run is evidence that <i>the tree a
/// reader walks is built correctly</i>, and it is not evidence about what
/// either reader says out loud — #192 was filed because these tests were cited
/// as the second thing when they only ever showed the first.
/// </para>
/// <para>
/// The far side is checked elsewhere. <c>tools/UiaProbe</c> walks the real UIA
/// tree of a running window on Windows and reads what a peer test cannot see
/// across the bridge; <see href="https://github.com/shaztechio/bunyi-app/issues/159">#159</see>
/// keeps the manual Narrator and Orca passes that neither can replace.
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
    public void The_status_lines_peer_reports_a_polite_live_setting()
    {
        // Off by default, which means a reader says the status only when focus
        // lands on it — and focus never lands on it during a run. Polite: it
        // waits for the reader to finish what it was saying.
        //
        // Read off the *peer*, not off the attached property. #192's charge was
        // that the old assertion — AutomationProperties.GetLiveSetting(status)
        // — proved only that the value had been stored. Going through
        // ControlAutomationPeer.GetLiveSettingCore() proves the one further step
        // this harness can reach: that the attribute arrives at the peer whose
        // GetLiveSetting() the Win32 bridge consults before raising
        // LiveRegionChanged.
        //
        // The step after that is a UIA event on a real desktop, which is what
        // tools/UiaProbe is for. See the class remarks.
        var (window, model) = Show();
        ((FakeEngine)model.Engine).Publish(
            new EngineStatus(EngineState.Generating, Detail: "49 frames · 3.9s of speech so far", Frames: 49));
        window.UpdateLayout();

        var status = window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Text == model.Status);

        Assert.Equal(AutomationLiveSetting.Polite, PeerOf(status).GetLiveSetting());
        Assert.Contains("3.9s of speech", PeerOf(status).GetName(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void The_status_lines_peer_renames_itself_when_the_status_changes()
    {
        // The other half of the same chain, and the half nothing pinned before.
        // Avalonia's Win32 bridge raises LiveRegionChanged from a *Name* change
        // on the peer; TextBlockAutomationPeer is what turns a Text change into
        // one. If that ever stopped happening, LiveSetting would go on reading
        // Polite and the region would go quiet — which is exactly the failure
        // #192 suspected. So: change the status, expect the peer to say its name
        // changed.
        var (window, model) = Show();
        window.UpdateLayout();

        var status = window.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Text == model.Status);

        var renamed = new List<string?>();
        PeerOf(status).PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.NameProperty)
                renamed.Add(e.NewValue as string);
        };

        ((FakeEngine)model.Engine).Publish(
            new EngineStatus(EngineState.Generating, Detail: "49 frames · 3.9s of speech so far", Frames: 49));
        window.UpdateLayout();

        Assert.Contains(renamed, name => name?.Contains("3.9s of speech", StringComparison.Ordinal) == true);
    }

    // ---- Dropdowns ----

    [AvaloniaFact]
    public void Every_dropdown_is_named_by_the_label_beside_it()
    {
        // Reported from using the app with Narrator, and true: the pickers
        // announced as a bare "combo box". LabeledBy rather than a repeated
        // Name, so the words a reader says are the words on screen and cannot
        // drift from them.
        var (window, _) = Show();

        var combos = window.GetLogicalDescendants().OfType<ComboBox>()
            .Where(c => c.IsEffectivelyVisible)
            .ToList();

        Assert.NotEmpty(combos);
        Assert.All(combos, combo =>
        {
            var name = PeerOf(combo).GetName();
            Assert.False(string.IsNullOrWhiteSpace(name),
                "a dropdown announces as an unnamed combo box");
        });
    }

    [AvaloniaFact]
    public void Changing_a_dropdown_changes_what_it_reports_it_is_set_to()
    {
        // The user-visible bug: arrowing through the list moved the value and
        // announced nothing. Avalonia's ComboBoxAutomationPeer raises property
        // changes for IsDropDownOpen, Text and IsEditable and none for
        // SelectedItem, so a non-editable box emits nothing when the selection
        // moves — and raising Value ourselves could not help, because
        // ValuePatternIdentifiers.ValueProperty is not in the Win32 bridge's
        // property map at all.
        //
        // ItemStatus is the mapped property that fits, and ControlAutomationPeer
        // raises the peer change for it without being asked.
        //
        // Layer note, per this file's remarks: this pins that the property
        // tracks the selection and that the peer says so. That the event then
        // reaches a reader is `tools/UiaProbe watch`, which shows
        // "ItemStatus -> 'Korean' on 'Language'" for each arrow press.
        var (window, model) = Show();
        var language = window.GetLogicalDescendants().OfType<ComboBox>()
            .First(c => AutomationProperties.GetLabeledBy(c) is TextBlock { Text: "Language" });

        var announced = new List<object?>();
        PeerOf(language).PropertyChanged += (_, e) =>
        {
            if (e.Property == AutomationElementIdentifiers.ItemStatusProperty)
                announced.Add(e.NewValue);
        };

        var next = model.AllLanguages.First(l => l != model.Language);
        model.Language = next;
        window.UpdateLayout();

        // The words on screen, not the raw identifier the model carries.
        Assert.Equal(DisplayName.For(next), AutomationProperties.GetItemStatus(language));
        Assert.Contains(DisplayName.For(next), announced.Select(a => a as string));
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
        // finding's accessible name, and the glyph leaves the tree.
        //
        // One announced element, not two. #192: with the detail left in the
        // tree and the severity on the group around it, a reader that lands on
        // the detail announces "Cannot write there." and is under no obligation
        // to have said "Blocker" on the way in. A name cannot be read half-way,
        // so the whole finding is the name and everything inside it is Raw.
        var panel = MainWindow.BuildFindings(new DoctorReport(TtsMode.PresetVoice, [
            new DoctorFinding("Output folder", "Cannot write there.", severity),
        ]));
        var (window, _) = Show();
        window.Content = panel;
        window.UpdateLayout();

        var announced = Descendants(PeerOf(panel))
            .Where(p => p.IsControlElement() || p.IsContentElement())
            .ToList();

        var finding = Assert.Single(announced);
        Assert.Equal($"{word}: Output folder. Cannot write there.", finding.GetName());

        // Text, not Group: there is nothing left inside to group, and an empty
        // group is a container a reader steps into and out of for nothing.
        Assert.Equal(AutomationControlType.Text, finding.GetAutomationControlType());
    }

    [AvaloniaFact]
    public void A_findings_severity_cannot_be_missed_by_landing_on_its_detail()
    {
        // The failure #192 described, stated as a test: nothing announced
        // anywhere under a report carries a detail without its severity.
        var panel = MainWindow.BuildFindings(new DoctorReport(TtsMode.PresetVoice, [
            new DoctorFinding("Model", "The preset voice model is downloaded and ready.", DoctorSeverity.Ok),
            new DoctorFinding("Output folder", "Cannot write there.", DoctorSeverity.Blocker),
        ]));
        var (window, _) = Show();
        window.Content = panel;
        window.UpdateLayout();

        var announced = Descendants(PeerOf(panel))
            .Where(p => p.IsControlElement() || p.IsContentElement())
            .Select(p => p.GetName() ?? string.Empty)
            .ToList();

        Assert.All(announced, name => Assert.Matches("^(Blocker|Warning|OK): ", name));

        // Blockers first, and each finding whole.
        Assert.Equal(
            [
                "Blocker: Output folder. Cannot write there.",
                "OK: Model. The preset voice model is downloaded and ready.",
            ],
            announced);
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
        //
        // #192 asked whether a reader announces that name rather than the child
        // text. It has no choice: the three text blocks are AccessibilityView
        // Raw, so the row's name is the only text under the row there is. That
        // is what the count below pins — five buttons and nothing else — and it
        // is the shape Doctor's findings were changed to for the same reason.
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

    [AvaloniaTheory]
    [InlineData("Hello there", "Voice design: Hello there. ")]
    [InlineData("Hello there.", "Voice design: Hello there. ")]
    [InlineData("Hello! We'll begin in just a few minutes.", "Voice design: Hello! We'll begin in just a few minutes. ")]
    [InlineData("Once upon a time, in a village by the sea…", "Voice design: Once upon a time, in a village by the sea… ")]
    [InlineData("Are you ready?", "Voice design: Are you ready? ")]
    public void The_rows_sentence_does_not_double_its_full_stop(string text, string expected)
    {
        // Found by tools/UiaProbe on the real tree, not here: rows were reading
        // "…in just a few minutes.. Serena", because the separator was added
        // whether or not the script had already ended the sentence. Most
        // scripts have, including two of the three built-in examples.
        var row = new HistoryRow(new GeneratedOutput(
            @"C:\out\Voice-design-20260101T000001.wav",
            new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero),
            5_000,
            new OutputMetadata
            {
                Mode = "Voice design",
                Text = text,
                Language = "english",
                ModelRepo = "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX",
                AppVersion = "0.1.0",
                Created = new DateTimeOffset(2026, 1, 1, 9, 30, 0, TimeSpan.Zero),
            }));

        Assert.StartsWith(expected, row.AccessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("..", row.AccessibleName, StringComparison.Ordinal);
    }
}
