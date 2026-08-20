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

using System.Collections.ObjectModel;
using System.Text;
using Bunyi.App.Infrastructure;
using Bunyi.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bunyi.App.ViewModels;

/// <summary>
/// The Logs window's state (spec §8).
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="ObservableCollection{T}" /> lives here and not in
/// <see cref="LogStore" />, which raises its events on whichever thread did the
/// work. Avalonia throws, or corrupts what it is drawing, if a bound collection
/// is changed from anywhere but the UI thread.
/// </para>
/// <para>
/// Arrivals are batched rather than posted one at a time. A generation logs
/// token milestones and a download logs progress, so a line-per-post during a
/// fast run is a dispatcher message per line — enough to make the list stutter
/// while scrolling. The delay is short enough that the log still reads as live.
/// </para>
/// </remarks>
public sealed partial class LogsViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long arrivals are gathered before the list is touched.
    /// </summary>
    /// <remarks>
    /// Long enough to collapse a burst, short enough that nobody notices the
    /// wait. A log that lags visibly behind the status line looks broken.
    /// </remarks>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    private readonly LogStore _store;
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private readonly List<LogEntry> _pending = [];
    private readonly IBatchTimer _timer;
    private bool _disposed;

    /// <summary>Every line, oldest first.</summary>
    public ObservableCollection<LogEntry> Lines { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// Every line as one block of text, which is what the window shows.
    /// </summary>
    /// <remarks>
    /// One string rather than one control per line, because a selection cannot
    /// cross from one control into the next: with a control per line, dragging
    /// down the log selected nothing beyond the line it started in. Copying a
    /// run of lines out of a log is the ordinary reason to select any of it.
    /// <see cref="Lines" /> stays as the model of what is shown — the count
    /// beside the buttons reads from it, and it is what the cap is applied to.
    /// </remarks>
    [ObservableProperty]
    private string _document = string.Empty;

    /// <summary>
    /// Raised when lines have been added, so the view can scroll.
    /// </summary>
    /// <remarks>
    /// An event rather than a property the view watches: "something arrived" is
    /// a moment, not a state, and the view needs to know it happened even when
    /// the count is unchanged because the cap dropped as many as it gained.
    /// </remarks>
    public event EventHandler? LinesAppended;

    public LogsViewModel(LogStore store, Action<Action>? post = null, IBatchTimerFactory? timers = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _post = post ?? UiThread.Post;

        foreach (var entry in _store.Snapshot()) Lines.Add(entry);
        IsEmpty = Lines.Count == 0;
        Rebuild();

        _store.Appended += OnAppended;
        _store.Cleared += OnCleared;

        _timer = (timers ?? new DispatcherTimerFactory()).Create(BatchInterval, Drain);
    }

    private void OnAppended(object? sender, LogEntry entry)
    {
        lock (_gate) _pending.Add(entry);
    }

    private void OnCleared(object? sender, EventArgs e) => _post(() =>
    {
        lock (_gate) _pending.Clear();

        Lines.Clear();
        IsEmpty = true;
        Rebuild();
    });

    /// <summary>Moves everything gathered since the last tick into the list.</summary>
    internal void Drain()
    {
        LogEntry[] batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;

            batch = [.. _pending];
            _pending.Clear();
        }

        foreach (var entry in batch) Lines.Add(entry);

        // §8 keeps the store to a cap; the list follows it rather than growing
        // without limit behind a window nobody has open.
        while (Lines.Count > LogStore.Capacity) Lines.RemoveAt(0);

        IsEmpty = Lines.Count == 0;

        // Before the event, so the view has the new text to lay out by the time
        // it is asked to scroll to the end of it.
        Rebuild();

        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds <see cref="Document" /> from <see cref="Lines" />.
    /// </summary>
    /// <remarks>
    /// The whole string each time rather than an append, because the cap drops
    /// lines from the front: at a steady state of a full log, most batches both
    /// add and remove, and an appending buffer would have to be rebuilt anyway.
    /// It is bounded work — <see cref="LogStore.Capacity" /> lines — and it
    /// happens at most once per <see cref="BatchInterval" />.
    /// </remarks>
    private void Rebuild()
    {
        var builder = new StringBuilder();

        foreach (var entry in Lines)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(entry.ToString());
        }

        Document = builder.ToString();
    }

    /// <summary>Everything on screen, as text (spec §8: Copy).</summary>
    public string Text() => _store.Text();

    /// <summary>Empties the log.</summary>
    [RelayCommand]
    private void Clear() => _store.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _store.Appended -= OnAppended;
        _store.Cleared -= OnCleared;
        _timer.Dispose();
    }
}

/// <summary>A repeating timer, so tests need not wait for real milliseconds.</summary>
public interface IBatchTimerFactory
{
    /// <summary>
    /// Creates a <b>started</b> timer that calls <paramref name="tick" />.
    /// </summary>
    /// <remarks>
    /// Started, because the two callers that want it stopped say so and the one
    /// that wants it running would otherwise have to remember. A timer created
    /// stopped and never started is a silent no-op; a timer created started and
    /// never stopped is a wasted tick, which is the cheaper mistake.
    /// </remarks>
    IBatchTimer Create(TimeSpan interval, Action tick);
}

/// <summary>A repeating timer that can be paused.</summary>
public interface IBatchTimer : IDisposable
{
    /// <summary>Begins ticking, or carries on if already ticking.</summary>
    void Start();

    /// <summary>Stops ticking until started again.</summary>
    void Stop();
}
