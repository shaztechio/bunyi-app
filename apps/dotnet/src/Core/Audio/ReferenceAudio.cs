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
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Metadata.Models;
using SoundFlow.Providers;

namespace Bunyi.Core.Audio;

/// <summary>What a clip turned out to contain.</summary>
/// <param name="Samples">Interleaved, one float per sample per channel.</param>
/// <param name="Channels">Channels the file carried.</param>
/// <param name="SampleRate">The rate the file was recorded at.</param>
public sealed record DecodedAudio(float[] Samples, int Channels, int SampleRate)
{
    /// <summary>How long the clip runs.</summary>
    public TimeSpan Duration => SampleRate > 0 && Channels > 0
        ? TimeSpan.FromSeconds((double)Samples.Length / Channels / SampleRate)
        : TimeSpan.Zero;
}

/// <summary>
/// Reads the clip a user picked, at the rate the model needs (spec §4).
/// </summary>
/// <remarks>
/// <para>
/// Decoding goes through miniaudio, which already ships with the playback
/// library — so wav, mp3 and flac cost no new dependency, and the formats behave
/// identically on Windows and Linux. NAudio would have been the obvious
/// alternative and is banned by <c>apps/dotnet/AGENTS.md</c> for being
/// Windows-only.
/// </para>
/// <para>
/// <b>No playback device is opened.</b> The engine and the device are separate
/// steps in this library, and only the engine is needed to decode — which
/// matters, because a machine with no sound card must still be able to load a
/// reference clip. A decoder that quietly required an output device would fail
/// on exactly the headless and server machines where nobody could explain why.
/// </para>
/// </remarks>
public static class ReferenceAudio
{
    /// <summary>
    /// Loads a clip as mono at <paramref name="targetRate" />.
    /// </summary>
    /// <remarks>
    /// The whole of §4's requirement in one call: decode whatever the user
    /// picked, average its channels, and resample to the rate the model asserts.
    /// </remarks>
    public static float[] Load(string path, int targetRate, ILogSink? log = null)
    {
        var decoded = Decode(path);

        log?.Log(
            $"Reference clip: {decoded.Duration.TotalSeconds:0.0}s, "
            + $"{decoded.SampleRate} Hz, {decoded.Channels} channel(s).");

        var mono = Resampler.ToMono(
            decoded.Samples, decoded.Channels, decoded.SampleRate, targetRate);

        if (mono.Length == 0)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} holds no audio. Choose a different recording.");
        }

        return mono;
    }

    /// <summary>Decodes a clip without changing its rate or channels.</summary>
    public static DecodedAudio Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no file at {path}.", path);
        }

        try
        {
            // The engine alone: InitializePlaybackDevice is what opens hardware,
            // and it is deliberately not called here.
            using var engine = new MiniAudioEngine();
            using var provider = new AssetDataProvider(engine, path, new ReadOptions());

            var samples = new float[provider.Length];
            var read = 0;

            while (read < samples.Length)
            {
                var got = provider.ReadBytes(samples.AsSpan(read));
                if (got <= 0) break;
                read += got;
            }

            if (read < samples.Length) Array.Resize(ref samples, read);

            var channels = Math.Max(1, provider.FormatInfo?.ChannelCount ?? 1);
            var rate = provider.SampleRate;

            if (rate <= 0)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} does not say what rate it was recorded at.");
            }

            return new DecodedAudio(samples, channels, rate);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not FileNotFoundException)
        {
            // §10: the sentence a person can act on. The full text goes to the
            // log through the caller.
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} could not be read as audio. "
                + "WAV, MP3 and FLAC all work; try converting it to one of those.", ex);
        }
    }
}
