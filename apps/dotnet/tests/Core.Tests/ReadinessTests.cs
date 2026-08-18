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

using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// §1's rule that Generate is unavailable until a mode has what it needs, and
/// says why.
/// </summary>
public class GenerationReadinessTests
{
    private static GenerateRequest Preset(string text = "Hello") =>
        new(TtsMode.PresetVoice, text, "english", "ryan");

    [Fact]
    public void Preset_voice_needs_only_text_because_a_speaker_is_always_selected()
    {
        Assert.True(GenerationReadiness.CanGenerate(Preset()));
        Assert.Null(GenerationReadiness.BlockedReason(Preset()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void No_text_blocks_every_mode(string text)
    {
        // Whitespace counts as nothing. In the original this was inconsistent:
        // the button trimmed and the example strip did not, so one typed space
        // hid the examples AND left Generate disabled — restoring the dead end
        // the examples exist to remove.
        Assert.False(GenerationReadiness.CanGenerate(Preset(text)));
        Assert.Contains("text", GenerationReadiness.BlockedReason(Preset(text))!);
    }

    [Fact]
    public void Voice_design_needs_a_description()
    {
        // Without this check the mode generates an arbitrary voice from an empty
        // description, which is what the original did.
        var request = new GenerateRequest(TtsMode.VoiceDesign, "Hello", "english");

        Assert.False(GenerationReadiness.CanGenerate(request));
        Assert.Contains("Describe the voice", GenerationReadiness.BlockedReason(request)!);
    }

    [Fact]
    public void Voice_design_is_ready_once_it_has_one()
    {
        var request = new GenerateRequest(
            TtsMode.VoiceDesign, "Hello", "english", Instruct: "Warm narrator");

        Assert.True(GenerationReadiness.CanGenerate(request));
    }

    [Fact]
    public void Voice_clone_needs_a_reference_clip()
    {
        // Checked before the button rather than inside the engine: the engine
        // does reject this, but only after preparing the model — which on a
        // first run means waiting out a multi-gigabyte download to be told a
        // file is missing.
        var request = new GenerateRequest(TtsMode.VoiceClone, "Hello", "english");

        Assert.False(GenerationReadiness.CanGenerate(request));
        Assert.Contains("recording", GenerationReadiness.BlockedReason(request)!);
    }

    [Fact]
    public void Voice_clone_is_ready_once_it_has_one()
    {
        var request = new GenerateRequest(
            TtsMode.VoiceClone, "Hello", "english", ReferenceAudioPath: "/tmp/clip.wav");

        Assert.True(GenerationReadiness.CanGenerate(request));
    }

    [Fact]
    public void Every_blocked_reason_is_a_sentence_naming_what_to_do()
    {
        // §1 requires the button to say why on hover, and §10 requires plain
        // sentences rather than jargon or codes.
        var blocked = new[]
        {
            Preset(""),
            new GenerateRequest(TtsMode.VoiceDesign, "Hello"),
            new GenerateRequest(TtsMode.VoiceClone, "Hello"),
        };

        foreach (var request in blocked)
        {
            var reason = GenerationReadiness.BlockedReason(request);
            Assert.NotNull(reason);
            Assert.EndsWith(".", reason);
            Assert.True(char.IsUpper(reason[0]));
        }
    }
}

/// <summary>§1's first-run examples.</summary>
public class ExamplePromptsTests
{
    [Fact]
    public void Preset_voice_offers_sentences_that_fill_the_script()
    {
        Assert.NotEmpty(ExamplePrompts.For(TtsMode.PresetVoice));
        Assert.True(ExamplePrompts.FillsScript(TtsMode.PresetVoice));
    }

    [Fact]
    public void Voice_design_offers_voice_descriptions_that_fill_the_description()
    {
        // Not the script. That field is what the mode adds and the one input
        // whose shape nobody guesses; the script is a sentence anyone can write.
        Assert.NotEmpty(ExamplePrompts.For(TtsMode.VoiceDesign));
        Assert.False(ExamplePrompts.FillsScript(TtsMode.VoiceDesign));
        Assert.Contains("describe a voice", ExamplePrompts.PromptFor(TtsMode.VoiceDesign)!);
    }

    [Fact]
    public void Voice_clone_deliberately_has_none()
    {
        // What it lacks on a first run is a reference recording, which no
        // shipped example can be — so filling its script would leave Generate
        // exactly as unavailable and teach the wrong thing about why.
        Assert.Empty(ExamplePrompts.For(TtsMode.VoiceClone));
        Assert.Null(ExamplePrompts.PromptFor(TtsMode.VoiceClone));
        Assert.False(ExamplePrompts.ShouldShow(TtsMode.VoiceClone, "", hasResult: false));
    }

    [Fact]
    public void Examples_show_on_an_unused_window()
    {
        Assert.True(ExamplePrompts.ShouldShow(TtsMode.PresetVoice, "", hasResult: false));
        Assert.True(ExamplePrompts.ShouldShow(TtsMode.PresetVoice, "   ", hasResult: false));
    }

    [Fact]
    public void Examples_vanish_once_the_script_has_anything_in_it()
    {
        Assert.False(ExamplePrompts.ShouldShow(TtsMode.PresetVoice, "Hi", hasResult: false));
    }

    [Fact]
    public void Examples_do_not_return_over_a_generated_result()
    {
        // "Nothing generated yet" is a real condition, not a restatement of an
        // empty script: clearing the box after a run leaves the result in the
        // bottom bar, and suggestions beside it read as the app forgetting what
        // it just did.
        Assert.False(ExamplePrompts.ShouldShow(TtsMode.PresetVoice, "", hasResult: true));
    }
}

public class LanguageAndSpeakerTests
{
    [Fact]
    public void The_language_list_is_auto_plus_the_ten_the_spec_names()
    {
        Assert.Equal("auto", Languages.All[0]);
        Assert.Equal(11, Languages.All.Count);

        foreach (var expected in new[]
                 {
                     "english", "chinese", "japanese", "korean", "german",
                     "french", "russian", "portuguese", "spanish", "italian",
                 })
        {
            Assert.Contains(expected, Languages.All);
        }
    }

    [Fact]
    public void The_dialects_the_model_supports_are_not_offered_without_a_spec_change()
    {
        // The preset-voice export's config carries beijing_dialect and
        // sichuan_dialect. §1 does not list them, and a mode's options are
        // observable behaviour.
        Assert.DoesNotContain("beijing_dialect", Languages.All);
        Assert.DoesNotContain("sichuan_dialect", Languages.All);
    }

    [Fact]
    public void The_fallback_speakers_match_the_published_export()
    {
        // Verified against embeddings/speaker_ids.json in the real repository,
        // and against the macOS app's list. A picker that offers a speaker the
        // model does not have fails at generation.
        Assert.Equal(9, FallbackSpeakers.All.Count);
        Assert.Equal("Ryan", FallbackSpeakers.Default);

        foreach (var expected in new[]
                 {
                     "Ryan", "Aiden", "Vivian", "Serena", "Uncle_Fu",
                     "Dylan", "Eric", "Ono_Anna", "Sohee",
                 })
        {
            Assert.Contains(expected, FallbackSpeakers.All);
        }
    }
}
