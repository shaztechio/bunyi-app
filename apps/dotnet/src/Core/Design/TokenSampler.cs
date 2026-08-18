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

namespace Bunyi.Core.Design;

/// <summary>How the next token is chosen.</summary>
/// <param name="Temperature">Above 1 flattens the distribution, below sharpens it.</param>
/// <param name="TopK">Only the k likeliest are eligible; 0 means all of them.</param>
/// <param name="RepetitionPenalty">Above 1 discourages tokens already produced.</param>
public sealed record SamplingOptions(
    float Temperature = 0.9f,
    int TopK = 50,
    float RepetitionPenalty = 1.05f)
{
    /// <summary>The export's own defaults, from its reference script.</summary>
    public static SamplingOptions Default { get; } = new();
}

/// <summary>
/// Chooses the next codec token from the talker's logits.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the export's reference script, in its order, because the order
/// changes the result: suppression before the penalty before top-k before the
/// softmax. Doing the softmax first, for instance, would spend probability mass
/// on tokens that are about to be removed.
/// </para>
/// <para>
/// The randomness is injected. Speech generation is deliberately stochastic —
/// the same text twice gives different audio, which the measurements in
/// RESEARCH-ONNX.md ran into as varying frame counts — so a test cannot assert
/// on what comes out unless it controls what comes in.
/// </para>
/// </remarks>
public sealed class TokenSampler(Func<double>? random = null)
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    /// <summary>
    /// Picks a token from <paramref name="logits" />, which is modified in place.
    /// </summary>
    /// <param name="logits">The scores, one per token.</param>
    /// <param name="options">Temperature, top-k and the repetition penalty.</param>
    /// <param name="generated">Tokens already produced, for the penalty.</param>
    public int Sample(
        Span<float> logits,
        SamplingOptions options,
        IReadOnlyCollection<int>? generated = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        ApplyRepetitionPenalty(logits, options.RepetitionPenalty, generated);

        if (options.Temperature is > 0 and not 1f)
        {
            for (var i = 0; i < logits.Length; i++) logits[i] /= options.Temperature;
        }

        var cutoff = TopKCutoff(logits, options.TopK);

        // Softmax over what survives, shifted by the maximum so that exp() has
        // nothing to overflow. Suppressed tokens are -infinity, and exp of that
        // is zero, so they simply do not appear.
        var max = float.NegativeInfinity;
        for (var i = 0; i < logits.Length; i++)
        {
            if (logits[i] >= cutoff && logits[i] > max) max = logits[i];
        }

        if (float.IsNegativeInfinity(max))
        {
            throw new InvalidOperationException(
                "Every token was suppressed, so there is nothing to choose from.");
        }

        double total = 0;
        for (var i = 0; i < logits.Length; i++)
        {
            logits[i] = logits[i] >= cutoff ? MathF.Exp(logits[i] - max) : 0f;
            total += logits[i];
        }

        // Walk the cumulative distribution. The final clamp matters: floating
        // point can leave the target a hair above the running total, and falling
        // off the end would return nothing.
        var target = _random() * total;
        double running = 0;
        for (var i = 0; i < logits.Length; i++)
        {
            running += logits[i];
            if (running >= target && logits[i] > 0) return i;
        }

        for (var i = logits.Length - 1; i >= 0; i--)
        {
            if (logits[i] > 0) return i;
        }

        throw new InvalidOperationException("No token had any probability.");
    }

    /// <summary>
    /// The score a token must reach to stay eligible.
    /// </summary>
    /// <remarks>
    /// Returns negative infinity when top-k is off or wider than the vocabulary,
    /// which lets the caller compare without a special case.
    /// </remarks>
    internal static float TopKCutoff(ReadOnlySpan<float> logits, int topK)
    {
        if (topK <= 0 || topK >= logits.Length) return float.NegativeInfinity;

        // The k-th largest, by partial selection. Sorting the whole vocabulary
        // would be 3072 elements per frame per group — sixteen times a frame.
        var best = new float[topK];
        best.AsSpan().Fill(float.NegativeInfinity);

        foreach (var value in logits)
        {
            if (value <= best[0]) continue;

            // best[0] is the smallest kept; insert and slide it into place.
            var i = 1;
            while (i < topK && best[i] < value)
            {
                best[i - 1] = best[i];
                i++;
            }

            best[i - 1] = value;
        }

        return best[0];
    }

    /// <summary>
    /// Divides a positive score by the penalty and multiplies a negative one.
    /// </summary>
    /// <remarks>
    /// Not a subtraction: a score below zero divided by a penalty above one gets
    /// <b>larger</b>, which would encourage exactly what the penalty exists to
    /// discourage. The reference script branches on the sign for this reason.
    /// </remarks>
    internal static void ApplyRepetitionPenalty(
        Span<float> logits, float penalty, IReadOnlyCollection<int>? generated)
    {
        if (penalty == 1f || generated is null || generated.Count == 0) return;

        foreach (var token in generated)
        {
            if ((uint)token >= (uint)logits.Length) continue;

            logits[token] = logits[token] > 0
                ? logits[token] / penalty
                : logits[token] * penalty;
        }
    }
}
