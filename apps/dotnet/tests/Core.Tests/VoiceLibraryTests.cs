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
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The saved voices library (spec §5, DATA-FORMATS).
/// </summary>
/// <remarks>
/// The file format is a contract: both apps read it, so a folder copied from a
/// Mac has to work here. Most of what follows is about the file rather than the
/// object holding it.
/// </remarks>
public sealed class VoiceLibraryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();

    public VoiceLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- Saving ----

    [Fact]
    public void A_saved_voice_keeps_the_three_things_a_clone_needs()
    {
        var library = New();

        var voice = library.Save("Eric", Clip(), "He shoots, he scores.");

        Assert.Equal("Eric", voice.Name);
        Assert.Equal("He shoots, he scores.", voice.Transcript);
        Assert.True(File.Exists(library.ClipPath(voice)));
    }

    [Fact]
    public void The_recording_is_copied_in_rather_than_pointed_at()
    {
        // §5. A voice whose clip lives in Downloads stops working the first
        // time the user tidies up, and the failure arrives weeks later.
        var library = New();
        var original = Clip();

        var voice = library.Save("Eric", original, "Some words.");
        File.Delete(original);

        library.Load();

        Assert.Single(library.Voices);
        Assert.True(File.Exists(library.ClipPath(library.Voices[0])));
    }

    [Fact]
    public void The_copy_is_the_rate_the_model_wants()
    {
        // 24 kHz mono whatever was handed in, so a library folder is the same
        // on every platform and no entry depends on a decoder later.
        var library = New();

        var voice = library.Save("Eric", Clip(rate: 44_100, seconds: 2), "Some words.");
        var decoded = ReferenceAudio.Decode(library.ClipPath(voice));

        Assert.Equal(24_000, decoded.SampleRate);
        Assert.Equal(1, decoded.Channels);
    }

    [Fact]
    public void The_copy_is_trimmed_to_what_a_clone_actually_reads()
    {
        // The saved transcript describes the saved clip. Keeping audio the
        // clone will never look at is how the two drift apart — and a
        // transcript running past its audio makes the model finish the
        // recording instead of speaking.
        var library = New();

        var voice = library.Save("Eric", Clip(seconds: 30), "Some words.");
        var decoded = ReferenceAudio.Decode(library.ClipPath(voice));

        Assert.InRange(decoded.Samples.Length, VoiceLibrary.MaxClipSamples - 2400,
            VoiceLibrary.MaxClipSamples);
    }

    [Fact]
    public void A_shorter_recording_is_kept_whole()
    {
        var library = New();

        var voice = library.Save("Eric", Clip(seconds: 3), "Some words.");
        var decoded = ReferenceAudio.Decode(library.ClipPath(voice));

        Assert.InRange(decoded.Samples.Length, 70_000, 74_000);
    }

    [Fact]
    public void A_voice_without_a_transcript_is_refused()
    {
        // §4 makes it effectively mandatory, and a saved voice that cannot be
        // used is worse than one that was never saved.
        var library = New();

        var error = Assert.Throws<ArgumentException>(
            () => library.Save("Eric", Clip(), "   "));

        Assert.Contains("what its recording says", error.Message);
    }

    [Fact]
    public void A_recording_that_has_gone_is_named()
    {
        var library = New();

        Assert.Throws<FileNotFoundException>(
            () => library.Save("Eric", Path.Combine(_root, "missing.wav"), "Some words."));
    }

    [Fact]
    public void Names_and_transcripts_are_trimmed()
    {
        var library = New();

        var voice = library.Save("  Eric  ", Clip(), "  Some words.  ");

        Assert.Equal("Eric", voice.Name);
        Assert.Equal("Some words.", voice.Transcript);
    }

    // ---- The file (DATA-FORMATS) ----

    [Fact]
    public void The_file_has_the_keys_the_other_app_reads()
    {
        // Both apps read this. The key names are the contract, not the storage.
        var library = New();
        library.Save("Eric", Clip(), "Some words.");

        using var document = JsonDocument.Parse(File.ReadAllText(library.Path));
        var entry = document.RootElement.EnumerateArray().Single();

        foreach (var key in new[] { "id", "name", "fileName", "transcript", "createdAt" })
        {
            Assert.True(entry.TryGetProperty(key, out _), $"the file is missing {key}");
        }
    }

    [Fact]
    public void The_clip_is_named_after_the_entry_and_sits_beside_the_file()
    {
        var library = New();

        var voice = library.Save("Eric", Clip(), "Some words.");

        Assert.Equal($"{voice.Id:D}.wav", voice.FileName);
        Assert.Equal(
            Path.GetDirectoryName(library.Path),
            Path.GetDirectoryName(library.ClipPath(voice)));
    }

    [Fact]
    public void A_library_written_by_the_other_app_is_read()
    {
        // Hand-written to the schema rather than round-tripped through this
        // code, which would only prove it agrees with itself.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "voices.json"), """
            [
              {
                "id": "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                "name": "Eric",
                "fileName": "clip.wav",
                "transcript": "He shoots, he scores.",
                "createdAt": "2026-01-02T03:04:05Z"
              }
            ]
            """);

        WavWriter.Write(Path.Combine(_root, "clip.wav"), new short[2_400], 24_000);

        var library = New();
        library.Load();

        var voice = Assert.Single(library.Voices);
        Assert.Equal("Eric", voice.Name);
        Assert.Equal("He shoots, he scores.", voice.Transcript);
        Assert.Equal("clip.wav", voice.FileName);
    }

    // ---- Loading and pruning ----

    [Fact]
    public void Entries_whose_audio_has_gone_are_pruned()
    {
        // §5. An entry without its clip would offer a voice and then fail at
        // the moment of use.
        var library = New();
        var kept = library.Save("Kept", Clip(), "Some words.");
        var lost = library.Save("Lost", Clip(), "Other words.");

        File.Delete(library.ClipPath(lost));
        library.Load();

        var remaining = Assert.Single(library.Voices);
        Assert.Equal(kept.Id, remaining.Id);
    }

    [Fact]
    public void Pruning_is_written_back_so_the_gap_does_not_persist()
    {
        var library = New();
        library.Save("Kept", Clip(), "Some words.");
        var lost = library.Save("Lost", Clip(), "Other words.");

        File.Delete(library.ClipPath(lost));
        library.Load();

        // A second library reading the same folder sees the repair.
        var another = New();
        another.Load();

        Assert.Single(another.Voices);
        Assert.DoesNotContain("Lost", File.ReadAllText(library.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_that_will_not_parse_does_not_stop_the_app()
    {
        // Clone mode still works without saved voices. Losing them is worth
        // saying out loud; it is not worth refusing to start over.
        File.WriteAllText(Path.Combine(_root, "voices.json"), "{ this is not json");

        var library = New();
        library.Load();

        Assert.Empty(library.Voices);
        Assert.Contains(_log.Lines, l => l.Contains("Could not read saved voices", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_folder_is_simply_empty()
    {
        var library = New();
        library.Load();

        Assert.Empty(library.Voices);
    }

    [Fact]
    public void The_newest_voice_comes_first()
    {
        // The one just saved is the one being looked for.
        var library = New();
        library.Save("First", Clip(), "Some words.");
        library.Save("Second", Clip(), "Other words.");

        Assert.Equal("Second", library.Voices[0].Name);
    }

    // ---- Deleting ----

    [Fact]
    public void Deleting_removes_the_entry_and_its_recording()
    {
        // §5 says both. A clip left behind is audio nothing refers to.
        var library = New();
        var voice = library.Save("Eric", Clip(), "Some words.");
        var clip = library.ClipPath(voice);

        library.Delete(voice);

        Assert.Empty(library.Voices);
        Assert.False(File.Exists(clip));
    }

    [Fact]
    public void Deleting_leaves_the_other_voices_alone()
    {
        var library = New();
        var kept = library.Save("Kept", Clip(), "Some words.");
        var going = library.Save("Going", Clip(), "Other words.");

        library.Delete(going);

        Assert.Equal(kept.Id, Assert.Single(library.Voices).Id);
        Assert.True(File.Exists(library.ClipPath(kept)));
    }

    [Fact]
    public void Deleting_something_already_gone_does_nothing()
    {
        var library = New();
        var voice = library.Save("Eric", Clip(), "Some words.");

        library.Delete(voice);
        library.Delete(voice);

        Assert.Empty(library.Voices);
    }

    [Fact]
    public void What_was_saved_survives_a_restart()
    {
        var library = New();
        library.Save("Eric", Clip(), "He shoots, he scores.");

        var reopened = New();
        reopened.Load();

        var voice = Assert.Single(reopened.Voices);
        Assert.Equal("Eric", voice.Name);
        Assert.Equal("He shoots, he scores.", voice.Transcript);
    }

    // ---- Fixtures ----

    private VoiceLibrary New() => new(_log, _root);

    /// <summary>A real, readable WAV — nothing here fakes the decoder.</summary>
    private string Clip(int rate = 24_000, int seconds = 1)
    {
        var path = Path.Combine(_root, $"source-{Guid.NewGuid():N}.wav");
        var count = rate * seconds;
        var pcm = new short[count];

        for (var i = 0; i < count; i++) pcm[i] = (short)(Math.Sin(i * 0.05) * 8000);

        WavWriter.Write(path, pcm, rate);
        return path;
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Lines { get; } = [];

        public void Log(string message) => Lines.Add(message);
    }
}
