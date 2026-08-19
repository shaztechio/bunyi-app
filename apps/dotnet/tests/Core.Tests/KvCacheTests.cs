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

using Bunyi.Core.Qwen;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Joining prefill's 56 per-layer tensors into decode's two (spec §1).
/// </summary>
/// <remarks>
/// The whole reason this is a named thing with tests rather than a loop inside
/// the driver: getting the layer order wrong is undetectable at run time. Every
/// tensor is the right shape, the model runs, and the attention reads one
/// layer's memory where another's belongs. The output is audio — just not the
/// right audio.
/// </remarks>
public class KvCacheTests
{
    /// <summary>A layer whose every value is its own number, so order shows.</summary>
    private static ReadOnlyMemory<float>? Marked(int layer, int size = 3) =>
        Enumerable.Repeat((float)layer, size).ToArray();

    [Fact]
    public void Layers_are_laid_out_in_order()
    {
        var stacked = KvCache.Stack(l => Marked(l), layers: 4);

        Assert.Equal([0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3], stacked);
    }

    [Fact]
    public void Layers_are_addressed_by_number_and_not_by_sorted_name()
    {
        // The trap, made visible. Sorting "present_key_0".."present_key_27" as
        // text gives 0, 1, 10, 11, ... 19, 2, 20 — so with 28 layers, position
        // 2 would hold layer 10.
        var byName = Enumerable.Range(0, 28)
            .Select(KvCache.KeyName)
            .Order(StringComparer.Ordinal)
            .Select(n => int.Parse(n["present_key_".Length..]))
            .ToArray();

        Assert.Equal(10, byName[2]);   // what sorting would have given

        var stacked = KvCache.Stack(l => Marked(l, size: 1), layers: 28);

        Assert.Equal(2f, stacked[2]);  // what addressing by number gives
    }

    [Fact]
    public void The_stacked_cache_is_every_layer_end_to_end()
    {
        var stacked = KvCache.Stack(l => Marked(l, size: 5), layers: 28);

        Assert.Equal(28 * 5, stacked.Length);
    }

    [Fact]
    public void Each_layer_keeps_the_values_it_was_given()
    {
        var stacked = KvCache.Stack(
            l => new float[] { l * 10, l * 10 + 1 }, layers: 3);

        Assert.Equal([0, 1, 10, 11, 20, 21], stacked);
    }

    [Fact]
    public void A_layer_the_model_did_not_return_is_an_error()
    {
        // Rather than a zeroed layer, which would run and attend to nothing.
        var error = Assert.Throws<InvalidDataException>(
            () => KvCache.Stack(l => l == 5 ? null : Marked(l), layers: 28));

        Assert.Contains("layer 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Layers_of_different_sizes_are_an_error()
    {
        // A stacked tensor has one shape for every layer, so ragged input
        // cannot be laid out at all — better to say so than to write past a
        // slice.
        var error = Assert.Throws<InvalidDataException>(
            () => KvCache.Stack(l => Marked(l, size: l == 2 ? 4 : 3), layers: 4));

        Assert.Contains("Layer 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_names_match_what_the_exports_publish()
    {
        // Both exports name them this way; the driver looks them up by these.
        Assert.Equal("present_key_0", KvCache.KeyName(0));
        Assert.Equal("present_key_27", KvCache.KeyName(27));
        Assert.Equal("present_value_13", KvCache.ValueName(13));
    }

    [Fact]
    public void The_shape_is_the_one_decode_asks_for()
    {
        // [layers, batch, kv_heads, seq, head_dim] — the batch dimension is
        // there and is always one.
        Assert.Equal([28, 1, 8, 42, 128], KvCache.Shape(28, 8, 42, 128));
    }

    [Fact]
    public void The_length_grows_with_the_sequence_and_nothing_else()
    {
        // The term behind the memory measurements: one more frame costs
        // layers x kv_heads x head_dim, twice over for keys and values.
        var atForty = KvCache.Length(28, 8, 40, 128);
        var atFortyOne = KvCache.Length(28, 8, 41, 128);

        Assert.Equal(28L * 8 * 128, atFortyOne - atForty);
    }

    [Fact]
    public void The_length_at_the_exports_own_cap_is_large_but_fits()
    {
        // max_new_tokens is 8192 in the config: 0.94 GB of keys, and the same
        // again of values. Under a 32-bit count, but not by much.
        var length = KvCache.Length(28, 8, 8192, 128);

        Assert.Equal(234_881_024L, length);
        Assert.True(length < int.MaxValue);
    }

    [Fact]
    public void The_length_is_computed_in_64_bit()
    {
        // Past the export's cap the product does overflow a 32-bit multiply,
        // and an overflowed length is worse than a large one: it comes back
        // small and positive, so the allocation succeeds and the copy runs off
        // the end of it.
        var length = KvCache.Length(28, 8, 100_000, 128);

        Assert.Equal(2_867_200_000L, length);
        Assert.True(length > int.MaxValue, "this is the case a 32-bit multiply loses");
    }

    [Fact]
    public void Asking_for_no_layers_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KvCache.Stack(l => Marked(l), layers: 0));
    }
}
