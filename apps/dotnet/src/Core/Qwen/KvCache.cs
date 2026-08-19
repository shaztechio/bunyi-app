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

namespace Bunyi.Core.Qwen;

/// <summary>
/// Joins prefill's per-layer cache into the stacked one decode expects.
/// </summary>
/// <remarks>
/// <para>
/// The exports have an asymmetry: <c>talker_prefill</c> returns the cache as
/// <b>56 separate tensors</b> — <c>present_key_0</c> through
/// <c>present_value_27</c>, each <c>[1, kv_heads, seq, head_dim]</c> — while
/// <c>talker_decode</c> takes two, stacked as
/// <c>[layers, 1, kv_heads, seq, head_dim]</c>. Something has to join them, and
/// both exports are the same in this, so it is done once here.
/// </para>
/// <para>
/// The trap is layer order. Sorting the output names as text gives 0, 1, 10,
/// 11, … 19, 2, 20 — an order that is wrong in a way nothing detects: every
/// tensor is the right shape, the run completes, and the attention reads layer
/// 10's memory where layer 2's belongs. The result is audio, just not the right
/// audio. So layers are addressed by number, and a missing one is an error.
/// </para>
/// </remarks>
public static class KvCache
{
    /// <summary>The name prefill gives one layer's keys.</summary>
    public static string KeyName(int layer) => $"present_key_{layer}";

    /// <summary>The name prefill gives one layer's values.</summary>
    public static string ValueName(int layer) => $"present_value_{layer}";

    /// <summary>
    /// Stacks per-layer tensors into one, in layer order.
    /// </summary>
    /// <param name="layer">Returns one layer's values, or null when absent.</param>
    /// <param name="layers">How many layers the config says there are.</param>
    /// <returns>The layers laid end to end, layer 0 first.</returns>
    /// <remarks>
    /// Addressed by number rather than by iterating whatever the runtime hands
    /// back, so the order cannot depend on how a dictionary happens to enumerate
    /// or on how names sort.
    /// </remarks>
    public static float[] Stack(Func<int, ReadOnlyMemory<float>?> layer, int layers)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layers);

        var slices = new ReadOnlyMemory<float>[layers];

        for (var i = 0; i < layers; i++)
        {
            slices[i] = layer(i) ?? throw new InvalidDataException(
                $"The model returned no cache for layer {i} of {layers}. "
                + "The graph does not match the config.");
        }

        var perLayer = slices[0].Length;

        for (var i = 1; i < layers; i++)
        {
            if (slices[i].Length != perLayer)
            {
                throw new InvalidDataException(
                    $"Layer {i} returned {slices[i].Length} values where layer 0 returned "
                    + $"{perLayer}. The layers must all be the same size to stack.");
            }
        }

        var stacked = new float[(long)perLayer * layers is var total && total <= int.MaxValue
            ? (int)total
            : throw new InvalidDataException("The cache is too large to hold in one array.")];

        for (var i = 0; i < layers; i++)
        {
            slices[i].Span.CopyTo(stacked.AsSpan(i * perLayer, perLayer));
        }

        return stacked;
    }

    /// <summary>
    /// How many values one stacked cache holds.
    /// </summary>
    /// <remarks>
    /// Both keys and values are this size, and it grows by
    /// <c>layers * kvHeads * headDim</c> with every frame — the term that makes
    /// long text expensive (see RESEARCH-ONNX.md).
    /// </remarks>
    public static long Length(int layers, int kvHeads, int sequence, int headDim) =>
        (long)layers * kvHeads * sequence * headDim;

    /// <summary>The shape decode expects, for building a tensor.</summary>
    public static int[] Shape(int layers, int kvHeads, int sequence, int headDim) =>
        [layers, 1, kvHeads, sequence, headDim];
}
