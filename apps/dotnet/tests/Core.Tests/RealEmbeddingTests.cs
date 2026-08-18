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

using Bunyi.Core.Design;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The reader, against the real voice-design export.
/// </summary>
/// <remarks>
/// <para>
/// The tests that write their own files prove the reader is self-consistent.
/// These prove it agrees with NumPy, which is the only thing that matters —
/// the export was written by NumPy and will be read by us, and a reader that
/// is confidently wrong about the real file passes every synthetic test.
/// </para>
/// <para>
/// Skipped unless the export is present, because it is 5.85 GB and CI has no
/// business downloading it. The expected values were taken from the file itself
/// by an independent reader, so they are ground truth rather than this code's
/// own output blessed as correct.
/// </para>
/// </remarks>
public sealed class RealEmbeddingTests
{
    /// <summary>Where the export sits, or null when it is not on this machine.</summary>
    /// <remarks>
    /// An environment variable so a developer can point at their own copy; the
    /// default is where the memory measurements put it.
    /// </remarks>
    private static string? Embeddings
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("BUNYI_DESIGN_MODEL")
                ?? @"C:\bs\dm\models\models\wavekat\Qwen3-TTS-1.7B-VoiceDesign-ONNX";

            var folder = Path.Combine(root, "embeddings");
            return File.Exists(Path.Combine(folder, "text_embedding.npy")) ? folder : null;
        }
    }

    /// <summary>Opens one array, skipping the test when the export is absent.</summary>
    private static NpyArray Open(string name)
    {
        Skip.If(Embeddings is null,
            "The 5.85 GB voice-design export is not on this machine. "
            + "Set BUNYI_DESIGN_MODEL to its folder to run these.");

        return NpyArray.Open(Path.Combine(Embeddings!, $"{name}.npy"));
    }

    [SkippableFact]
    public void The_text_embedding_table_is_the_shape_the_config_implies()
    {
        using var array = Open("text_embedding");

        // 151,936 rows is the tokenizer's vocabulary; 2048 is talker_hidden_size
        // for this export. Both are checked because a transposed read would give
        // the same total and the wrong everything.
        Assert.Equal([151_936, 2048], array.Shape);
    }

    [SkippableTheory]
    // Ground truth, read out of the file by an independent reader.
    [InlineData(0, -0.00592041015625f, -0.00933837890625f)]
    [InlineData(1, -0.0050048828125f, -0.006744384765625f)]
    [InlineData(12_345, 0.0101318359375f, -0.00182342529296875f)]
    [InlineData(151_935, -0.0027008056640625f, 0.0025482177734375f)]
    public void Rows_match_what_NumPy_reads(int row, float first, float second)
    {
        using var array = Open("text_embedding");

        var values = array.Row(row);

        // Exact, not approximate: these are the file's bytes reinterpreted, and
        // nothing in between should be rounding them.
        Assert.Equal(first, values[0]);
        Assert.Equal(second, values[1]);
    }

    [SkippableFact]
    public void The_last_row_is_reachable()
    {
        // 151,935 x 2048 x 4 bytes is 1.24 GB, past the point where a 32-bit
        // offset would have wrapped. The row before the end is the one that
        // proves the arithmetic is 64-bit throughout.
        using var array = Open("text_embedding");

        var values = array.Row(array.Rows - 1);

        Assert.Equal(2048, values.Length);
        Assert.Contains(values, v => v != 0);
    }

    [SkippableFact]
    public void The_projection_weights_are_square_and_the_biases_match_them()
    {
        using var fc1 = Open("text_projection_fc1_weight");
        using var fc1Bias = Open("text_projection_fc1_bias");
        using var fc2 = Open("text_projection_fc2_weight");
        using var fc2Bias = Open("text_projection_fc2_bias");

        Assert.Equal([2048, 2048], fc1.Shape);
        Assert.Equal([2048, 2048], fc2.Shape);
        Assert.Equal(2048, fc1Bias.Columns);
        Assert.Equal(2048, fc2Bias.Columns);
    }

    [SkippableFact]
    public void The_codec_tables_are_the_sizes_the_config_gives()
    {
        using var talker = Open("talker_codec_embedding");
        using var group0 = Open("cp_codec_embedding_0");

        // talker_vocab_size 3072, cp_vocab_size 2048, both at hidden width.
        Assert.Equal([3072, 2048], talker.Shape);
        Assert.Equal([2048, 2048], group0.Shape);
    }

    [SkippableFact]
    public void A_projection_over_the_real_tables_produces_finite_numbers()
    {
        // Not a correctness proof — that needs the golden WAV, which is M8's
        // next step. This catches the transpose being wrong in the way that
        // overflows, which is the failure that at least announces itself.
        using var embedding = Open("text_embedding");
        using var fc1W = Open("text_projection_fc1_weight");
        using var fc1B = Open("text_projection_fc1_bias");
        using var fc2W = Open("text_projection_fc2_weight");
        using var fc2B = Open("text_projection_fc2_bias");

        var projection = new TextProjection(
            embedding, fc1W.ToArray(), fc1B.ToArray(), fc2W.ToArray(), fc2B.ToArray());

        var projected = projection.Project(12_345);

        Assert.Equal(2048, projected.Length);
        Assert.All(projected, v => Assert.True(float.IsFinite(v), $"{v} is not finite"));
        Assert.Contains(projected, v => v != 0);
    }
}
