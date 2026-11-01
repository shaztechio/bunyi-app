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

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Qwen;

namespace Bunyi.Core.Engine;

/// <summary>Session-local disk cache. Never a History entry or durable project.</summary>
public sealed class SrtSession : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bunyi-srt-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, Clip> _clips = new();
    private long _revision;
    private string? _configuration;
    private bool _disposed;
    public IReadOnlyList<SrtCue> Cues { get; private set; }
    public IReadOnlyList<SrtDiagnostic> Diagnostics { get; private set; } = [];
    public bool HasClips => _clips.Count > 0;
    public event EventHandler? Changed;
    internal sealed record Clip(string Key, string RawPath, string PreviewPath, long Samples, int Frames, float Peak);
    internal Clip? Get(SrtCue cue) => _clips.TryGetValue(cue.Id, out var clip) && clip.Key == cue.SpokenText ? clip : null;
    public string? PreviewPath(string id) => _clips.TryGetValue(id, out var clip) ? clip.PreviewPath : null;

    public SrtSession(IReadOnlyList<SrtCue> cues) { Cues = Array.AsReadOnly(cues.ToArray()); Recheck(); }
    public void Update(IReadOnlyList<SrtCue> cues)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Increment(ref _revision);
        Cues = Array.AsReadOnly(cues.ToArray());
        var current = Cues.ToDictionary(c => c.Id);
        foreach (var id in _clips.Keys.ToArray())
            if (!current.TryGetValue(id, out var cue) || _clips[id].Key != cue.SpokenText) RemoveClip(id);
        Recheck();
    }
    public void Invalidate(string id)
    {
        Interlocked.Increment(ref _revision);
        RemoveClip(id);
        Recheck();
    }
    private void RemoveClip(string id)
    {
        if (_clips.TryRemove(id, out var old)) { File.Delete(old.RawPath); File.Delete(old.PreviewPath); }
    }
    public void InvalidateAll()
    {
        Interlocked.Increment(ref _revision);
        foreach (var id in _clips.Keys.ToArray()) RemoveClip(id);
        _configuration = null;
        Recheck();
    }
    internal void Recheck()
    {
        Diagnostics = SrtDocument.Check(Cues, id => _clips.TryGetValue(id, out var clip) ? clip.Samples : null);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal async Task GenerateAsync(GenerateRequest request, string source,
        Func<GenerateRequest, CancellationToken, IProgress<int>, Task<SynthesisResult>> synthesize,
        Action<EngineStatus> status, Action release, ILogSink log, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        var cues = Cues.ToArray();
        SrtDocument.Validate(cues);
        if (request.Mode == TtsMode.VoiceDesign) throw new InvalidDataException("SRT supports Preset voice and Voice clone only.");
        var referenceHash = "";
        if (request.Mode == TtsMode.VoiceClone && request.ReferenceAudioPath is { } reference)
        {
            await using var file = File.OpenRead(reference);
            referenceHash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        }
        var configuration = JsonSerializer.Serialize(new { request.Mode, request.Language, request.Speaker,
            request.Instruct, request.ReferenceAudioPath, request.ReferenceTranscript,
            request.ReferenceTranscriptAudioSeconds, source, referenceHash });
        if (_configuration != configuration) { InvalidateAll(); _configuration = configuration; }
        var revision = Interlocked.Read(ref _revision);
        Directory.CreateDirectory(_folder);
        var included = cues.Where(c => c.Included && (request.SrtCueId is null || c.Id == request.SrtCueId)).ToArray();
        for (var i = 0; i < included.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var cue = included[i];
            if (Get(cue) is not null) continue;
            var prefix = $"Cue {cue.Number} ({i + 1} of {included.Length})";
            var frames = new CallbackProgress(n => status(new EngineStatus(EngineState.Generating,
                Detail: $"{prefix} · {OnnxTtsEngine.FramesSoFar(n)}", Frames: n)));
            frames.Report(0);
            var cueRequest = request with { Srt = null, Text = cue.SpokenText, KeepRawSamples = true };
            try
            {
                var audio = SpeechDurationEstimate.RequiresSections(cue.SpokenText, request.Language)
                    ? await new LongTextGeneration(synthesize, s => status(s with { Detail = prefix + " · " + s.Detail }), log)
                        .GenerateAsync(cueRequest, ct).ConfigureAwait(false)
                    : await synthesize(cueRequest, ct, frames).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _revision) != revision)
                    throw new OperationCanceledException("The cue script changed during generation. Generate again.");
                var raw = audio.RawSamples ?? Array.ConvertAll(audio.Samples, x => x / 32768f);
                if (audio.SampleRate != 24000 || raw.Length == 0 || raw.Any(x => !float.IsFinite(x)))
                    throw new InvalidDataException($"Cue {cue.Number} produced invalid audio. Generate it again.");
                var fade = Math.Min(120, raw.Length / 2);
                for (var n = 0; n < fade; n++) { raw[n] *= n / (float)fade; raw[^(n + 1)] *= n / (float)fade; }
                var temporaryId = Guid.NewGuid().ToString("N");
                var rawPath = Path.Combine(_folder, temporaryId + ".f32");
                var preview = Path.Combine(_folder, temporaryId + ".wav");
                try
                {
                    using (var writer = new BinaryWriter(File.Create(rawPath))) foreach (var sample in raw) writer.Write(sample);
                    WavWriter.Write(preview, DesignSpeechSynthesizer.ToPcm16(raw));
                    ct.ThrowIfCancellationRequested();
                    if (Interlocked.Read(ref _revision) != revision)
                        throw new OperationCanceledException("The cue script changed during generation. Generate again.");
                    _clips[cue.Id] = new Clip(cue.SpokenText, rawPath, preview, raw.LongLength, audio.Frames, raw.Max(x => Math.Abs(x)));
                }
                catch { File.Delete(rawPath); File.Delete(preview); throw; }
                Recheck();
            }
            finally { release(); }
        }
        Recheck();
        if (Diagnostics.Any(d => d.Status == "error" || d.SampleCount is null)) throw new SrtTimingException(Diagnostics);
    }

    internal (long Samples, int Frames) WriteTrack(string path, ILogSink log, CancellationToken ct)
    {
        SrtDocument.Validate(Cues);
        Recheck();
        var cues = Cues.Where(c => c.Included).ToArray();
        if (Diagnostics.Any(d => d.Status == "error") || cues.Any(c => Get(c) is null)) throw new SrtTimingException(Diagnostics);
        var clips = cues.Select(c => Get(c)!).ToArray();
        SrtDocument.TrySamples(cues[^1].Start, out var lastStart);
        SrtDocument.TrySamples(cues[^1].End, out var lastEnd);
        var total = Math.Max(lastEnd, checked(lastStart + clips[^1].Samples));
        // Leave space for standard RIFF metadata; WAV has a 32-bit length field.
        if (total > (uint.MaxValue - 1024 * 1024) / 2) throw new InvalidDataException("The timeline exceeds the WAV size limit. Use a shorter SRT file.");
        var peak = clips.Max(c => c.Peak);
        var gain = peak > 1 ? 0.98 / peak : 1;
        if (gain < 1) log.Log($"SRT output level reduced uniformly by {-20 * Math.Log10(gain):F1} dB to prevent clipping.");
        using var file = File.Create(path);
        WavWriter.WriteTo(file, ReadOnlySpan<short>.Empty);
        var buffer = new byte[8192];
        long position = 0;
        for (var i = 0; i < cues.Length; i++)
        {
            SrtDocument.TrySamples(cues[i].Start, out var start);
            Silence(start - position);
            using var reader = new BinaryReader(File.OpenRead(clips[i].RawPath));
            for (long offset = 0; offset < clips[i].Samples;)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length / 2, clips[i].Samples - offset);
                for (var n = 0; n < count; n++)
                {
                    var sample = reader.ReadSingle();
                    if (!float.IsFinite(sample)) throw new InvalidDataException("Invalid cached cue audio. Regenerate the cue.");
                    BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(n * 2), (short)Math.Round(sample * gain * short.MaxValue));
                }
                file.Write(buffer, 0, count * 2);
                offset += count;
            }
            position = start + clips[i].Samples;
        }
        Silence(total - position);
        file.Position = 4;
        using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((uint)(36 + total * 2));
            file.Position = 40;
            writer.Write((uint)(total * 2));
        }
        return (total, clips.Sum(c => c.Frames));

        void Silence(long samples)
        {
            Array.Clear(buffer);
            for (long remaining = samples * 2; remaining > 0;)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, remaining);
                file.Write(buffer, 0, count);
                remaining -= count;
            }
        }
    }

    public static string SidecarPath(string wav) => Path.ChangeExtension(wav, ".srt.json");
    internal void WriteSidecar(string path, OutputMetadata metadata)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, sourceKind = "srt", normalization = "srt-basic-v1", metadata,
            cues = Cues.Select(c => new { c.Id, c.Number, c.OriginalText, c.Text, c.SpokenText,
                c.Start, c.End, c.Included, sampleCount = Get(c)?.Samples,
                diagnostic = Diagnostics.FirstOrDefault(d => d.Id == c.Id) })
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        if (json.Length > SrtDocument.MaximumBytes) throw new InvalidDataException("The timeline metadata exceeds 16 MiB.");
        File.WriteAllBytes(path, json);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _revision);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        _clips.Clear();
    }
    private sealed class CallbackProgress(Action<int> action) : IProgress<int> { public void Report(int value) => action(value); }
}

public sealed class SrtTimingException(IReadOnlyList<SrtDiagnostic> diagnostics)
    : InvalidOperationException("The track is not ready to export. Generate missing cues and resolve timing errors by editing text/timestamps, excluding cues, or regenerating.")
{
    public IReadOnlyList<SrtDiagnostic> Diagnostics { get; } = diagnostics;
}
