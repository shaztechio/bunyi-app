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
/// The gated MLP that turns a text token into a talker input (spec §1).
/// </summary>
/// <remarks>
/// Checked against arithmetic small enough to do by hand, because the failure
/// mode is silent: a transpose the wrong way round, or a ReLU where the model
/// wants SiLU, produces a vector of exactly the right shape full of the wrong
/// numbers, and the result is speech that sounds like nothing in particular.
/// </remarks>
public sealed class TextProjectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly List<NpyArray> _open = [];

    public TextProjectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var array in _open) array.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private NpyArray Table(float[] values, int rows, int columns)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.npy");
        var dict = $"{{'descr': '<f4', 'fortran_order': False, 'shape': ({rows}, {columns}), }}";
        var padding = (64 - (10 + dict.Length + 1) % 64) % 64;
        var header = dict + new string(' ', padding) + "\n";

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.Write([0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y']);
            stream.WriteByte(1);
            stream.WriteByte(0);
            stream.Write(BitConverter.GetBytes((ushort)header.Length));
            stream.Write(Encoding.ASCII.GetBytes(header));
            foreach (var value in values) stream.Write(BitConverter.GetBytes(value));
        }

        var array = NpyArray.Open(path);
        _open.Add(array);
        return array;
    }

    private static float Silu(float x) => x * (1f / (1f + MathF.Exp(-x)));

    [Fact]
    public void A_token_goes_through_both_matrices_and_the_gate()
    {
        // One token, width 2, hidden 2, out 2 — small enough to check by hand.
        //
        //   embedding row      = [1, 2]
        //   fc1 = [[1,0],[0,1]], bias [0,0]   -> hidden = [1, 2]
        //   SiLU                              -> [silu(1), silu(2)]
        //   fc2 = [[1,1],[1,-1]], bias [0,1]  -> [h0+h1, h0-h1+1]
        var embedding = Table([9, 9, 1, 2], rows: 2, columns: 2);

        var projection = new TextProjection(
            embedding,
            fc1Weight: [1, 0, 0, 1], fc1Bias: [0, 0],
            fc2Weight: [1, 1, 1, -1], fc2Bias: [0, 1]);

        var result = projection.Project(1);

        var h0 = Silu(1f);
        var h1 = Silu(2f);
        Assert.Equal(h0 + h1, result[0], 5);
        Assert.Equal(h0 - h1 + 1f, result[1], 5);
    }

    [Fact]
    public void The_weights_are_read_as_stored_rather_than_transposed()
    {
        // The trap. With an asymmetric fc1, a transposed read gives a different
        // answer of the same shape. Row-major, one row per output:
        //   fc1 = [[1,2],[3,4]] applied to [1,0] gives [1, 3], not [1, 2].
        var embedding = Table([1, 0], rows: 1, columns: 2);

        var projection = new TextProjection(
            embedding,
            fc1Weight: [1, 2, 3, 4], fc1Bias: [0, 0],
            fc2Weight: [1, 0, 0, 1], fc2Bias: [0, 0]);

        var result = projection.Project(0);

        Assert.Equal(Silu(1f), result[0], 5);
        Assert.Equal(Silu(3f), result[1], 5);
    }

    [Fact]
    public void The_biases_are_added_to_the_right_layers()
    {
        var embedding = Table([0, 0], rows: 1, columns: 2);

        var projection = new TextProjection(
            embedding,
            fc1Weight: [0, 0, 0, 0], fc1Bias: [2, 4],
            fc2Weight: [1, 0, 0, 1], fc2Bias: [10, 20]);

        var result = projection.Project(0);

        // hidden = bias = [2, 4] -> SiLU -> identity fc2 -> plus [10, 20]
        Assert.Equal(Silu(2f) + 10f, result[0], 5);
        Assert.Equal(Silu(4f) + 20f, result[1], 5);
    }

    [Fact]
    public void The_gate_is_SiLU_and_not_ReLU()
    {
        // The distinguishing case: SiLU passes a small negative value through
        // as a small negative number; ReLU flattens it to zero. Both look
        // plausible in the output, and only one is the model's.
        var embedding = Table([-1, 0], rows: 1, columns: 2);

        var projection = new TextProjection(
            embedding,
            fc1Weight: [1, 0, 0, 1], fc1Bias: [0, 0],
            fc2Weight: [1, 0, 0, 1], fc2Bias: [0, 0]);

        var result = projection.Project(0);

        Assert.NotEqual(0f, result[0]);
        Assert.Equal(Silu(-1f), result[0], 5);
        Assert.True(result[0] < 0, "SiLU keeps the sign of a small negative input");
    }

    [Fact]
    public void Several_tokens_project_to_one_row_each()
    {
        var embedding = Table([1, 0, 0, 1], rows: 2, columns: 2);

        var projection = new TextProjection(
            embedding,
            fc1Weight: [1, 0, 0, 1], fc1Bias: [0, 0],
            fc2Weight: [1, 0, 0, 1], fc2Bias: [0, 0]);

        var rows = projection.Project([0, 1, 0]);

        Assert.Equal(3, rows.Length);
        Assert.Equal(rows[0], rows[2]);
        Assert.NotEqual(rows[0], rows[1]);
    }

    [Fact]
    public void A_weight_of_the_wrong_size_is_refused_at_construction()
    {
        // Better than a shape error partway through a generation, which would
        // arrive minutes and gigabytes later.
        var embedding = Table([1, 2], rows: 1, columns: 2);

        var error = Assert.Throws<ArgumentException>(() => new TextProjection(
            embedding,
            fc1Weight: [1, 2, 3], fc1Bias: [0, 0],
            fc2Weight: [1, 0, 0, 1], fc2Bias: [0, 0]));

        Assert.Contains("does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sizes_are_taken_from_the_weights_rather_than_assumed()
    {
        var embedding = Table([1, 2, 3], rows: 1, columns: 3);

        var projection = new TextProjection(
            embedding,
            fc1Weight: new float[4 * 3], fc1Bias: new float[4],
            fc2Weight: new float[2 * 4], fc2Bias: new float[2]);

        Assert.Equal(3, embedding.Columns);
        Assert.Equal(4, projection.HiddenSize);
        Assert.Equal(2, projection.OutputSize);
        Assert.Equal(1, projection.VocabularySize);
    }

    [Fact]
    public void Projecting_into_a_short_destination_is_refused()
    {
        var embedding = Table([1, 2], rows: 1, columns: 2);

        var projection = new TextProjection(
            embedding,
            fc1Weight: [1, 0, 0, 1], fc1Bias: [0, 0],
            fc2Weight: [1, 0, 0, 1], fc2Bias: [0, 0]);

        Assert.Throws<ArgumentException>(() => projection.Project(0, new float[1]));
    }
}
