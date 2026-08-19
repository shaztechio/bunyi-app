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

using System.Text;

namespace Bunyi.Core.Tests;

/// <summary>
/// Writes a .npy exactly as NumPy does, for tests that need one.
/// </summary>
/// <remarks>
/// Shared rather than copied: the reader's tests need the odd variants — version
/// 2 headers, Fortran order, other dtypes — while tests that merely need an
/// embedding table to exist need none of that. Two writers would drift, and the
/// one place they must not disagree is the format itself.
/// </remarks>
internal static class NpyFile
{
    public static string Write(
        string directory,
        float[] values,
        int[] shape,
        string dtype = "<f4",
        bool fortran = false,
        byte major = 1)
    {
        Directory.CreateDirectory(directory);
        return WriteTo(Path.Combine(directory, $"{Guid.NewGuid():N}.npy"),
            values, shape, dtype, fortran, major);
    }

    /// <summary>Writes to a path the caller chose, for tables loaded by name.</summary>
    public static string WriteTo(
        string path,
        float[] values,
        int[] shape,
        string dtype = "<f4",
        bool fortran = false,
        byte major = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var shapeText = shape.Length == 1
            ? $"({shape[0]},)"
            : "(" + string.Join(", ", shape) + ")";

        var dict = $"{{'descr': '{dtype}', 'fortran_order': {(fortran ? "True" : "False")}, "
                   + $"'shape': {shapeText}, }}";

        // NumPy pads the header so the values start on a 64-byte boundary.
        var prefix = major == 1 ? 10 : 12;
        var unpadded = prefix + dict.Length + 1;
        var padding = (64 - (unpadded % 64)) % 64;
        var header = dict + new string(' ', padding) + "\n";

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);

        // Raw 0x93, not a UTF-8 literal: that would be seven bytes, because
        // UTF-8 encodes U+0093 as two.
        stream.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y']);
        stream.WriteByte(major);
        stream.WriteByte(0);

        if (major == 1)
        {
            stream.Write(BitConverter.GetBytes((ushort)header.Length));
        }
        else
        {
            stream.Write(BitConverter.GetBytes((uint)header.Length));
        }

        stream.Write(Encoding.ASCII.GetBytes(header));

        foreach (var value in values) stream.Write(BitConverter.GetBytes(value));
        return path;
    }
}
