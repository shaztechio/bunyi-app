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

using System.Globalization;
using System.Text;

namespace Bunyi.Core.Diagnostics;

/// <summary>One line in the log.</summary>
public sealed record LogEntry(DateTimeOffset Time, string Message)
{
    /// <summary>
    /// How a line appears when copied (spec §8: "timestamped, selectable,
    /// monospaced lines" with Copy). Time only, not the date: the window shows
    /// one session, and a date on every line is column noise.
    /// </summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Time:HH:mm:ss}  {Message}");
}

/// <summary>
/// The in-memory log behind the Logs window (spec §8), mirrored to the
/// platform's log where a per-user app can write to one.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is deliberately no ObservableCollection here.</b> The scaffold kept
/// one and mutated it under a lock from whatever thread was logging, which
/// Avalonia's collection binding cannot survive: a CollectionChanged raised off
/// the UI thread throws, or corrupts the list view. Core owns the data and
/// raises <see cref="Appended"/>; turning that into a bindable collection on
/// the UI thread is the App layer's job, and the App layer is the only place
/// that knows what a UI thread is.
/// </para>
/// <para>
/// <see cref="Appended"/> is raised on whichever thread logged, by design.
/// Marshalling it here would mean Core knowing about a dispatcher, and would
/// also lose the ordering guarantee the lock gives.
/// </para>
/// </remarks>
public sealed class LogStore : ILogSink
{
    private static readonly Lazy<LogStore> SharedStore = new(CreateShared);

    /// <summary>
    /// The app-wide log, mirrored to a file under the data root. Prefer taking
    /// an <see cref="ILogSink"/> by constructor; this exists for the
    /// composition root, not for reaching into from library code.
    /// </summary>
    public static LogStore Shared => SharedStore.Value;

    /// <summary>
    /// Builds the shared store with its file mirror, falling back to an
    /// in-memory-only store if the folder cannot be worked out.
    /// </summary>
    /// <remarks>
    /// Defensive because this runs in a lazy initializer: a throw here would
    /// surface as a TypeInitializationException from whatever first tried to
    /// log, which is both fatal and misleading. Losing the mirror is a bad day;
    /// failing to start because the log could not be set up is a worse one.
    /// </remarks>
    private static LogStore CreateShared()
    {
        try
        {
            return new LogStore(FileMirror(Infrastructure.AppPaths.LogsFolder));
        }
        catch
        {
            return new LogStore();
        }
    }

    /// <summary>
    /// Lines kept in memory. Matches the macOS store, so the two apps discard
    /// history at the same point.
    /// </summary>
    public const int Capacity = 2000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new(Capacity);
    private readonly Action<LogEntry>? _mirror;

    /// <summary>Creates a store, optionally mirroring each line somewhere durable.</summary>
    /// <param name="mirror">
    /// Called for every line, outside the lock. Used to write the file mirror
    /// §8 asks for; a failure here is swallowed, because losing a log line must
    /// never fail the work that produced it.
    /// </param>
    public LogStore(Action<LogEntry>? mirror = null) => _mirror = mirror;

    /// <summary>Raised after a line is appended, on the thread that logged it.</summary>
    public event EventHandler<LogEntry>? Appended;

    /// <summary>Raised after <see cref="Clear"/>.</summary>
    public event EventHandler? Cleared;

    /// <summary>How many lines are held.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <inheritdoc />
    public void Log(string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, message ?? string.Empty);

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity) _entries.Dequeue();
        }

        // Outside the lock: a slow mirror must not serialise every logger, and
        // a handler that logs would otherwise deadlock on a non-reentrant lock.
        Safely(() => _mirror?.Invoke(entry));
        Safely(() => Appended?.Invoke(this, entry));
    }

    /// <summary>Empties the log (spec §8: Clear).</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Safely(() => Cleared?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// A point-in-time copy, oldest first. A copy rather than a view, so a
    /// reader can enumerate it while other threads keep logging.
    /// </summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>
    /// The whole log as text, for Copy (spec §8).
    /// </summary>
    public string Text()
    {
        var builder = new StringBuilder();
        foreach (var entry in Snapshot()) builder.AppendLine(entry.ToString());
        return builder.ToString();
    }

    /// <summary>
    /// Writes each line to a dated file under the data root, and to standard
    /// error.
    /// </summary>
    /// <remarks>
    /// §8 asks for the platform's system log "where the app can write to one
    /// unprivileged". Windows' Event Log needs an administrator-created source,
    /// so a per-user app does not qualify; this is the equivalent that works
    /// the same on both targets. The point is that a run which crashed still
    /// left a record — the in-app window cannot be read after the window is
    /// gone.
    /// </remarks>
    public static Action<LogEntry> FileMirror(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var gate = new object();

        return entry =>
        {
            var path = Path.Combine(
                folder,
                string.Create(CultureInfo.InvariantCulture, $"bunyi-{entry.Time:yyyy-MM-dd}.log"));

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff zzz}  {entry.Message}{Environment.NewLine}");

            lock (gate)
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(path, line);
            }

            Console.Error.Write(line);
        };
    }

    /// <summary>
    /// Runs an action, discarding any failure.
    /// </summary>
    /// <remarks>
    /// Logging sits inside downloads, generations and backups. If a full disk
    /// or a throwing subscriber could propagate from here, it would fail the
    /// operation being logged — turning a diagnostic into the fault.
    /// </remarks>
    private static void Safely(Action action)
    {
        try { action(); }
        catch { /* a lost log line is not worth failing the work that wrote it */ }
    }
}
