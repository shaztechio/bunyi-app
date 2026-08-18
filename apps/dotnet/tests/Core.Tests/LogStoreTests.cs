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

using System.Collections.Concurrent;
using Bunyi.Core.Diagnostics;
using Xunit;

namespace Bunyi.Core.Tests;

public class LogStoreTests
{
    [Fact]
    public void Logged_lines_are_kept_in_order()
    {
        var log = new LogStore();

        log.Log("first");
        log.Log("second");
        log.Log("third");

        Assert.Equal(
            ["first", "second", "third"],
            log.Snapshot().Select(e => e.Message));
    }

    [Fact]
    public void The_oldest_lines_are_dropped_once_the_cap_is_reached()
    {
        var log = new LogStore();

        for (var i = 0; i < LogStore.Capacity + 250; i++) log.Log($"line {i}");

        var entries = log.Snapshot();
        Assert.Equal(LogStore.Capacity, entries.Count);
        Assert.Equal("line 250", entries[0].Message);
        Assert.Equal($"line {LogStore.Capacity + 249}", entries[^1].Message);
    }

    [Fact]
    public void A_snapshot_is_a_copy_and_does_not_change_underneath_a_reader()
    {
        // The reason Core hands out copies rather than a live view: a
        // generation logs while the Logs window is being read, and an
        // enumeration that could see a concurrent mutation would throw.
        var log = new LogStore();
        log.Log("before");

        var snapshot = log.Snapshot();
        log.Log("after");

        Assert.Single(snapshot);
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void Appending_raises_the_event_with_the_entry()
    {
        var log = new LogStore();
        var seen = new List<LogEntry>();
        log.Appended += (_, entry) => seen.Add(entry);

        log.Log("hello");

        Assert.Single(seen);
        Assert.Equal("hello", seen[0].Message);
    }

    [Fact]
    public void Clearing_empties_the_log_and_announces_it()
    {
        var log = new LogStore();
        var cleared = 0;
        log.Cleared += (_, _) => cleared++;
        log.Log("something");

        log.Clear();

        Assert.Empty(log.Snapshot());
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void Copyable_text_is_one_timestamped_line_per_entry()
    {
        var log = new LogStore();
        log.Log("downloading");
        log.Log("done");

        var lines = log.Text().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}  downloading$", lines[0]);
        Assert.Matches(@"^\d{2}:\d{2}:\d{2}  done$", lines[1]);
    }

    [Fact]
    public void A_subscriber_that_throws_does_not_fail_the_caller()
    {
        // Logging sits inside downloads and generations. A bad subscriber must
        // not take down the work being logged.
        var log = new LogStore();
        log.Appended += (_, _) => throw new InvalidOperationException("bad subscriber");

        log.Log("still fine");

        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void A_mirror_that_throws_does_not_fail_the_caller()
    {
        var log = new LogStore(_ => throw new IOException("disk full"));

        log.Log("still fine");

        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void The_mirror_sees_every_line()
    {
        var mirrored = new List<string>();
        var log = new LogStore(entry => mirrored.Add(entry.Message));

        log.Log("one");
        log.Log("two");

        Assert.Equal(["one", "two"], mirrored);
    }

    [Fact]
    public async Task Concurrent_writers_neither_lose_lines_nor_corrupt_the_log()
    {
        // The bug this type was rewritten to fix: the scaffold mutated an
        // ObservableCollection from arbitrary threads. Nothing in Core is
        // single-threaded — a download, a generation and the UI all log.
        const int writers = 8;
        const int perWriter = 500;
        var log = new LogStore();
        var seen = new ConcurrentBag<string>();
        log.Appended += (_, entry) => seen.Add(entry.Message);

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++) log.Log($"w{w}-{i}");
        })));

        Assert.Equal(writers * perWriter, seen.Count);
        Assert.Equal(LogStore.Capacity, log.Count);
        Assert.All(log.Snapshot(), e => Assert.False(string.IsNullOrEmpty(e.Message)));
    }

    [Fact]
    public void The_file_mirror_writes_a_dated_file_under_the_folder_it_is_given()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new LogStore(LogStore.FileMirror(folder));
            log.Log("written to disk");

            var files = Directory.GetFiles(folder, "bunyi-*.log");
            Assert.Single(files);
            Assert.Contains("written to disk", File.ReadAllText(files[0]));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
