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

using Bunyi.Core.Diagnostics;

namespace Bunyi.Core.Models;

/// <summary>
/// Reports how a download is going, and says so when it stops going (spec §3b).
/// </summary>
/// <remarks>
/// <para>
/// <b>It watches bytes from the network, not the folder.</b> §3b is explicit,
/// and both alternatives were real bugs on the macOS side: counting completed
/// files makes a multi-gigabyte transfer look frozen, and watching the
/// destination folder makes a transfer that buffers elsewhere before moving the
/// finished file into place look dead while it is running at full speed.
/// </para>
/// <para>
/// Driven by <see cref="TimeProvider"/> so the 10 s and 30 s rules are asserted
/// in milliseconds with a fake clock, rather than by a test that sleeps for
/// half a minute and is deleted the first time someone is in a hurry.
/// </para>
/// <para>
/// It counts ticks rather than comparing timestamps. The timer already defines
/// the interval, so elapsed time is <c>ticks * interval</c> by construction —
/// which is monotonic, and therefore immune to the clock moving underneath a
/// long download when NTP corrects it or the season changes.
/// </para>
/// </remarks>
public sealed class StallMonitor : IDisposable
{
    /// <summary>How often progress is logged.</summary>
    public static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long without a byte before the user is told.</summary>
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(30);

    private readonly ILogSink _log;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _gate = new();

    /// <summary>Quiet ticks before the user is told. 3 x 10 s = 30 s.</summary>
    private static readonly int StallTicks =
        (int)Math.Round(StallAfter.TotalSeconds / LogInterval.TotalSeconds);

    private long _bytes;
    private long _bytesAtLastTick;
    private int _quietTicks;
    private bool _warned;

    public StallMonitor(ILogSink log, TimeProvider? time = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Tick(), null, LogInterval, LogInterval);
    }

    /// <summary>Total bytes seen so far.</summary>
    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    /// <summary>Records bytes arriving from the network.</summary>
    public void Add(long count)
    {
        if (count <= 0) return;
        lock (_gate)
        {
            _bytes += count;
        }
    }

    private void Tick()
    {
        string? message;

        lock (_gate)
        {
            if (_bytes != _bytesAtLastTick)
            {
                _bytesAtLastTick = _bytes;
                _quietTicks = 0;
                _warned = false;
                message = $"{DownloadProgress.Bytes(_bytes)} received";
            }
            else if (!_warned && ++_quietTicks >= StallTicks)
            {
                _warned = true;   // said once, not every ten seconds
                message = "No new data for 30 s — the connection may be stalled.";
            }
            else
            {
                message = null;
            }
        }

        if (message is not null) _log.Log(message);
    }

    public void Dispose() => _timer.Dispose();
}
