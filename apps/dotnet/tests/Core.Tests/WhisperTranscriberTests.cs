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

using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Models;
using Bunyi.Core.Transcription;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Auto-transcribing a reference clip (spec §4).
/// </summary>
/// <remarks>
/// The language mapping and the tidying run anywhere. Actually transcribing
/// needs a 141 MB model, so those cases skip unless it is on the machine — and
/// report as skipped, so a green suite never means the transcription itself was
/// exercised.
/// </remarks>
public sealed class WhisperTranscriberTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public WhisperTranscriberTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- The language mapping ----

    [Theory]
    [InlineData("english", "en")]
    [InlineData("chinese", "zh")]
    [InlineData("japanese", "ja")]
    [InlineData("korean", "ko")]
    [InlineData("german", "de")]
    [InlineData("french", "fr")]
    [InlineData("russian", "ru")]
    [InlineData("portuguese", "pt")]
    [InlineData("spanish", "es")]
    [InlineData("italian", "it")]
    public void Every_language_the_app_offers_has_a_code(string name, string code)
    {
        // §1's list is spelled in words because the TTS configs use words.
        // Whisper wants ISO codes. A name that fell through would silently
        // become language detection, which transcribes the right sounds into
        // the wrong words.
        Assert.Equal(code, WhisperTranscriber.LanguageCode(name));
    }

    [Fact]
    public void The_apps_whole_language_list_is_covered()
    {
        // Guards the pairing rather than the pairs: adding a language to §1 and
        // forgetting this map is exactly the kind of omission that shows up as
        // a bad transcript months later.
        foreach (var language in Languages.All.Where(l => l != Languages.Default))
        {
            Assert.NotNull(WhisperTranscriber.LanguageCode(language));
        }
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("klingon")]
    public void Anything_else_lets_Whisper_decide(string? language)
    {
        // "auto" is a real answer, not a missing one — §1 offers it, and the
        // model is better at guessing than a wrong guess from us.
        Assert.Null(WhisperTranscriber.LanguageCode(language));
    }

    [Fact]
    public void Case_and_spacing_do_not_matter()
    {
        Assert.Equal("en", WhisperTranscriber.LanguageCode("English"));
        Assert.Equal("en", WhisperTranscriber.LanguageCode("  ENGLISH  "));
    }

    // ---- The transcript as a person sees it ----

    [Fact]
    public void Segment_joins_do_not_leave_double_spaces()
    {
        // Whisper hands back every segment with a leading space, so joining
        // them doubles up at each boundary. The transcript is shown and edited,
        // so it should read like something a person typed.
        Assert.Equal(
            "Hello there. How are you?",
            WhisperTranscriber.Tidy(" Hello there.  How are you?"));
    }

    [Fact]
    public void Newlines_inside_a_transcript_become_spaces()
    {
        Assert.Equal("one two three", WhisperTranscriber.Tidy("one\n two\r\nthree"));
    }

    [Fact]
    public void An_empty_result_stays_empty()
    {
        Assert.Equal(string.Empty, WhisperTranscriber.Tidy("   \n  "));
    }

    // ---- What the model download looks like ----

    [Fact]
    public void The_multilingual_model_is_the_one_fetched()
    {
        // base, not base.en. §1 offers ten languages, and the English-only
        // build would turn the other nine into nonsense that looks like
        // English — which a clone would then faithfully speak.
        var file = Assert.Single(ModelLayout.Whisper.Files);

        Assert.Equal("ggml-base.bin", file.RelativePath);
        Assert.True(file.Required);
    }

    [Fact]
    public void Its_size_is_stated_so_Doctor_can_ask_before_downloading()
    {
        // Roughly 141 MB on the hub. Doctor answers "is there room" from this.
        Assert.InRange(ModelLayout.Whisper.ApproxDownloadBytes, 140_000_000, 160_000_000);
    }

    [Fact]
    public void It_comes_from_whisper_cpps_own_repository()
    {
        // Plenty of accounts re-host these files; this is the one they copy
        // from, and the only one with a reason to still be there next year.
        Assert.Equal("ggerganov/whisper.cpp", ModelLayout.WhisperSource);

        // A repo id rather than a base URL, so the download goes through the
        // Hub — which is what lists the files and serves the digests §3b
        // verifies against. A bare URL has neither.
        var source = Assert.IsType<ModelSource.Repo>(
            ModelSource.Parse(ModelLayout.WhisperSource, defaultRepoId: "unused"));
        Assert.Equal(ModelLayout.WhisperSource, source.Id);
    }

    [Fact]
    public void It_is_far_smaller_than_a_voice_model()
    {
        // Worth keeping true: this is a second download on top of a mode's own,
        // and the moment it stops being small the trade needs revisiting.
        Assert.True(
            ModelLayout.Whisper.ApproxDownloadBytes < ModelLayout.PresetVoice.ApproxDownloadBytes / 10,
            "the transcription model has grown to a size worth reconsidering");
    }

    // ---- Behaviour that needs no model ----

    [Fact]
    public async Task A_clip_that_cannot_be_read_fails_before_the_model_is_fetched()
    {
        // 141 MB is a long way to go to find out the file was a text document.
        var asked = false;
        var notAudio = Path.Combine(_root, "notes.txt");
        File.WriteAllText(notAudio, "not a recording");

        using var transcriber = new WhisperTranscriber(
            _ =>
            {
                asked = true;
                return Task.FromResult("never-used.bin");
            },
            new RecordingLog());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => transcriber.TranscribeAsync(notAudio, "english", default));

        Assert.False(asked, "the model was fetched for a file that was never audio");
    }

    [Fact]
    public async Task A_missing_clip_says_so()
    {
        using var transcriber = new WhisperTranscriber(
            _ => Task.FromResult("never-used.bin"), new RecordingLog());

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => transcriber.TranscribeAsync(
                Path.Combine(_root, "gone.wav"), "english", default));
    }

    [Fact]
    public void Whisper_runs_at_16k_whatever_the_voice_model_wants()
    {
        // The two rates are different on purpose, and the decoder takes the
        // rate as an argument so neither has to know about the other.
        Assert.Equal(16_000, WhisperTranscriber.SampleRate);
        Assert.NotEqual(WhisperTranscriber.SampleRate, Bunyi.Core.Audio.Resampler.ModelSampleRate);
    }

    private sealed class RecordingLog : ILogSink
    {
        public void Log(string message) { }
    }
}
