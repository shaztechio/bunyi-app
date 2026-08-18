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
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Bunyi.Core.Design;

/// <summary>
/// The header of a NumPy <c>.npy</c> file.
/// </summary>
/// <param name="Shape">The dimensions, outermost first.</param>
/// <param name="DataOffset">Where the values start.</param>
public sealed record NpyHeader(IReadOnlyList<int> Shape, int DataOffset)
{
    /// <summary>Rows, for a 2-D array; 1 for a 1-D one.</summary>
    public int Rows => Shape.Count >= 2 ? Shape[0] : 1;

    /// <summary>Values per row.</summary>
    public int Columns => Shape.Count >= 2 ? Shape[^1] : (Shape.Count == 1 ? Shape[0] : 0);

    /// <summary>Total number of values.</summary>
    public long Count => Shape.Count == 0 ? 0 : Shape.Aggregate(1L, (a, d) => a * d);
}

/// <summary>
/// A float32 <c>.npy</c> array on disk, read a row at a time.
/// </summary>
/// <remarks>
/// <para>
/// The voice-design export keeps its embeddings as NumPy files, and the largest
/// is <c>text_embedding.npy</c> at 1.24 GB — a vocabulary of 151,936 rows, of
/// which one generation touches a few dozen. The Python reference
/// <c>np.load</c>s the whole thing; this maps it instead and copies out the rows
/// asked for, so the cost is the rows used rather than the table's size.
/// </para>
/// <para>
/// Only <c>&lt;f4</c> — little-endian float32, C order — is accepted. Every array
/// in the export is that, and a reader that quietly mis-reads a different dtype
/// would produce numbers rather than an error, which is the worst outcome here:
/// wrong embeddings do not crash, they speak nonsense.
/// </para>
/// </remarks>
public sealed class NpyArray : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    /// <summary>The shape, outermost dimension first.</summary>
    public IReadOnlyList<int> Shape { get; }

    /// <summary>Rows, for a 2-D array; 1 for a 1-D one.</summary>
    public int Rows { get; }

    /// <summary>Values per row.</summary>
    public int Columns { get; }

    private readonly long _dataOffset;

    private NpyArray(MemoryMappedFile file, MemoryMappedViewAccessor view, NpyHeader header)
    {
        _file = file;
        _view = view;
        _dataOffset = header.DataOffset;
        Shape = header.Shape;
        Rows = header.Rows;
        Columns = header.Columns;
    }

    /// <summary>Maps a <c>.npy</c> file without reading its values.</summary>
    public static NpyArray Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = ReadHeader(stream, path);

        var file = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);

        try
        {
            var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new NpyArray(file, view, header);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Copies one row into <paramref name="destination" />.</summary>
    /// <remarks>
    /// The row index is a token id in every use here, and a token id outside the
    /// table means the tokenizer and the embeddings disagree — a mismatch worth
    /// stopping for rather than clamping into a plausible-looking row.
    /// </remarks>
    public void CopyRow(int row, Span<float> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, Rows);

        if (destination.Length < Columns)
        {
            throw new ArgumentException(
                $"Row {row} needs {Columns} values, given {destination.Length}.",
                nameof(destination));
        }

        var start = _dataOffset + (long)row * Columns * sizeof(float);
        for (var i = 0; i < Columns; i++)
        {
            destination[i] = _view.ReadSingle(start + (long)i * sizeof(float));
        }
    }

    /// <summary>One row, as a new array.</summary>
    public float[] Row(int row)
    {
        var values = new float[Columns];
        CopyRow(row, values);
        return values;
    }

    /// <summary>
    /// The whole array, flattened.
    /// </summary>
    /// <remarks>
    /// For the small arrays — the projection weights, the codec tables — where
    /// every value is used on every generation and mapping row by row would cost
    /// more than it saves.
    /// </remarks>
    public float[] ToArray()
    {
        var total = checked((int)(Rows * (long)Columns));
        var values = new float[total];

        for (var row = 0; row < Rows; row++)
        {
            CopyRow(row, values.AsSpan(row * Columns, Columns));
        }

        return values;
    }

    /// <summary>
    /// The six bytes every <c>.npy</c> file starts with.
    /// </summary>
    /// <remarks>
    /// Spelled out as bytes rather than as the obvious UTF-8 literal, which is
    /// a trap: U+0093 is not ASCII, so UTF-8 encodes it as <b>two</b> bytes and
    /// the literal is seven bytes long, not six. The format wants the raw byte.
    /// </remarks>
    private static ReadOnlySpan<byte> Magic =>
        [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

    /// <summary>Reads and validates the header.</summary>
    internal static NpyHeader ReadHeader(Stream stream, string path)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);

        if (!magic[..6].SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path} is not a .npy file.");
        }

        var major = magic[6];

        // v1 stores the header length in two bytes, v2 and v3 in four.
        int headerLength;
        int prefix;
        if (major == 1)
        {
            Span<byte> two = stackalloc byte[2];
            stream.ReadExactly(two);
            headerLength = BinaryPrimitives.ReadUInt16LittleEndian(two);
            prefix = 10;
        }
        else if (major is 2 or 3)
        {
            Span<byte> four = stackalloc byte[4];
            stream.ReadExactly(four);
            headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(four));
            prefix = 12;
        }
        else
        {
            throw new InvalidDataException($"{path} uses .npy version {major}, which is not supported.");
        }

        var text = new byte[headerLength];
        stream.ReadExactly(text);
        var dict = Encoding.ASCII.GetString(text);

        if (!dict.Contains("'descr': '<f4'", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{path} is not little-endian float32. Every array in the export is; "
                + "reading a different type would produce numbers rather than an error.");
        }

        if (dict.Contains("'fortran_order': True", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{path} is in Fortran order, which is not supported.");
        }

        return new NpyHeader(ParseShape(dict, path), prefix + headerLength);
    }

    private static List<int> ParseShape(string dict, string path)
    {
        const string key = "'shape': (";
        var start = dict.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) throw new InvalidDataException($"{path} has no shape in its header.");

        start += key.Length;
        var end = dict.IndexOf(')', start);
        if (end < 0) throw new InvalidDataException($"{path} has a malformed shape.");

        var shape = new List<int>();
        foreach (var part in dict[start..end].Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0) continue;   // the trailing comma of a 1-tuple

            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidDataException($"{path} has a malformed shape: '{part}'.");
            }

            shape.Add(value);
        }

        return shape;
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}
