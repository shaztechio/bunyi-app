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
using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Reading the voice-design export's embedding tables (spec §1, design mode).
/// </summary>
/// <remarks>
/// The failure this guards against does not throw. A header misread by a few
/// bytes, or a dtype taken on trust, yields an array of the right shape full of
/// the wrong numbers — and wrong embeddings do not crash, they speak nonsense.
/// </remarks>
public sealed class NpyArrayTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public NpyArrayTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Writes a .npy exactly as NumPy does, for the reader to read.</summary>
    private string Write(
        float[] values, int[] shape, string dtype = "<f4",
        bool fortran = false, byte major = 1)
        => NpyFile.Write(_root, values, shape, dtype, fortran, major);

    [Fact]
    public void A_two_dimensional_array_knows_its_shape()
    {
        var path = Write([1, 2, 3, 4, 5, 6], [3, 2]);

        using var array = NpyArray.Open(path);

        Assert.Equal([3, 2], array.Shape);
        Assert.Equal(3, array.Rows);
        Assert.Equal(2, array.Columns);
    }

    [Fact]
    public void Rows_come_back_in_the_order_they_were_written()
    {
        // C order, not Fortran: the second row is values 3 and 4, not 2 and 5.
        var path = Write([1, 2, 3, 4, 5, 6], [3, 2]);

        using var array = NpyArray.Open(path);

        Assert.Equal([1f, 2f], array.Row(0));
        Assert.Equal([3f, 4f], array.Row(1));
        Assert.Equal([5f, 6f], array.Row(2));
    }

    [Fact]
    public void A_one_dimensional_array_is_a_single_row()
    {
        // The biases are 1-D, and treating one as a column of rows would give
        // a projection the wrong shape.
        var path = Write([7, 8, 9], [3]);

        using var array = NpyArray.Open(path);

        Assert.Equal(1, array.Rows);
        Assert.Equal(3, array.Columns);
        Assert.Equal([7f, 8f, 9f], array.Row(0));
    }

    [Fact]
    public void The_whole_array_flattens_in_row_order()
    {
        var path = Write([1, 2, 3, 4, 5, 6], [3, 2]);

        using var array = NpyArray.Open(path);

        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f], array.ToArray());
    }

    [Fact]
    public void Version_two_headers_are_read_too()
    {
        // Larger headers use a four-byte length. Getting the prefix wrong shifts
        // every value by two bytes, which reads as noise rather than an error.
        var path = Write([1, 2, 3, 4], [2, 2], major: 2);

        using var array = NpyArray.Open(path);

        Assert.Equal([3f, 4f], array.Row(1));
    }

    [Fact]
    public void A_dtype_that_is_not_float32_is_refused()
    {
        // The dangerous case: float64 read as float32 gives plausible garbage.
        var path = Write([1, 2], [2], dtype: "<f8");

        var error = Assert.Throws<InvalidDataException>(() => NpyArray.Open(path));
        Assert.Contains("float32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Big_endian_data_is_refused()
    {
        var path = Write([1, 2], [2], dtype: ">f4");

        Assert.Throws<InvalidDataException>(() => NpyArray.Open(path));
    }

    [Fact]
    public void Fortran_order_is_refused_rather_than_read_backwards()
    {
        var path = Write([1, 2, 3, 4], [2, 2], fortran: true);

        Assert.Throws<InvalidDataException>(() => NpyArray.Open(path));
    }

    [Fact]
    public void A_file_that_is_not_a_npy_at_all_says_so()
    {
        var path = Path.Combine(_root, "not.npy");
        File.WriteAllText(path, "this is not a NumPy file, it is a sentence");

        var error = Assert.Throws<InvalidDataException>(() => NpyArray.Open(path));
        Assert.Contains("not a .npy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_a_row_that_is_not_there_stops_rather_than_guessing()
    {
        // Row indices are token ids. One outside the table means the tokenizer
        // and the embeddings disagree, which is worth stopping for.
        var path = Write([1, 2, 3, 4], [2, 2]);

        using var array = NpyArray.Open(path);

        Assert.Throws<ArgumentOutOfRangeException>(() => array.Row(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => array.Row(-1));
    }

    [Fact]
    public void A_destination_too_small_is_refused()
    {
        var path = Write([1, 2, 3, 4], [2, 2]);

        using var array = NpyArray.Open(path);

        Assert.Throws<ArgumentException>(() => array.CopyRow(0, new float[1]));
    }

    [Fact]
    public void The_file_is_not_read_into_memory_to_open_it()
    {
        // The point of mapping: text_embedding.npy is 1.24 GB and a generation
        // touches a few dozen of its 151,936 rows. The Python reference loads
        // the whole table; this must not.
        var rows = 20_000;
        var columns = 64;
        var values = new float[rows * columns];
        for (var i = 0; i < values.Length; i++) values[i] = i;

        var path = Write(values, [rows, columns]);

        // Per-thread, not process-wide: xunit runs test classes in parallel, so
        // a process-wide counter measures whatever else happened to be running.
        // Mapped pages are not managed allocations at all, which is exactly the
        // distinction being asserted.
        var before = GC.GetAllocatedBytesForCurrentThread();

        using var array = NpyArray.Open(path);
        array.Row(19_999);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var fileSize = new FileInfo(path).Length;

        Assert.True(allocated < fileSize / 4,
            $"opening allocated {allocated} bytes for a {fileSize} byte file");
    }
}
