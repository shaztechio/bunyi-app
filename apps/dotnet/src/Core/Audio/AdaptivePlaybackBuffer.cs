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

namespace Bunyi.Core.Audio;

/// <summary>Predicts the head start needed to cover slower-than-playback production.</summary>
internal sealed class AdaptivePlaybackBuffer
{
    internal const double MinimumSeconds = 10;
    private readonly object _gate = new();
    private readonly Queue<(double Audio, double Time)> _recent = new();
    private double _estimate;
    private double _generated;
    private double _produced;
    private (double Audio, double Time)? _first;
    private double _lastChunkSeconds = 2;

    internal void Configure(double estimatedSeconds)
    {
        if (!double.IsFinite(estimatedSeconds) || estimatedSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedSeconds));
        lock (_gate) _estimate = estimatedSeconds;
    }

    internal void ObserveGenerated(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return;
        lock (_gate) _generated = Math.Max(_generated, seconds);
    }

    internal void ObserveProduced(double seconds, double elapsed)
    {
        lock (_gate)
        {
            if (seconds <= _produced || !double.IsFinite(seconds) || !double.IsFinite(elapsed)) return;
            _lastChunkSeconds = seconds - _produced;
            _produced = seconds;
            _first ??= (seconds, elapsed);
            _recent.Enqueue((seconds, elapsed));
            while (_recent.Count > 4) _recent.Dequeue();
        }
    }

    internal double TargetSeconds(double elapsed, double playedSeconds = 0)
    {
        lock (_gate)
        {
            if (_estimate <= 0) return MinimumSeconds;
            // Do not infer a sustained rate from model loading or one chunk.
            if (_first is not { } first || _recent.Count < 2)
                return Math.Max(MinimumSeconds, _estimate);
            var recent = _recent.Peek();
            var overallRate = (_produced - first.Audio) / Math.Max(.001, elapsed - first.Time);
            var recentRate = (_produced - recent.Audio) / Math.Max(.001, elapsed - recent.Time);
            var rate = Math.Max(.001, Math.Min(overallRate, recentRate) * .8);
            // Generation may exceed the original text estimate. Retain a horizon
            // until completion is explicit instead of predicting zero work left.
            var horizon = Math.Max(MinimumSeconds, _estimate * .15);
            var expectedEnd = Math.Max(_estimate, Math.Max(_generated, _produced) + horizon);
            var remaining = Math.Max(0, expectedEnd - _produced);
            var deficit = remaining * Math.Max(0, 1 / rate - 1);
            var deliveryAllowance = _lastChunkSeconds / rate + 2;
            // If the deficit exceeds the forecast remainder, wait for that
            // whole remainder instead of displaying an impossible larger goal.
            var remainingToPlay = Math.Max(0, expectedEnd - playedSeconds);
            return Math.Ceiling(Math.Max(MinimumSeconds, Math.Min(deficit + deliveryAllowance, remainingToPlay)));
        }
    }
}
