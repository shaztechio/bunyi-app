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

using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Qwen;

namespace Bunyi.Core.Engine;

/// <summary>Retryable sections, one voice and one completed recording.</summary>
internal sealed class LongTextGeneration(
    Func<GenerateRequest, CancellationToken, IProgress<int>, Task<SynthesisResult>> synthesize,
    Action<EngineStatus> status,
    ILogSink log)
{
    private const int SampleRate = 24_000;
    private readonly List<float[]> _accepted = [];
    private long _samples;
    private int _frames;

    public async Task<SynthesisResult> GenerateAsync(GenerateRequest original, CancellationToken ct)
    {
        var current = original;
        string? temporaryReference = null;
        try
        {
            if (original.Mode == TtsMode.VoiceDesign)
            {
                // Fix the voice once, using a COMPLETE short part of the user's
                // script. Never use a clipped recording with a longer transcript.
                var opening = SpeechSections.Split(original.Text, 8)[0];
                float[]? voice = null;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        voice = await Attempt(current with { Text = opening }, 125,
                            attempt == 0 ? "Creating your designed voice" : "Retrying a shorter voice opening", ct);
                        break;
                    }
                    catch (GenerationDidNotFinishException) when (attempt < 2)
                    {
                        var parts = SpeechSections.Bisect(opening);
                        if (parts.Count < 2) throw;
                        opening = parts[0];
                    }
                }
                ct.ThrowIfCancellationRequested();
                temporaryReference = Path.Combine(Path.GetTempPath(), $"bunyi-designed-{Guid.NewGuid():N}.wav");
                WavWriter.Write(temporaryReference, DesignSpeechSynthesizer.ToPcm16(voice!, log));
                Accept(voice!, ct);
                current = original with
                {
                    Mode = TtsMode.VoiceClone,
                    Text = original.Text[opening.Length..],
                    Instruct = null, Speaker = null,
                    ReferenceAudioPath = temporaryReference,
                    ReferenceTranscript = opening
                };
            }

            var sections = SpeechSections.Split(current.Text);
            for (var i = 0; i < sections.Count; i++)
                await Section(current with { Text = sections[i] },
                    $"Section {i + 1} of {sections.Count}", 0, ct);

            ct.ThrowIfCancellationRequested();
            var combined = new float[checked((int)_samples)];
            var offset = 0;
            foreach (var part in _accepted)
            {
                part.CopyTo(combined, offset);
                offset += part.Length;
            }
            return new SynthesisResult(DesignSpeechSynthesizer.ToPcm16(combined, log), SampleRate, _frames);
        }
        finally
        {
            _accepted.Clear();
            if (temporaryReference is not null)
            {
                try { File.Delete(temporaryReference); }
                catch (IOException ex) { log.Log($"Could not remove temporary designed reference: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { log.Log($"Could not remove temporary designed reference: {ex.Message}"); }
            }
        }
    }

    private async Task Section(GenerateRequest section, string label, int depth, CancellationToken ct)
    {
        var upper = SpeechDurationEstimate.ForText(section.Text, section.Language).UpperSeconds;
        var limit = (int)Math.Ceiling(Math.Max(20, 2 * upper + 5) * TalkerLoop.FramesPerSecond);
        float[] audio;
        try { audio = await Attempt(section, limit, depth > 0 ? $"Retrying {label.ToLowerInvariant()} with shorter text" : label, ct); }
        catch (GenerationDidNotFinishException)
        {
            ct.ThrowIfCancellationRequested();
            var smaller = SpeechSections.Bisect(section.Text);
            if (depth >= 2 || smaller.Count < 2)
                throw new GenerationDidNotFinishException();
            log.Log($"{label}: did not finish. Retrying as two shorter sections (subdivision {depth + 1} of 2).");
            foreach (var text in smaller)
                await Section(section with { Text = text }, label, depth + 1, ct);
            return;
        }
        Accept(audio, ct);
    }

    private async Task<float[]> Attempt(GenerateRequest request, int limit, string label, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        void Progress(int n) => status(new EngineStatus(EngineState.Generating,
            Detail: $"{label} · {n / 12.5:0.0}s in this attempt · {_samples / (double)SampleRate:0.0}s ready",
            Frames: _frames));
        Progress(0);
        var result = await synthesize(request with
        {
            SectionFrameLimit = limit, KeepRawSamples = true
        }, ct, new InlineProgress(Progress));
        ct.ThrowIfCancellationRequested();
        var raw = result.RawSamples ?? Array.ConvertAll(result.Samples, s => s / (float)short.MaxValue);
        if (result.SampleRate != SampleRate || raw.Length == 0 || raw.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("The model produced invalid section audio. Please generate again.");
        // The actual driver throws before decode on limit exhaustion. Validate
        // the seam too, so another adapter cannot pass a truncated take through.
        if (result.Frames >= limit || raw.Length > limit * 1920)
            throw new GenerationDidNotFinishException();
        _frames += result.Frames;
        return raw;
    }

    private void Accept(float[] audio, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Gentle edges without overlapping words. Existing model pauses remain;
        // the fixed gap is recorded punctuation space, never a buffering wait.
        var fade = Math.Min(120, audio.Length / 2);
        for (var i = 0; i < fade; i++)
        {
            var gain = i / (float)fade;
            audio[i] *= gain;
            audio[^(i + 1)] *= gain;
        }
        if (_accepted.Count > 0) Add(new float[2880]);
        Add(audio);
        void Add(float[] part)
        {
            ct.ThrowIfCancellationRequested();
            _accepted.Add(part);
            _samples += part.Length;
        }
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
