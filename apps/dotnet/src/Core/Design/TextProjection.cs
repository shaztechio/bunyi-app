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

using System.Numerics.Tensors;

namespace Bunyi.Core.Design;

/// <summary>
/// The gated MLP that turns a text token into something the talker takes.
/// </summary>
/// <remarks>
/// <para>
/// From the export's own reference script: look the token up in the embedding
/// table, put it through <c>fc1</c>, apply SiLU, then <c>fc2</c>. Both weights
/// are stored transposed, as PyTorch keeps them, so each output is a dot product
/// with a <b>row</b> of the weight rather than a column.
/// </para>
/// <para>
/// Written out rather than delegated to a matrix library because it is two
/// matrix-vector products on a handful of tokens per generation — the model's
/// own graphs do the work that needs a library — and because getting the
/// transpose backwards is silent: it produces a vector of the right shape full
/// of the wrong numbers, and the result is speech that sounds like nothing in
/// particular rather than an error.
/// </para>
/// </remarks>
public sealed class TextProjection
{
    private readonly NpyArray _embedding;
    private readonly float[] _fc1Weight;
    private readonly float[] _fc1Bias;
    private readonly float[] _fc2Weight;
    private readonly float[] _fc2Bias;

    /// <summary>Values in a projected vector.</summary>
    public int OutputSize => _fc2Bias.Length;

    /// <summary>Values in the hidden layer between the two matrices.</summary>
    public int HiddenSize => _fc1Bias.Length;

    /// <summary>Rows in the embedding table.</summary>
    public int VocabularySize => _embedding.Rows;

    public TextProjection(
        NpyArray embedding,
        float[] fc1Weight, float[] fc1Bias,
        float[] fc2Weight, float[] fc2Bias)
    {
        _embedding = embedding ?? throw new ArgumentNullException(nameof(embedding));
        _fc1Weight = fc1Weight ?? throw new ArgumentNullException(nameof(fc1Weight));
        _fc1Bias = fc1Bias ?? throw new ArgumentNullException(nameof(fc1Bias));
        _fc2Weight = fc2Weight ?? throw new ArgumentNullException(nameof(fc2Weight));
        _fc2Bias = fc2Bias ?? throw new ArgumentNullException(nameof(fc2Bias));

        // Checked here rather than discovered as a shape error deep in a run:
        // fc1 maps the embedding width to the hidden width, fc2 back out.
        Expect(_fc1Weight.Length, (long)_fc1Bias.Length * embedding.Columns, "fc1");
        Expect(_fc2Weight.Length, (long)_fc2Bias.Length * _fc1Bias.Length, "fc2");
    }

    private static void Expect(long actual, long wanted, string what)
    {
        if (actual != wanted)
        {
            throw new ArgumentException(
                $"The {what} weight has {actual} values; the embedding and bias sizes imply {wanted}. "
                + "The embeddings folder does not match this model.");
        }
    }

    /// <summary>Projects one token.</summary>
    public float[] Project(int tokenId)
    {
        var output = new float[OutputSize];
        Project(tokenId, output);
        return output;
    }

    /// <summary>Projects one token into <paramref name="destination" />.</summary>
    public void Project(int tokenId, Span<float> destination)
    {
        if (destination.Length < OutputSize)
        {
            throw new ArgumentException(
                $"A projection needs {OutputSize} values, given {destination.Length}.",
                nameof(destination));
        }

        var embedded = _embedding.Row(tokenId);

        var hidden = new float[HiddenSize];
        Multiply(_fc1Weight, _fc1Bias, embedded, hidden);

        // SiLU: x * sigmoid(x). The gate the export was trained with; a plain
        // ReLU here would be silently wrong in the same way a bad transpose is.
        for (var i = 0; i < hidden.Length; i++)
        {
            hidden[i] *= 1f / (1f + MathF.Exp(-hidden[i]));
        }

        Multiply(_fc2Weight, _fc2Bias, hidden, destination);
    }

    /// <summary>Projects a run of tokens, one row per token.</summary>
    public float[][] Project(IReadOnlyList<int> tokenIds)
    {
        ArgumentNullException.ThrowIfNull(tokenIds);

        var rows = new float[tokenIds.Count][];
        for (var i = 0; i < tokenIds.Count; i++) rows[i] = Project(tokenIds[i]);
        return rows;
    }

    /// <summary>
    /// <c>output = weight . input + bias</c>, with weight stored row-major and
    /// transposed.
    /// </summary>
    private static void Multiply(
        ReadOnlySpan<float> weight, ReadOnlySpan<float> bias,
        ReadOnlySpan<float> input, Span<float> output)
    {
        for (var row = 0; row < bias.Length; row++)
        {
            var slice = weight.Slice(row * input.Length, input.Length);
            output[row] = TensorPrimitives.Dot(slice, input) + bias[row];
        }
    }
}
