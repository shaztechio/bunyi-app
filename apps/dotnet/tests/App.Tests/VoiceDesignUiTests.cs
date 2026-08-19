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
using Bunyi.Core.Models;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Voice design in the window (spec §1).
/// </summary>
/// <remarks>
/// The mode's whole difference on screen: a described voice in place of a
/// chosen one. What is tested here is what the user can and cannot reach,
/// because an input offered but ignored is the failure §1 names explicitly.
/// </remarks>
public class VoiceDesignUiTests : HeadlessWindows
{
    private (MainWindow Window, MainViewModel Model) Show(TtsMode mode)
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            Mode = mode,
        };

        return (Open(new MainWindow { DataContext = model }), model);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    [Fact]
    public void Every_mode_is_available_now()
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog());

        foreach (var mode in Enum.GetValues<TtsMode>())
        {
            model.Mode = mode;
            Assert.True(model.ModeIsAvailable, $"{mode} should be available");
        }
    }

    [Fact]
    public void Design_mode_offers_no_speaker_picker()
    {
        // The export has no speakers — the voice comes from the description —
        // so a picker here would change nothing, which is the trap §1 refuses
        // for clone mode's emotion field.
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog());

        model.Mode = TtsMode.PresetVoice;
        Assert.True(model.ShowSpeakers);

        model.Mode = TtsMode.VoiceDesign;
        Assert.False(model.ShowSpeakers);
    }

    [Fact]
    public void The_field_is_a_voice_in_design_mode_and_a_style_elsewhere()
    {
        // §1: preset voice takes a style instruction on top of a chosen
        // speaker; design mode's text IS the voice.
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog());

        model.Mode = TtsMode.PresetVoice;
        Assert.Equal("Style", model.InstructLabel);

        model.Mode = TtsMode.VoiceDesign;
        Assert.Equal("Voice", model.InstructLabel);
    }

    [Fact]
    public void The_placeholder_suggests_describing_a_voice()
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            Mode = TtsMode.VoiceDesign,
        };

        Assert.Contains("Describe the voice", model.InstructPlaceholder, StringComparison.Ordinal);
    }

    [Fact]
    public void The_subtitle_no_longer_says_design_is_unimplemented()
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            Mode = TtsMode.VoiceDesign,
        };

        Assert.DoesNotContain("Not implemented", model.ModeSubtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Describe", model.ModeSubtitle, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void The_window_hides_the_speaker_row_in_design_mode()
    {
        var (window, model) = Show(TtsMode.PresetVoice);
        window.UpdateLayout();

        var speakerRow = window.GetLogicalDescendants().OfType<ComboBox>()
            .First(c => c.ItemsSource == model.Speakers);
        var row = (Control)speakerRow.Parent!;

        Assert.True(row.IsVisible);

        model.Mode = TtsMode.VoiceDesign;
        window.UpdateLayout();

        Assert.False(row.IsVisible);
    }

    [AvaloniaFact]
    public void The_window_keeps_the_voice_field_in_design_mode()
    {
        // The one input the mode cannot work without.
        var (window, model) = Show(TtsMode.VoiceDesign);
        window.UpdateLayout();

        Assert.True(model.ShowInstruct);

        var field = window.GetLogicalDescendants().OfType<TextBox>()
            .First(t => t.Text == model.Instruct || t.PlaceholderText == model.InstructPlaceholder);

        Assert.True(field.IsVisible);
    }

    [AvaloniaFact]
    public void Generate_is_offered_in_design_mode_once_it_has_what_it_needs()
    {
        // It was not offered at all before: the mode was a tab that could do
        // nothing. It still waits for a description, which is the mode's one
        // required input.
        var (window, model) = Show(TtsMode.VoiceDesign);
        model.Script = "Hello there.";
        model.Instruct = "A warm female voice";
        window.UpdateLayout();

        var generate = Find<Button>(window, "GenerateButton");

        Assert.True(generate.IsVisible);
        Assert.True(generate.IsEffectivelyEnabled);
    }

    [Fact]
    public void Design_mode_waits_for_a_description_and_says_so()
    {
        // §1 requires the button to say why it is unavailable, and for this
        // mode the description is not optional the way a style instruction is.
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            Mode = TtsMode.VoiceDesign,
            Script = "Hello there.",
        };

        Assert.False(model.CanGenerate);
        Assert.Contains("Describe the voice", model.BlockedReason!, StringComparison.Ordinal);

        model.Instruct = "A warm female voice";

        Assert.True(model.CanGenerate);
        Assert.Null(model.BlockedReason);
    }

    [Fact]
    public void Preset_voice_still_needs_only_text()
    {
        // The style instruction stays optional there, which is the difference
        // between decorating a chosen voice and being the voice.
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
        {
            Mode = TtsMode.PresetVoice,
            Script = "Hello there.",
        };

        Assert.True(model.CanGenerate);
    }

    [AvaloniaFact]
    public void Nothing_apologises_for_clone_mode_any_more()
    {
        // The notice stays in the window for a mode added without an
        // implementation behind it, but no mode is in that state today — so it
        // must not be showing.
        var (window, _) = Show(TtsMode.VoiceClone);
        window.UpdateLayout();

        var notice = window.GetLogicalDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text?.Contains("not implemented") == true);

        Assert.True(notice is null || !notice.IsVisible,
            "the window still says a mode is unimplemented");
    }

    // ---- The export the mode downloads ----

    [Fact]
    public void Every_mode_has_an_export_to_download()
    {
        foreach (var mode in Enum.GetValues<TtsMode>())
        {
            Assert.True(ModelLayout.Exists(mode), $"{mode} has no layout");
            Assert.NotEmpty(ModelLayout.For(mode).Files);
        }
    }

    [Fact]
    public void No_two_modes_share_a_download_folder()
    {
        // They are different exports, and a shared folder would have one mode's
        // completeness check pass on another mode's files.
        var names = Enum.GetValues<TtsMode>().Select(m => ModelLayout.For(m).Id).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_design_layout_fetches_only_the_int4_variant()
    {
        // fp32 is another 12.70 GB of the same model. A layout that assumed a
        // flat export would pull 18.55 GB to use 4.27 GB.
        var files = ModelLayout.VoiceDesign.Files.Select(f => f.RelativePath).ToList();

        Assert.Contains("int4/vocoder.onnx", files);
        Assert.DoesNotContain(files, f => f.StartsWith("fp32/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_design_layout_asks_for_every_codebook_table()
    {
        // Fifteen, one per codebook after the first. A missing one is a frame
        // the pipeline cannot complete.
        var files = ModelLayout.VoiceDesign.Files.Select(f => f.RelativePath).ToList();

        for (var i = 0; i < 15; i++)
        {
            Assert.Contains($"embeddings/cp_codec_embedding_{i}.npy", files);
        }
    }

    [Fact]
    public void The_design_layout_pairs_every_graph_with_its_data()
    {
        // §3b's completeness rule: an interrupted download very often leaves
        // the small half of a pair.
        var pairs = ModelLayout.VoiceDesign.ExternalDataPairs.ToList();

        Assert.Equal(4, pairs.Count);
        Assert.All(pairs, p => Assert.StartsWith("int4/", p.Graph, StringComparison.Ordinal));
    }

    [Fact]
    public void The_design_download_is_the_size_the_repository_lists()
    {
        // Doctor answers "is there room for this" from this number before the
        // download starts, so it is measured rather than estimated.
        Assert.Equal(5_850_000_000, ModelLayout.VoiceDesign.ApproxDownloadBytes);

        // And within a whisker of the preset export's, which is the surprising
        // part: int4 more than pays for 2.8x the parameters.
        var difference = Math.Abs(
            ModelLayout.VoiceDesign.ApproxDownloadBytes - ModelLayout.PresetVoice.ApproxDownloadBytes);

        Assert.True(difference < 100_000_000, $"they differ by {difference / 1e9:F2} GB");
    }
}
