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
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Reading the clip a user picked (spec §4).
/// </summary>
/// <remarks>
/// <para>
/// These run a real decoder over real files. That is the point: the risk here is
/// not arithmetic but whether miniaudio can be driven without opening an output
/// device, and no fake would tell us.
/// </para>
/// <para>
/// WAV only, at several rates and channel counts, because that is what can be
/// written from this repository without an encoder. MP3 and FLAC rest on
/// miniaudio's own support rather than on anything asserted here — worth knowing,
/// since the error message names all three.
/// </para>
/// </remarks>
public sealed class ReferenceAudioTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public ReferenceAudioTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Writes a WAV a test can then decode.</summary>
    private string WriteWav(double hz, int rate, double seconds, int channels = 1)
    {
        var frames = (int)(rate * seconds);
        var samples = new short[frames * channels];

        for (var frame = 0; frame < frames; frame++)
        {
            var value = (short)(0.5 * short.MaxValue * Math.Sin(2 * Math.PI * hz * frame / rate));
            for (var c = 0; c < channels; c++) samples[frame * channels + c] = value;
        }

        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.wav");
        WriteWavFile(path, samples, rate, channels);
        return path;
    }

    /// <summary>
    /// A minimal PCM-16 WAV writer, owned by the tests.
    /// </summary>
    /// <remarks>
    /// Not <see cref="WavWriter" />, which writes 24 kHz mono because §2 says
    /// every generated file is exactly that. Giving production code a channel
    /// count it never needs, so that a test can make a stereo fixture, would be
    /// letting the test dictate the shape of the thing it is testing.
    /// </remarks>
    private static void WriteWavFile(string path, short[] samples, int rate, int channels)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        var dataBytes = samples.Length * sizeof(short);
        var byteRate = rate * channels * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                                   // PCM header size
        writer.Write((short)1);                             // PCM
        writer.Write((short)channels);
        writer.Write(rate);
        writer.Write(byteRate);
        writer.Write((short)(channels * sizeof(short)));    // block align
        writer.Write((short)16);                            // bits per sample

        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples) writer.Write(sample);
    }

    private static double Energy(float[] samples, double hz, int rate)
    {
        var from = samples.Length / 10;
        var to = samples.Length - from;

        double re = 0, im = 0;
        for (var i = from; i < to; i++)
        {
            var phase = 2 * Math.PI * hz * i / rate;
            re += samples[i] * Math.Cos(phase);
            im += samples[i] * Math.Sin(phase);
        }

        var n = to - from;
        return n > 0 ? 2 * Math.Sqrt(re * re + im * im) / n : 0;
    }

    [Fact]
    public void A_clip_decodes_without_an_output_device()
    {
        // The question this file exists to answer. CI runners have no sound
        // card, and neither do plenty of the machines this app will run on — a
        // decoder that quietly needed one would fail there and nowhere else.
        var path = WriteWav(1000, 44_100, 0.25);

        var decoded = ReferenceAudio.Decode(path);

        Assert.NotEmpty(decoded.Samples);
        Assert.Equal(44_100, decoded.SampleRate);
    }

    [Theory]
    [InlineData(48_000)]
    [InlineData(44_100)]
    [InlineData(22_050)]
    [InlineData(16_000)]
    public void The_rate_is_read_from_the_file_rather_than_assumed(int rate)
    {
        var decoded = ReferenceAudio.Decode(WriteWav(440, rate, 0.2));

        Assert.Equal(rate, decoded.SampleRate);
    }

    [Fact]
    public void A_stereo_clip_reports_two_channels()
    {
        var decoded = ReferenceAudio.Decode(WriteWav(440, 44_100, 0.2, channels: 2));

        Assert.Equal(2, decoded.Channels);
    }

    [Fact]
    public void The_duration_survives_the_round_trip()
    {
        var decoded = ReferenceAudio.Decode(WriteWav(440, 44_100, 0.5));

        Assert.Equal(0.5, decoded.Duration.TotalSeconds, 2);
    }

    [Fact]
    public void Loading_gives_mono_at_the_rate_the_model_wants()
    {
        // §4 in one call: decode, downmix, resample.
        var path = WriteWav(1000, 44_100, 0.5, channels: 2);

        var mono = ReferenceAudio.Load(path, Resampler.ModelSampleRate);

        Assert.InRange(mono.Length, 12_000 - 200, 12_000 + 200);
        Assert.InRange(Energy(mono, 1000, 24_000), 0.4, 0.6);
    }

    [Fact]
    public void Loading_a_clip_already_at_24k_changes_nothing_about_it()
    {
        var path = WriteWav(1000, 24_000, 0.5);

        var mono = ReferenceAudio.Load(path, Resampler.ModelSampleRate);

        Assert.InRange(mono.Length, 12_000 - 50, 12_000 + 50);
        Assert.InRange(Energy(mono, 1000, 24_000), 0.4, 0.6);
    }

    [Fact]
    public void Whisper_can_have_the_same_clip_at_its_own_rate()
    {
        // Whisper wants 16 kHz where the model wants 24. One decoder, two
        // targets, so the file is never read twice for two different rates by
        // two different code paths.
        var path = WriteWav(1000, 44_100, 0.5);

        var forWhisper = ReferenceAudio.Load(path, 16_000);

        Assert.InRange(forWhisper.Length, 8_000 - 150, 8_000 + 150);
        Assert.InRange(Energy(forWhisper, 1000, 16_000), 0.4, 0.6);
    }

    [Fact]
    public void A_missing_file_says_so_plainly()
    {
        var error = Assert.Throws<FileNotFoundException>(
            () => ReferenceAudio.Decode(Path.Combine(_root, "nothing.wav")));

        Assert.Contains("no file at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_that_is_not_audio_is_refused_with_advice()
    {
        // §10: actionable. "Could not be read" alone leaves someone stuck.
        var path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "this is not a recording, it is a sentence");

        var error = Assert.Throws<InvalidDataException>(() => ReferenceAudio.Decode(path));

        Assert.Contains("could not be read as audio", error.Message, StringComparison.Ordinal);
        Assert.Contains("try converting", error.Message, StringComparison.Ordinal);

        // And no unexpanded placeholder, which is what an interpolation missing
        // its $ looks like once it reaches a person.
        Assert.DoesNotContain("{", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_path_is_refused_before_anything_is_opened()
    {
        Assert.Throws<ArgumentException>(() => ReferenceAudio.Decode("  "));
    }

    [Fact]
    public void A_clip_of_pure_silence_still_loads()
    {
        // Silence is a poor reference but a real file, and refusing it here
        // would be refusing it for the wrong reason. §4's own guard is the
        // transcript, not the waveform.
        var path = Path.Combine(_root, "silence.wav");
        WriteWavFile(path, new short[24_000], 24_000, 1);

        var mono = ReferenceAudio.Load(path, Resampler.ModelSampleRate);

        Assert.InRange(mono.Length, 24_000 - 50, 24_000 + 50);
        Assert.All(mono, s => Assert.Equal(0f, s, 3));
    }
}
