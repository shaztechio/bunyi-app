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
using Bunyi.Core.Platform;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>§2a: "The folder is the record", not an in-app database.</summary>
public sealed class GeneratedOutputsTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public GeneratedOutputsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string WriteClip(string name, OutputMetadata? metadata = null, int samples = 2_400)
    {
        var path = Path.Combine(_folder, name);
        WavWriter.Write(path, new short[samples]);
        if (metadata is not null) WavMetadata.TryWrite(path, metadata);
        return path;
    }

    private static OutputMetadata Meta(string text = "Hello there.", string mode = "Preset voice") => new()
    {
        Mode = mode,
        Text = text,
        Language = "english",
        Speaker = "ryan",
        ModelRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        AppVersion = "0.1.0",
        Created = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void An_empty_folder_lists_nothing()
    {
        Assert.Empty(GeneratedOutputs.Read(_folder));
    }

    [Fact]
    public void A_folder_that_does_not_exist_lists_nothing_rather_than_failing()
    {
        // History is opened before anything has ever been generated.
        Assert.Empty(GeneratedOutputs.Read(Path.Combine(_folder, "nope")));
    }

    [Fact]
    public void Clips_are_listed_newest_first()
    {
        // §2a: "everything generated so far, newest first".
        var older = WriteClip("Preset-voice-20260101T000000.wav", Meta("first"));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-2));
        var newer = WriteClip("Preset-voice-20260102T000000.wav", Meta("second"));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var outputs = GeneratedOutputs.Read(_folder);

        Assert.Equal(2, outputs.Count);
        Assert.Equal("second", outputs[0].Metadata!.Text);
        Assert.Equal("first", outputs[1].Metadata!.Text);
    }

    [Fact]
    public void A_file_deleted_outside_the_app_disappears_from_the_list()
    {
        // The point of reading the folder each time rather than keeping a
        // database: no state to migrate, and nothing to go stale.
        var path = WriteClip("Preset-voice-20260101T000000.wav", Meta());
        Assert.Single(GeneratedOutputs.Read(_folder));

        File.Delete(path);

        Assert.Empty(GeneratedOutputs.Read(_folder));
    }

    [Fact]
    public void Only_wav_files_are_listed()
    {
        WriteClip("Preset-voice-20260101T000000.wav", Meta());
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a clip");

        Assert.Single(GeneratedOutputs.Read(_folder));
    }

    [Fact]
    public void A_zero_length_file_is_not_a_clip()
    {
        // What an interrupted write leaves behind.
        File.WriteAllBytes(Path.Combine(_folder, "Preset-voice-20260101T000000.wav"), []);

        Assert.Empty(GeneratedOutputs.Read(_folder));
    }

    [Fact]
    public void A_row_shows_what_was_said()
    {
        WriteClip("Preset-voice-20260101T000000.wav", Meta("The quick brown fox."));

        var output = Assert.Single(GeneratedOutputs.Read(_folder));

        Assert.Equal("The quick brown fox.", output.Summary());
        Assert.Equal("Preset voice", output.Mode);

        // Shown the way the picker shows it. The file stores the model's
        // identifier, "ryan", and this is the only place it is read for a
        // person — a clip made with "ryan" and one made with "Ryan" are the
        // same voice and should not read as two.
        Assert.Equal("Ryan", output.Voice);
    }

    [Fact]
    public void A_file_with_no_metadata_says_so_rather_than_looking_broken()
    {
        // §2a: it "says so on hover rather than showing a bare filename that
        // reads like a fault". It may have come from another program.
        WriteClip("someone-elses.wav");

        var output = Assert.Single(GeneratedOutputs.Read(_folder));

        Assert.Null(output.Metadata);
        Assert.Equal("someone-elses.wav", output.Summary());
        Assert.Contains("does not carry any details", output.Details());
        Assert.Contains("another program", output.Details());
    }

    [Fact]
    public void The_details_carry_the_whole_record()
    {
        // §2a: hover shows text, mode, language, voice, style or reference
        // transcript, and the model.
        WriteClip("Preset-voice-20260101T000000.wav",
            Meta() with { Style = "cheerful", Text = "Speak this." });

        var details = Assert.Single(GeneratedOutputs.Read(_folder)).Details();

        Assert.Contains("Text: Speak this.", details);
        Assert.Contains("Mode: Preset voice", details);
        Assert.Contains("Language: english", details);
        Assert.Contains("Speaker: ryan", details);
        Assert.Contains("Style: cheerful", details);
        Assert.Contains("Model: elbruno/", details);
        Assert.Contains("Size:", details);
    }

    [Fact]
    public void The_details_omit_fields_the_mode_never_had()
    {
        // A blank line for a field this mode does not use reads as missing data.
        WriteClip("Preset-voice-20260101T000000.wav", Meta());

        var details = Assert.Single(GeneratedOutputs.Read(_folder)).Details();

        Assert.DoesNotContain("Style:", details);
        Assert.DoesNotContain("Voice:", details);
        Assert.DoesNotContain("Reference transcript:", details);
    }

    [Fact]
    public void A_long_prompt_is_summarised_in_the_row_but_whole_in_the_details()
    {
        var long_ = new string('a', 300);
        WriteClip("Preset-voice-20260101T000000.wav", Meta(long_));

        var output = Assert.Single(GeneratedOutputs.Read(_folder));

        Assert.True(output.Summary().Length <= 60);
        Assert.Contains(long_, output.Details());
    }
}

/// <summary>§2a: Trash, not an unrecoverable delete.</summary>
[Collection("environment")]
public sealed class TrashTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();

    public TrashTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_trashed_file_leaves_its_original_location()
    {
        var path = Path.Combine(_folder, "clip.wav");
        File.WriteAllBytes(path, new byte[1024]);

        var trashed = Trash.TryMoveToTrash(path, _log);

        Assert.True(trashed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Trashing_something_that_is_not_there_reports_rather_than_throws()
    {
        var missing = Path.Combine(_folder, "gone.wav");

        Assert.False(Trash.TryMoveToTrash(missing, _log));
        Assert.Contains(_log.Lines, l => l.Contains("not there"));
    }

    [Fact]
    public void On_Linux_the_file_is_recoverable_through_the_freedesktop_trash()
    {
        // The whole point: a file manager must be able to offer "restore",
        // which needs the .trashinfo recording where it came from. A plain
        // move would not be recoverable, and File.Delete would not be either.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;

        var home = Path.Combine(_folder, "home");
        Directory.CreateDirectory(home);
        var original = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);

        try
        {
            var path = Path.Combine(_folder, "clip.wav");
            File.WriteAllBytes(path, new byte[1024]);

            Assert.True(Trash.TryMoveToTrash(path, _log));

            var files = Path.Combine(home, "Trash", "files", "clip.wav");
            var info = Path.Combine(home, "Trash", "info", "clip.wav.trashinfo");
            Assert.True(File.Exists(files), "the file itself moves into Trash/files");
            Assert.True(File.Exists(info), "and an info entry records where it came from");

            var text = File.ReadAllText(info);
            Assert.Contains("[Trash Info]", text);
            Assert.Contains($"Path={path}", text);
            Assert.Contains("DeletionDate=", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", original);
        }
    }

    [Fact]
    public void On_Linux_a_second_file_of_the_same_name_does_not_overwrite_the_first()
    {
        // Trashing two clips with the same name must not lose one of them.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;

        var home = Path.Combine(_folder, "home");
        Directory.CreateDirectory(home);
        var original = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", home);

        try
        {
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var path = Path.Combine(_folder, "clip.wav");
                File.WriteAllBytes(path, new byte[1024]);
                Assert.True(Trash.TryMoveToTrash(path, _log));
            }

            Assert.Equal(2, Directory.GetFiles(Path.Combine(home, "Trash", "files")).Length);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(home, "Trash", "info")).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", original);
        }
    }

    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }
        public void Log(string message) { lock (_lines) _lines.Add(message); }
    }
}
