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
using System.Text;
using System.Text.Json;

namespace Bunyi.Core.Audio;

/// <summary>
/// Reads and writes a RIFF <c>LIST</c>/<c>INFO</c> chunk in a WAV file
/// (/spec/DATA-FORMATS.md, "Output WAV").
/// </summary>
/// <remarks>
/// <para>
/// WAV is RIFF, so tagging is a standard chunk rather than a sidecar file:
/// ffprobe and most editors already show it. The individual INFO fields are
/// what those tools display; the whole record additionally goes into
/// <c>ICMT</c> as JSON, because there is no standard four-character code for
/// "the prompt" and inventing private ones would be readable by nothing.
/// </para>
/// <para>
/// The chunk is <b>appended</b>, leaving the audio bytes untouched.
/// </para>
/// </remarks>
public static class WavMetadata
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>ISO 8601 with milliseconds, as the spec requires.</summary>
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";

    /// <summary>
    /// Appends the metadata to a WAV file, returning whether it worked.
    /// </summary>
    /// <remarks>
    /// <b>Best-effort by design.</b> A file that plays without its metadata
    /// beats losing the audio to a failed tag write, so a failure here is
    /// reported and swallowed rather than thrown — the audio is already on
    /// disk and correct by the time this runs.
    /// </remarks>
    public static bool TryWrite(string path, OutputMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(metadata);

        try
        {
            var chunk = BuildListChunk(metadata);

            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (file.Length < 12) return false;

            file.Seek(0, SeekOrigin.End);
            file.Write(chunk);

            // RIFF's size field counts everything after it, so appending a
            // chunk without updating it leaves a file whose declared length
            // stops before the new data — which readers honour, making the
            // chunk invisible.
            var riffSize = (uint)(file.Length - 8);
            file.Seek(4, SeekOrigin.Begin);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, riffSize);
            file.Write(size);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the metadata back, or null when the file carries none.
    /// </summary>
    /// <remarks>
    /// History uses this to label each row. A file with no metadata is not an
    /// error — it may have been produced by another tool, or by a version
    /// before tagging — so it returns null and the caller says so, rather than
    /// showing a bare filename that reads like a fault.
    /// </remarks>
    public static OutputMetadata? TryRead(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var json = FindComment(bytes);
            if (json is null) return null;

            return JsonSerializer.Deserialize<OutputMetadata>(json, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Walks the RIFF chunks looking for LIST/INFO's ICMT.</summary>
    private static string? FindComment(byte[] bytes)
    {
        if (bytes.Length < 12) return null;
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF") return null;
        if (Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE") return null;

        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            var body = position + 8;
            if (body + size > bytes.Length) break;

            if (id == "LIST" && size >= 4
                && Encoding.ASCII.GetString(bytes, body, 4) == "INFO")
            {
                var found = FindInfoField(bytes, body + 4, (int)(body + size), "ICMT");
                if (found is not null) return found;
            }

            // Chunks are word-aligned: an odd size is followed by a pad byte
            // that is not counted in the size. Ignoring it walks into the
            // middle of the next chunk and the rest of the file reads as
            // garbage.
            position = body + (int)size + ((size % 2 == 1) ? 1 : 0);
        }

        return null;
    }

    private static string? FindInfoField(byte[] bytes, int start, int end, string wanted)
    {
        var position = start;
        while (position + 8 <= end)
        {
            var id = Encoding.ASCII.GetString(bytes, position, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            var body = position + 8;
            if (body + size > end) break;

            if (id == wanted)
            {
                var text = Encoding.UTF8.GetString(bytes, body, size);
                return text.TrimEnd('\0');
            }

            position = body + size + ((size % 2 == 1) ? 1 : 0);
        }

        return null;
    }

    /// <summary>Builds the LIST/INFO chunk for a record.</summary>
    internal static byte[] BuildListChunk(OutputMetadata metadata)
    {
        var fields = new List<(string Id, string Value)>();

        void Add(string id, string? value)
        {
            // Empty values are omitted rather than stored blank: a reader
            // cannot tell a blank field from a field that means "blank".
            if (!string.IsNullOrEmpty(value)) fields.Add((id, value));
        }

        Add("INAM", metadata.Title());
        Add("IART", metadata.VoiceSummary());
        Add("ISFT", $"Bunyi {metadata.AppVersion}");
        Add("ICRD", metadata.Created.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        Add("IGNR", "Speech");
        Add("ICMT", JsonSerializer.Serialize(metadata, Json));

        using var body = new MemoryStream();
        body.Write("INFO"u8);

        // Hoisted: one buffer reused, rather than a stack allocation per field.
        Span<byte> size = stackalloc byte[4];

        foreach (var (id, value) in fields)
        {
            // NUL-terminated, as RIFF INFO expects.
            var text = Encoding.UTF8.GetBytes(value + '\0');
            body.Write(Encoding.ASCII.GetBytes(id));

            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)text.Length);
            body.Write(size);
            body.Write(text);

            if (text.Length % 2 == 1) body.WriteByte(0);   // word alignment
        }

        var payload = body.ToArray();

        using var chunk = new MemoryStream();
        chunk.Write("LIST"u8);
        Span<byte> chunkSize = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkSize, (uint)payload.Length);
        chunk.Write(chunkSize);
        chunk.Write(payload);

        return chunk.ToArray();
    }
}
