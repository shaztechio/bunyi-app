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

// In-memory log for the Logs window, mirrored to the platform log.
// Mirrors macOS LogStore. Spec: /spec/FEATURES.md §8.
using System.Collections.ObjectModel;

namespace Bunyi.Core;

public sealed record LogEntry(DateTimeOffset Time, string Message);

public sealed class LogStore
{
    public static LogStore Shared { get; } = new();

    private const int Cap = 2000;
    private readonly object _gate = new();
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public void Log(string message)
    {
        // TODO: mirror to the platform log (EventLog on Windows / syslog on
        // Linux) and marshal to the UI thread. Spec §8.
        lock (_gate)
        {
            Entries.Add(new LogEntry(DateTimeOffset.Now, message));
            while (Entries.Count > Cap) Entries.RemoveAt(0);
        }
    }

    public void Clear() { lock (_gate) Entries.Clear(); }
}
