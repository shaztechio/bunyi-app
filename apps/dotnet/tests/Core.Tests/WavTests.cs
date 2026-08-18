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
using System.Text;
using System.Text.Json;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class WavWriterTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public WavWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static short[] Tone(int samples)
    {
        var data = new short[samples];
        for (var i = 0; i < samples; i++)
            data[i] = (short)(Math.Sin(i * 2 * Math.PI * 440 / WavWriter.SampleRate) * 12_000);
        return data;
    }

    [Fact]
    public void A_written_file_is_24_kHz_mono_16_bit_as_the_spec_requires()
    {
        var path = Path.Combine(_folder, "out.wav");
        WavWriter.Write(path, Tone(2_400));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));      // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));      // mono
        Assert.Equal(24_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
    }

    [Fact]
    public void The_declared_sizes_match_the_file_that_was_written()
    {
        // A header that disagrees with the body is how a player reports a clip
        // as a different length than it is, or refuses it outright.
        var path = Path.Combine(_folder, "out.wav");
        WavWriter.Write(path, Tone(1_000));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(2_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.Equal(44 + 2_000, bytes.Length);
    }

    [Fact]
    public void Samples_survive_the_round_trip_unchanged()
    {
        var path = Path.Combine(_folder, "out.wav");
        var samples = Tone(500);
        WavWriter.Write(path, samples);

        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i], BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + i * 2)));
        }
    }

    [Fact]
    public void The_folder_is_created_if_it_is_not_there()
    {
        var path = Path.Combine(_folder, "nested", "deeper", "out.wav");

        WavWriter.Write(path, Tone(10));

        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(TtsMode.PresetVoice, "Preset-voice-20260725T231200.wav")]
    [InlineData(TtsMode.VoiceDesign, "Voice-design-20260725T231200.wav")]
    [InlineData(TtsMode.VoiceClone, "Voice-clone-20260725T231200.wav")]
    public void The_filename_follows_the_pinned_format(TtsMode mode, string expected)
    {
        // DATA-FORMATS gives the shape and an example, down to the mode's space
        // becoming a hyphen.
        var when = new DateTimeOffset(2026, 7, 25, 23, 12, 0, TimeSpan.Zero);

        Assert.Equal(expected, WavWriter.FileNameFor(mode, when));
    }
}

public sealed class WavMetadataTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public WavMetadataTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static OutputMetadata Preset(string text = "Hello there.") => new()
    {
        Mode = TtsMode.PresetVoice.DisplayName(),
        Text = text,
        Language = "english",
        Speaker = "ryan",
        Style = "cheerful and quick",
        ModelRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        AppVersion = "0.1.0",
        Created = new DateTimeOffset(2026, 7, 25, 23, 12, 0, 456, TimeSpan.Zero),
    };

    private string WriteWav(string name = "out.wav")
    {
        var path = Path.Combine(_folder, name);
        WavWriter.Write(path, new short[1_000]);
        return path;
    }

    [Fact]
    public void Metadata_round_trips_through_the_file()
    {
        var path = WriteWav();
        var written = Preset();

        Assert.True(WavMetadata.TryWrite(path, written));
        var read = WavMetadata.TryRead(path);

        Assert.NotNull(read);
        Assert.Equal(written.Mode, read.Mode);
        Assert.Equal(written.Text, read.Text);
        Assert.Equal(written.Language, read.Language);
        Assert.Equal(written.Speaker, read.Speaker);
        Assert.Equal(written.Style, read.Style);
        Assert.Equal(written.ModelRepo, read.ModelRepo);
        Assert.Equal(written.Created.ToUnixTimeMilliseconds(), read.Created.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void The_audio_bytes_are_untouched_by_tagging()
    {
        // The chunk is appended. Rewriting the audio to tag it would risk the
        // one thing that must not be lost.
        var path = WriteWav();
        var before = File.ReadAllBytes(path);

        WavMetadata.TryWrite(path, Preset());

        var after = File.ReadAllBytes(path);
        Assert.True(after.Length > before.Length);
        Assert.Equal(before.AsSpan(44).ToArray(), after.AsSpan(44, before.Length - 44).ToArray());
    }

    [Fact]
    public void The_riff_size_is_updated_so_the_new_chunk_is_visible()
    {
        // Appending without updating the RIFF size leaves a file whose declared
        // length stops before the new data, which readers honour — the chunk
        // would be invisible to every tool including ours.
        var path = WriteWav();
        WavMetadata.TryWrite(path, Preset());

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    }

    [Fact]
    public void A_file_with_no_metadata_reads_as_null_rather_than_failing()
    {
        // Not an error: it may predate tagging, or come from another tool.
        // History says so on hover instead of showing a bare filename.
        Assert.Null(WavMetadata.TryRead(WriteWav()));
    }

    [Fact]
    public void Standard_fields_are_written_so_ordinary_tools_show_something()
    {
        var path = WriteWav();
        WavMetadata.TryWrite(path, Preset());

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.Contains("INAM", text);
        Assert.Contains("IART", text);
        Assert.Contains("ISFT", text);
        Assert.Contains("Bunyi 0.1.0", text);
        Assert.Contains("IGNR", text);
        Assert.Contains("Speech", text);
    }

    [Fact]
    public void Empty_values_are_omitted_rather_than_stored_blank()
    {
        var path = WriteWav();
        var metadata = Preset() with { Style = null, Speaker = "ryan" };

        WavMetadata.TryWrite(path, metadata);

        var read = WavMetadata.TryRead(path)!;
        Assert.Null(read.Style);
        Assert.Null(read.VoiceDescription);
        Assert.Null(read.ReferenceTranscript);
    }

    [Fact]
    public void The_voice_fields_stay_separate_so_a_style_is_never_read_as_a_voice()
    {
        // DATA-FORMATS is explicit: the macOS UI reuses one text field for the
        // preset-voice STYLE and the voice-design DESCRIPTION, so a single key
        // would leave a reader unable to tell a delivery instruction from a
        // voice.
        var path = WriteWav();
        var design = new OutputMetadata
        {
            Mode = TtsMode.VoiceDesign.DisplayName(),
            Text = "Once upon a time.",
            Language = "english",
            VoiceDescription = "Warm documentary narrator, unhurried",
            ModelRepo = "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX",
            AppVersion = "0.1.0",
            Created = DateTimeOffset.UtcNow,
        };

        WavMetadata.TryWrite(path, design);
        var read = WavMetadata.TryRead(path)!;

        Assert.Equal("Warm documentary narrator, unhurried", read.VoiceDescription);
        Assert.Null(read.Style);
        Assert.Null(read.Speaker);
    }

    [Fact]
    public void The_timestamp_keeps_its_milliseconds()
    {
        var path = WriteWav();
        var created = new DateTimeOffset(2026, 7, 25, 23, 12, 0, 456, TimeSpan.Zero);

        WavMetadata.TryWrite(path, Preset() with { Created = created });

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        Assert.Contains("2026-07-25T23:12:00.456", text);
    }

    [Fact]
    public void A_long_prompt_is_truncated_for_the_title_but_kept_whole_in_the_record()
    {
        var long_ = new string('a', 200);
        var metadata = Preset(long_);

        Assert.Equal(60, metadata.Title().Length);
        Assert.EndsWith("…", metadata.Title());

        var path = WriteWav();
        WavMetadata.TryWrite(path, metadata);
        Assert.Equal(long_, WavMetadata.TryRead(path)!.Text);
    }

    [Fact]
    public void The_title_is_the_first_line_of_a_multi_line_prompt()
    {
        Assert.Equal("First line.", Preset("First line.\nSecond line.").Title());
    }

    [Fact]
    public void A_clone_is_identified_by_its_reference_transcript()
    {
        var clone = Preset() with
        {
            Speaker = null,
            Style = null,
            ReferenceTranscript = "He shoots, he scores",
        };

        Assert.Equal("Clone of “He shoots, he scores”", clone.VoiceSummary());
    }

    [Fact]
    public void Non_ascii_text_survives_as_utf8()
    {
        var path = WriteWav();
        var metadata = Preset("你好，世界 — ñ, é, 日本語");

        WavMetadata.TryWrite(path, metadata);

        Assert.Equal("你好，世界 — ñ, é, 日本語", WavMetadata.TryRead(path)!.Text);
    }

    [Fact]
    public void An_odd_length_field_stays_word_aligned_so_later_chunks_still_parse()
    {
        // RIFF chunks are word-aligned and the pad byte is not counted in the
        // size. Ignoring it walks into the middle of the next chunk, and
        // everything after reads as garbage.
        var path = WriteWav();
        var metadata = Preset("odd");   // 3 bytes + NUL terminator behaviour

        Assert.True(WavMetadata.TryWrite(path, metadata));
        Assert.Equal("odd", WavMetadata.TryRead(path)!.Text);
    }

    [Fact]
    public void The_comment_field_carries_the_whole_record_as_json()
    {
        // There is no standard four-character code for "the prompt", and
        // private ones would be readable by nothing.
        var path = WriteWav();
        WavMetadata.TryWrite(path, Preset());

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        var start = text.IndexOf('{', text.IndexOf("ICMT", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(text[start..(text.LastIndexOf('}') + 1)]);

        Assert.Equal("Preset voice", document.RootElement.GetProperty("mode").GetString());
        Assert.Equal("english", document.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public void Tagging_a_file_that_is_not_a_wav_fails_quietly()
    {
        // Best-effort: a failed tag write must never cost the audio.
        var path = Path.Combine(_folder, "not.wav");
        File.WriteAllText(path, "x");

        Assert.False(WavMetadata.TryWrite(path, Preset()));
    }
}
