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

using System.Text.Json;
using System.Text.Json.Serialization;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Infrastructure;

namespace Bunyi.Core;

/// <summary>One saved clone recipe (spec §5).</summary>
/// <param name="FileName">The copied clip, a sibling of <c>voices.json</c>.</param>
public sealed record SavedVoice(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("transcript")] string Transcript,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>
/// Saved voices: a name, a recording and what it says (spec §5).
/// </summary>
/// <remarks>
/// <para>
/// Not a model preset — those are trained speaker tokens. This re-runs the
/// clone path with inputs that were kept, which is why an entry is exactly the
/// three things clone mode asks for.
/// </para>
/// <para>
/// <b>The clip is copied in, not pointed at.</b> A saved voice whose recording
/// lives in the user's Downloads folder stops working the first time they tidy
/// up, and the failure arrives weeks later with no obvious cause.
/// </para>
/// </remarks>
public sealed class VoiceLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ILogSink _log;
    private readonly string _folder;
    private readonly object _gate = new();
    private readonly List<SavedVoice> _voices = [];

    /// <param name="folder">The Voices folder, or the app's own when null.</param>
    public VoiceLibrary(ILogSink log, string? folder = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _folder = folder ?? AppPaths.Voices;
    }

    /// <summary>Where <c>voices.json</c> lives.</summary>
    public string Path => System.IO.Path.Combine(_folder, "voices.json");

    /// <summary>The saved voices, newest first.</summary>
    public IReadOnlyList<SavedVoice> Voices
    {
        get { lock (_gate) return [.. _voices]; }
    }

    /// <summary>The copied clip for an entry.</summary>
    public string ClipPath(SavedVoice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        return System.IO.Path.Combine(_folder, voice.FileName);
    }

    /// <summary>
    /// Reads the library, dropping entries whose audio has gone (spec §5).
    /// </summary>
    /// <remarks>
    /// An entry without its clip cannot be used — it would offer a voice and then
    /// fail at the moment of use. Pruning is written back, so a library that
    /// lost a file heals rather than carrying the gap forever.
    /// </remarks>
    public void Load()
    {
        lock (_gate)
        {
            _voices.Clear();

            if (!File.Exists(Path)) return;

            List<SavedVoice>? read;
            try
            {
                read = JsonSerializer.Deserialize<List<SavedVoice>>(
                    File.ReadAllText(Path), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A library that will not parse is worth saying out loud, but it
                // must not stop the app: clone mode still works without it.
                _log.Log($"Could not read saved voices from {Path}. {ex.Message}");
                return;
            }

            var kept = 0;
            var pruned = 0;

            foreach (var voice in read ?? [])
            {
                if (voice is null || string.IsNullOrWhiteSpace(voice.FileName)) continue;

                if (File.Exists(System.IO.Path.Combine(_folder, voice.FileName)))
                {
                    _voices.Add(voice);
                    kept++;
                }
                else
                {
                    _log.Log($"The recording for the saved voice “{voice.Name}” is missing; removing it.");
                    pruned++;
                }
            }

            Sort();

            if (pruned > 0) Write();
            if (kept > 0) _log.Log($"Loaded {kept} saved voice(s).");
        }
    }

    /// <summary>
    /// Saves a voice, copying its recording into the library (spec §5).
    /// </summary>
    /// <param name="name">What to call it.</param>
    /// <param name="audioPath">The recording the user chose.</param>
    /// <param name="transcript">What that recording says.</param>
    /// <remarks>
    /// <para>
    /// The copy is rewritten as 24 kHz mono, which is what the model takes
    /// anyway, so a library folder is the same on every platform and no entry
    /// depends on a decoder being present later.
    /// </para>
    /// <para>
    /// It is also trimmed to the ten seconds a clone actually uses. The
    /// transcript is stored beside it and describes what was kept: a saved voice
    /// whose words run past its audio makes the model finish the recording
    /// instead of speaking, and that pairing is fixed here once rather than
    /// risked on every use.
    /// </para>
    /// </remarks>
    public SavedVoice Save(string name, string audioPath, string transcript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);

        if (string.IsNullOrWhiteSpace(transcript))
        {
            throw new ArgumentException(
                "A saved voice needs to know what its recording says.", nameof(transcript));
        }

        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException(
                $"That recording is no longer where it was: {System.IO.Path.GetFileName(audioPath)}.",
                audioPath);
        }

        var id = Guid.NewGuid();
        var fileName = $"{id:D}.wav";

        Directory.CreateDirectory(_folder);
        WriteClip(audioPath, System.IO.Path.Combine(_folder, fileName));

        var voice = new SavedVoice(
            id, name.Trim(), fileName, transcript.Trim(), DateTimeOffset.UtcNow);

        lock (_gate)
        {
            _voices.Add(voice);
            Sort();
            Write();
        }

        _log.Log($"Saved the voice “{voice.Name}”.");
        return voice;
    }

    /// <summary>Removes an entry and the clip it copied in (spec §5).</summary>
    public void Delete(SavedVoice voice)
    {
        ArgumentNullException.ThrowIfNull(voice);

        lock (_gate)
        {
            if (_voices.RemoveAll(v => v.Id == voice.Id) == 0) return;
            Write();
        }

        // The clip goes too. Leaving it behind fills the folder with audio
        // nothing refers to, and §5 says delete removes both.
        try
        {
            var clip = ClipPath(voice);
            if (File.Exists(clip)) File.Delete(clip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Log($"Removed the saved voice “{voice.Name}”, but its recording is still on disk. {ex.Message}");
        }

        _log.Log($"Deleted the saved voice “{voice.Name}”.");
    }

    /// <summary>
    /// Writes the copied clip: 24 kHz mono, at most ten seconds.
    /// </summary>
    internal static void WriteClip(string source, string destination)
    {
        var samples = ReferenceAudio.Load(source, MelSpectrogram.SampleRate);
        var used = Math.Min(samples.Length, MaxClipSamples);

        var pcm = new short[used];
        for (var i = 0; i < used; i++)
        {
            pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
        }

        WavWriter.Write(destination, pcm, MelSpectrogram.SampleRate);
    }

    /// <summary>The most audio a clone ever reads, so the most worth keeping.</summary>
    internal const int MaxClipSamples = 10 * MelSpectrogram.SampleRate;

    /// <summary>Newest first: the one just saved is the one being looked for.</summary>
    private void Sort() =>
        _voices.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(_folder);

            // Written beside its destination and renamed over it. Same
            // directory, so the move is atomic within one filesystem — a crash
            // cannot leave malformed JSON that loses every voice rather than
            // the one being changed.
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_voices, Json));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Log($"Could not save the voices library to {Path}. {ex.Message}");
        }
    }
}
