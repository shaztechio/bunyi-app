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

using Avalonia.Threading;
using Bunyi.App.ViewModels;

namespace Bunyi.App.Infrastructure;

/// <summary>
/// The real batching timer, which ticks on the UI thread.
/// </summary>
/// <remarks>
/// A <see cref="DispatcherTimer" /> rather than a
/// <see cref="System.Threading.Timer" /> precisely because its callback already
/// runs where the bound collection can be touched — the whole point of the
/// batch is to arrive on the UI thread once instead of many times.
/// </remarks>
public sealed class DispatcherTimerFactory : IBatchTimerFactory
{
    /// <inheritdoc />
    public IBatchTimer Create(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        timer.Start();
        return new Running(timer);
    }

    private sealed class Running(DispatcherTimer timer) : IBatchTimer
    {
        public void Start() => timer.Start();
        public void Stop() => timer.Stop();
        public void Dispose() => timer.Stop();
    }
}
