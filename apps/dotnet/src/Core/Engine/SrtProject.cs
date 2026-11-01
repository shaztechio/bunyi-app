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
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Bunyi.Core.Audio;

namespace Bunyi.Core.Engine;

public sealed record SrtProjectLoad(SrtSession Session, GenerateRequest Request, string? ModelSource);

public sealed partial class SrtSession
{
    private const long MaximumClipBytes = 512L * 1024 * 1024;
    private const long MaximumReferenceBytes = 256L * 1024 * 1024;
    private const long MaximumProjectBytes = 8L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions ProjectJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record ProjectVoice(TtsMode Mode, string Language, string? Speaker, string? Instruct,
        string? ReferenceTranscript, double? ReferenceTranscriptAudioSeconds, string? ModelSource);
    private sealed record ProjectAdjustment(long Samples, double Speed, string Sha256);
    private sealed record ProjectClip(string Id, string Key, long Samples, int Frames, string Sha256, ProjectAdjustment? Adjustment = null);
    private sealed record ProjectManifest(int SchemaVersion, string Normalization, ProjectVoice Voice,
        SrtCue[] Cues, string? Configuration, ProjectClip[] Clips, string? ReferenceEntry, string? ReferenceSha256);

    public static string? SafeModelSource(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        return value;
    }

    /// <summary>Atomically saves a portable draft and all accepted takes, including excluded cues.</summary>
    public Task SaveProjectAsync(string path, GenerateRequest request, string? modelSource = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cues = Cues.ToArray();
        var clips = cues.Select(c => (Cue: c, Clip: Get(c))).Where(c => c.Clip is not null).ToArray();
        var configuration = _configuration;
        var version = Version;
        ValidateDraft(cues);
        ValidateVoice(request.Mode, request.Language, request.ReferenceTranscriptAudioSeconds);
        return Task.Run(async () =>
        {
            var destination = Path.GetFullPath(path);
            if (!destination.EndsWith(".bunyi-srt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Save the project with a .bunyi-srt extension.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".bunyi-project-{Guid.NewGuid():N}.partial");
            try
            {
                using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    var descriptors = new List<ProjectClip>();
                    long total = 0;
                    foreach (var item in clips)
                    {
                        var clip = item.Clip!;
                        await using var input = File.OpenRead(clip.RawPath);
                        if (input.Length != clip.Samples * 4 || input.Length > MaximumClipBytes)
                            throw new InvalidDataException("A cue take is missing or too large to save as a project.");
                        total = checked(total + input.Length);
                        CheckProjectSize(total);
                        string hash;
                        await using (var output = archive.CreateEntry($"clips/{item.Cue.Id}.f32", CompressionLevel.NoCompression).Open())
                            hash = await CopyBounded(input, output, MaximumClipBytes, ct).ConfigureAwait(false);
                        ProjectAdjustment? adjustment = null;
                        if (clip.Adjustment is { } adjusted)
                        {
                            await using var adjustedInput = File.OpenRead(adjusted.RawPath);
                            if (adjustedInput.Length != adjusted.Samples * 4 || adjustedInput.Length > MaximumClipBytes)
                                throw new InvalidDataException("The adjusted cue audio has an invalid length.");
                            total = checked(total + adjustedInput.Length);
                            CheckProjectSize(total);
                            await using var adjustedOutput = archive.CreateEntry($"adjusted/{item.Cue.Id}.f32", CompressionLevel.NoCompression).Open();
                            var adjustedHash = await CopyBounded(adjustedInput, adjustedOutput, MaximumClipBytes, ct).ConfigureAwait(false);
                            adjustment = new ProjectAdjustment(adjusted.Samples, adjusted.Speed, adjustedHash);
                        }
                        descriptors.Add(new(item.Cue.Id, clip.Key, clip.Samples, clip.Frames, hash, adjustment));
                    }
                    string? referenceEntry = null;
                    string? referenceHash = null;
                    if (request.Mode == TtsMode.VoiceClone && !string.IsNullOrWhiteSpace(request.ReferenceAudioPath))
                    {
                        var extension = Path.GetExtension(request.ReferenceAudioPath).ToLowerInvariant();
                        if (extension is not (".wav" or ".mp3" or ".flac")) throw new InvalidDataException("The clone reference must be WAV, MP3 or FLAC.");
                        referenceEntry = "reference" + extension;
                        await using var input = File.OpenRead(request.ReferenceAudioPath);
                        if (input.Length > MaximumReferenceBytes) throw new InvalidDataException("Use a clone reference smaller than 256 MiB to save this project.");
                        total = checked(total + input.Length);
                        CheckProjectSize(total);
                        await using var output = archive.CreateEntry(referenceEntry, CompressionLevel.NoCompression).Open();
                        referenceHash = await CopyBounded(input, output, MaximumReferenceBytes, ct).ConfigureAwait(false);
                    }
                    var voice = new ProjectVoice(request.Mode, request.Language, request.Speaker, request.Instruct,
                        request.ReferenceTranscript, request.ReferenceTranscriptAudioSeconds, SafeModelSource(modelSource));
                    var schema = cues.Any(c => c.AllowSpeedLimitOverride) ? 3 : descriptors.Any(c => c.Adjustment is not null) ? 2 : 1;
                    var manifest = new ProjectManifest(schema, "srt-basic-v1", voice, cues, configuration,
                        descriptors.ToArray(), referenceEntry, referenceHash);
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectJson);
                    if (bytes.Length > SrtDocument.MaximumBytes) throw new InvalidDataException("The project manifest exceeds 16 MiB.");
                    CheckProjectSize(total + bytes.Length);
                    await using var metadata = archive.CreateEntry("project.json", CompressionLevel.Optimal).Open();
                    await metadata.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                if (Version != version) throw new InvalidOperationException("The project changed while saving. Save it again.");
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, ct);
    }

    public static Task<SrtProjectLoad> OpenProjectAsync(string path, CancellationToken ct = default) => Task.Run(async () =>
    {
        SrtSession? session = null;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > SrtDocument.MaximumCues * 2 + 2) throw new InvalidDataException("This project has too many files.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("The project contains duplicate files.");
                total = checked(total + entry.Length);
                CheckProjectSize(total);
            }
            if (!entries.TryGetValue("project.json", out var metadata) || metadata.Length > SrtDocument.MaximumBytes)
                throw new InvalidDataException("The project manifest is missing or exceeds 16 MiB.");
            using var bytes = new MemoryStream();
            await using (var input = metadata.Open()) await CopyBounded(input, bytes, SrtDocument.MaximumBytes, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<ProjectManifest>(bytes.ToArray(), ProjectJson)
                ?? throw new InvalidDataException("The project manifest is empty.");
            if (manifest.SchemaVersion is not (1 or 2 or 3) || manifest.Normalization != "srt-basic-v1")
                throw new InvalidDataException("This project format is not supported by this version of Bunyi.");
            if (manifest.Voice is null || manifest.Cues is null || manifest.Clips is null)
                throw new InvalidDataException("The project manifest is incomplete.");
            ValidateDraft(manifest.Cues);
            if (manifest.SchemaVersion < 3 && manifest.Cues.Any(c => c.AllowSpeedLimitOverride))
                throw new InvalidDataException("This project version does not support per-cue speed overrides.");
            ValidateVoice(manifest.Voice.Mode, manifest.Voice.Language, manifest.Voice.ReferenceTranscriptAudioSeconds);
            if (manifest.Clips.Length > manifest.Cues.Length || manifest.Clips.Any(c => c is null)
                || manifest.Clips.Select(c => c.Id).Distinct().Count() != manifest.Clips.Length
                || manifest.Clips.Length > 0 && string.IsNullOrEmpty(manifest.Configuration))
                throw new InvalidDataException("The project's cue takes are inconsistent.");
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "project.json" };
            var cues = manifest.Cues.ToDictionary(c => c.Id);
            foreach (var clip in manifest.Clips)
            {
                if (!cues.TryGetValue(clip.Id, out var cue) || clip.Key != cue.SpokenText || clip.Samples <= 0
                    || clip.Samples > MaximumClipBytes / 4 || clip.Frames < 0)
                    throw new InvalidDataException("A saved cue take does not match its text or length.");
                var name = $"clips/{clip.Id}.f32";
                if (!entries.TryGetValue(name, out var entry) || entry.Length != clip.Samples * 4)
                    throw new InvalidDataException($"Cue {cue.Number}'s audio is missing or has the wrong length.");
                allowed.Add(name);
                if (clip.Adjustment is { } adjusted)
                {
                    var adjustedName = $"adjusted/{clip.Id}.f32";
                    if (manifest.SchemaVersion < 2 || adjusted.Samples < 128 || adjusted.Samples >= clip.Samples
                        || (!cue.AllowSpeedLimitOverride && clip.Samples * 4 > adjusted.Samples * 5) || !double.IsFinite(adjusted.Speed)
                        || Math.Abs(adjusted.Speed - clip.Samples / (double)adjusted.Samples) > 1e-9
                        || !entries.TryGetValue(adjustedName, out var adjustedEntry) || adjustedEntry.Length != adjusted.Samples * 4)
                        throw new InvalidDataException("The saved speed adjustment is invalid or missing.");
                    allowed.Add(adjustedName);
                }
            }
            if (manifest.ReferenceEntry is { } reference)
            {
                if (manifest.Voice.Mode != TtsMode.VoiceClone || reference is not ("reference.wav" or "reference.mp3" or "reference.flac")
                    || !entries.TryGetValue(reference, out var entry) || entry.Length > MaximumReferenceBytes)
                    throw new InvalidDataException("The clone reference is missing or invalid.");
                allowed.Add(reference);
            }
            if (entries.Keys.Any(name => !allowed.Contains(name))) throw new InvalidDataException("The project contains unexpected files.");
            session = new SrtSession(manifest.Cues);
            Directory.CreateDirectory(session._folder);
            foreach (var clip in manifest.Clips)
            {
                ct.ThrowIfCancellationRequested();
                var rawPath = Path.Combine(session._folder, Guid.NewGuid().ToString("N") + ".f32");
                await ExtractChecked(entries[$"clips/{clip.Id}.f32"], rawPath, MaximumClipBytes, clip.Sha256, ct).ConfigureAwait(false);
                var peak = ValidateRaw(rawPath, clip.Samples, ct);
                var preview = Path.ChangeExtension(rawPath, ".wav");
                WritePreview(rawPath, preview, clip.Samples, peak, ct);
                var restored = new Clip(clip.Key, rawPath, preview, clip.Samples, clip.Frames, peak);
                if (clip.Adjustment is { } adjusted)
                {
                    var adjustedRaw = Path.Combine(session._folder, Guid.NewGuid().ToString("N") + ".f32");
                    await ExtractChecked(entries[$"adjusted/{clip.Id}.f32"], adjustedRaw, MaximumClipBytes, adjusted.Sha256, ct).ConfigureAwait(false);
                    var adjustedPeak = ValidateRaw(adjustedRaw, adjusted.Samples, ct);
                    var adjustedPreview = Path.ChangeExtension(adjustedRaw, ".wav");
                    WritePreview(adjustedRaw, adjustedPreview, adjusted.Samples, adjustedPeak, ct);
                    restored = restored with { Adjustment = new AdjustedClip(adjustedRaw, adjustedPreview, adjusted.Samples, adjustedPeak, adjusted.Speed) };
                }
                session._clips[clip.Id] = restored;
            }
            string? referencePath = null;
            if (manifest.ReferenceEntry is { } referenceName)
            {
                referencePath = Path.Combine(session._folder, referenceName);
                await ExtractChecked(entries[referenceName], referencePath, MaximumReferenceBytes, manifest.ReferenceSha256!, ct).ConfigureAwait(false);
            }
            session._configuration = manifest.Configuration;
            session.Recheck();
            var voice = manifest.Voice;
            var request = new GenerateRequest(voice.Mode, "SRT timed speech", voice.Language, voice.Speaker, voice.Instruct,
                referencePath, voice.ReferenceTranscript, voice.ReferenceTranscriptAudioSeconds) { Srt = session };
            return new SrtProjectLoad(session, request, voice.ModelSource);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            if (ex is JsonException or OverflowException or ArgumentException)
                throw new InvalidDataException("The SRT project is malformed. The current project has not been changed.", ex);
            throw;
        }
    }, ct);

    private static void ValidateDraft(IReadOnlyList<SrtCue> cues)
    {
        if (cues.Count == 0 || cues.Count > SrtDocument.MaximumCues
            || cues.Any(c => c is null || !Guid.TryParseExact(c.Id, "N", out _) || c.Number is null
                || c.OriginalText is null || c.Text is null || c.Start is null || c.End is null)
            || cues.Select(c => c.Id).Distinct().Count() != cues.Count)
            throw new InvalidDataException("The project has invalid cue records.");
    }
    private static void ValidateVoice(TtsMode mode, string language, double? referenceSeconds)
    {
        if (mode is not (TtsMode.PresetVoice or TtsMode.VoiceClone) || !Languages.All.Contains(language)
            || referenceSeconds is { } seconds && (!double.IsFinite(seconds) || seconds <= 0 || seconds > 10))
            throw new InvalidDataException("The project has unsupported voice settings.");
    }
    private static void CheckProjectSize(long bytes)
    {
        if (bytes > MaximumProjectBytes) throw new InvalidDataException("The SRT project exceeds the 8 GiB limit.");
    }
    private static async Task<string> CopyBounded(Stream input, Stream output, long limit, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("A project file exceeds its size limit.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task ExtractChecked(ZipArchiveEntry entry, string destination, long limit, string expectedHash, CancellationToken ct)
    {
        await using var input = entry.Open();
        await using var output = File.Create(destination);
        var hash = await CopyBounded(input, output, limit, ct).ConfigureAwait(false);
        if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase) || output.Length != entry.Length)
            throw new InvalidDataException("A saved audio file is damaged or has a mismatched checksum.");
    }
    private static float ValidateRaw(string path, long samples, CancellationToken ct)
    {
        using var input = new BinaryReader(File.OpenRead(path));
        float peak = 0;
        for (long i = 0; i < samples; i++)
        {
            if ((i & 8191) == 0) ct.ThrowIfCancellationRequested();
            var sample = input.ReadSingle();
            if (!float.IsFinite(sample)) throw new InvalidDataException("A saved cue contains invalid audio samples.");
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }
    private static void WritePreview(string raw, string preview, long samples, float peak, CancellationToken ct)
    {
        using var input = new BinaryReader(File.OpenRead(raw));
        using var output = File.Create(preview);
        WavWriter.WriteTo(output, ReadOnlySpan<short>.Empty);
        var gain = peak > 1 ? 0.98 / peak : 1;
        var buffer = new byte[8192];
        for (long offset = 0; offset < samples;)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length / 2, samples - offset);
            for (var i = 0; i < count; i++)
            {
                var sample = (float)(input.ReadSingle() * gain);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), (short)Math.Round(sample * short.MaxValue));
            }
            output.Write(buffer, 0, count * 2);
            offset += count;
        }
        using var writer = new BinaryWriter(output);
        output.Position = 4; writer.Write((uint)(36 + samples * 2));
        output.Position = 40; writer.Write((uint)(samples * 2));
    }
}
