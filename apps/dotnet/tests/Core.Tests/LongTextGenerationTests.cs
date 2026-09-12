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
using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class LongTextGenerationTests
{
    private const string Sentence = "The morning light filled the room as we prepared to leave for the station. ";
    private static string LongText => string.Concat(Enumerable.Repeat(Sentence, 5));
    private static SynthesisResult Audio(float level = .2f) => new([], 24000, 25)
        { RawSamples = Enumerable.Repeat(level, 48000).ToArray() };

    [Theory]
    [InlineData(39, false)]
    [InlineData(40, false)]
    [InlineData(41, true)]
    public void Section_threshold_uses_unrounded_estimated_speech(int words, bool needsSections)
    {
        var estimate = SpeechDurationEstimate.ForText(string.Join(' ', Enumerable.Repeat("word", words)));
        Assert.Equal(words / 2.0, estimate.UpperSeconds);
        Assert.Equal(needsSections, estimate.NeedsSections);
        Assert.Equal(new SpeechDurationEstimate(0, 0), SpeechDurationEstimate.ForText(" ... "));
        Assert.True(SpeechDurationEstimate.ForText(new string('声', 61)).NeedsSections);
    }

    [Theory]
    [InlineData(Sentence)]
    [InlineData("Dr. Smith paid 3.14 dollars. “Really?” she asked.\n\nHe nodded. ")]
    [InlineData("你好世界。こんにちは世界！안녕하세요 세계? ")]
    [InlineData("e\u0301 🧑‍🚀 a\u0301 very long sentence with no punctuation ")]
    public void Splitting_preserves_every_character_and_bounds_sections(string phrase)
    {
        var input = string.Concat(Enumerable.Repeat(phrase, 20));
        var parts = SpeechSections.Split(input);
        Assert.True(parts.Count > 1);
        Assert.Equal(input, string.Concat(parts));
        Assert.All(parts, p => Assert.InRange(SpeechDurationEstimate.ForText(p).UpperSeconds, .01, 20));
        Assert.All(parts, p => Assert.False(char.IsLowSurrogate(p[0]) || char.IsHighSurrogate(p[^1])));
    }

    [Fact]
    public void Sentence_and_quoted_sentence_boundaries_are_preferred()
    {
        var parts = SpeechSections.Split("Dr. Smith paid 3.14 dollars. “Really?” she asked. " + Sentence, 6);
        Assert.Equal("Dr. Smith paid 3.14 dollars. “Really?” ", parts[0]);
    }

    [Theory]
    [InlineData(TtsMode.PresetVoice)]
    [InlineData(TtsMode.VoiceClone)]
    public async Task Completed_sections_preserve_voice_in_one_recording(TtsMode mode)
    {
        var requests = new List<GenerateRequest>();
        var request = new GenerateRequest(mode, LongText, "english", "ryan", "calm",
            "reference.wav", "Reference words.");
        var run = new LongTextGeneration((r, ct, p) =>
        {
            requests.Add(r);
            p.Report(25);
            return Task.FromResult(Audio());
        }, _ => { }, new LogStore());
        var result = await run.GenerateAsync(request, default);
        Assert.True(requests.Count > 1);
        Assert.Equal(LongText, string.Concat(requests.Select(r => r.Text)));
        Assert.All(requests, r =>
        {
            Assert.Equal(mode, r.Mode);
            Assert.Equal(request.Speaker, r.Speaker);
            Assert.Equal(request.Instruct, r.Instruct);
            Assert.Equal(request.ReferenceAudioPath, r.ReferenceAudioPath);
            Assert.Equal(request.ReferenceTranscript, r.ReferenceTranscript);
            Assert.InRange(r.SectionFrameLimit!.Value, 250, 563);
        });
        Assert.Equal(requests.Count * 48000 + (requests.Count - 1) * 2880, result.Samples.Length);
        Assert.Equal(25 * requests.Count, result.Frames);
    }

    [Fact]
    public async Task Failed_attempt_is_discarded_and_retries_preserve_all_words()
    {
        var attempted = 0;
        var acceptedText = new List<string>();
        var statuses = new List<EngineStatus>();
        var run = new LongTextGeneration((r, ct, p) =>
        {
            attempted++;
            p.Report(400);
            if (attempted == 1) throw new GenerationDidNotFinishException();
            acceptedText.Add(r.Text);
            return Task.FromResult(Audio());
        }, statuses.Add, new LogStore());
        var result = await run.GenerateAsync(new(TtsMode.VoiceClone, LongText), default);
        Assert.Equal(LongText, string.Concat(acceptedText));
        Assert.Contains(statuses, s => s.Detail!.Contains("Retrying"));
        Assert.Equal(0, statuses[1].Frames);
        Assert.Equal(acceptedText.Count * 48000 + (acceptedText.Count - 1) * 2880, result.Samples.Length);
    }

    [Fact]
    public async Task Repeated_runaway_failure_has_bounded_attempts()
    {
        var attempts = 0;
        var run = new LongTextGeneration((r, ct, p) =>
        { attempts++; throw new GenerationDidNotFinishException(); }, _ => { }, new LogStore());
        await Assert.ThrowsAsync<GenerationDidNotFinishException>(() => run.GenerateAsync(
            new(TtsMode.PresetVoice, LongText), default));
        Assert.Equal(3, attempts); // parent, half, quarter; do not continue after final failure
    }

    [Fact]
    public async Task Stop_during_first_section_does_not_start_another_attempt()
    {
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        var run = new LongTextGeneration((r, ct, p) =>
        { attempts++; stop.Cancel(); return Task.FromResult(Audio()); }, _ => { }, new LogStore());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.GenerateAsync(
            new(TtsMode.PresetVoice, LongText), stop.Token));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Designed_opening_is_reused_once_and_its_reference_is_cleaned_up(bool fail)
    {
        var requests = new List<GenerateRequest>();
        string? reference = null;
        var run = new LongTextGeneration((r, ct, p) =>
        {
            requests.Add(r);
            if (r.Mode == TtsMode.VoiceClone)
            {
                reference = r.ReferenceAudioPath;
                Assert.True(File.Exists(reference));
                Assert.Equal(requests[0].Text, r.ReferenceTranscript);
                Assert.Null(r.Instruct);
                if (fail) throw new InvalidDataException("test failure");
            }
            return Task.FromResult(Audio());
        }, _ => { }, new LogStore());
        var task = run.GenerateAsync(new(TtsMode.VoiceDesign, LongText, Instruct: "warm narrator"), default);
        if (fail) await Assert.ThrowsAsync<InvalidDataException>(() => task);
        else
        {
            await task;
            Assert.Equal(LongText, string.Concat(requests.Select(r => r.Text)));
            Assert.Equal(1, requests.Count(r => r.Mode == TtsMode.VoiceDesign));
        }
        Assert.NotNull(reference);
        Assert.False(File.Exists(reference));
    }

    [Fact]
    public async Task Whole_recording_gain_preserves_relative_section_levels()
    {
        var attempts = 0;
        var run = new LongTextGeneration((r, ct, p) =>
            Task.FromResult(Audio(++attempts == 1 ? 2 : .5f)), _ => { }, new LogStore());
        var result = await run.GenerateAsync(new(TtsMode.PresetVoice, LongText), default);
        Assert.InRange(result.Samples[1000], 32110, 32113);
        Assert.InRange(result.Samples[48000 + 2880 + 1000], 8026, 8029);
    }
}
