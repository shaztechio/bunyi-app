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
using System.Globalization;

namespace Bunyi.Core.Audio;

/// <summary>Writes the 24 kHz mono PCM WAV files spec §2 describes.</summary>
public static class WavWriter
{
    /// <summary>The rate every generated file uses (spec §2).</summary>
    public const int SampleRate = 24_000;

    /// <summary>Mono (spec §2).</summary>
    public const int Channels = 1;

    /// <summary>16-bit PCM.</summary>
    public const int BitsPerSample = 16;

    /// <summary>Writes 16-bit samples as a PCM WAV.</summary>
    public static void Write(string path, ReadOnlySpan<short> samples, int sampleRate = SampleRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        WriteTo(file, samples, sampleRate);
    }

    /// <summary>Writes a WAV to any stream.</summary>
    public static void WriteTo(Stream stream, ReadOnlySpan<short> samples, int sampleRate = SampleRate)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var dataBytes = samples.Length * sizeof(short);
        var byteRate = sampleRate * Channels * (BitsPerSample / 8);
        var blockAlign = Channels * (BitsPerSample / 8);

        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);          // PCM header size
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);           // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], BitsPerSample);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)dataBytes);
        stream.Write(header);

        var buffer = new byte[dataBytes];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), samples[i]);
        }
        stream.Write(buffer);
    }

    /// <summary>
    /// The filename for a generated clip:
    /// <c>&lt;Mode&gt;-&lt;ISO8601-basic&gt;.wav</c>.
    /// </summary>
    /// <remarks>
    /// Pinned by /spec/DATA-FORMATS.md, down to the mode's spaces becoming
    /// hyphens: <c>Voice-clone-20260725T2312.wav</c>. The mode names come from
    /// <see cref="TtsModeExtensions.DisplayName"/>, which is the same string
    /// the settings keys and the embedded metadata use.
    /// </remarks>
    public static string FileNameFor(TtsMode mode, DateTimeOffset when) =>
        $"{mode.DisplayName().Replace(' ', '-')}-" +
        $"{when.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}.wav";
}
