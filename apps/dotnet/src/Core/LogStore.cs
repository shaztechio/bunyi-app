// In-memory log for the Logs window, mirrored to the platform log.
// Mirrors macOS LogStore. Spec: /spec/FEATURES.md §8.
using System.Collections.ObjectModel;

namespace Qwen3TtsStudio.Core;

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
