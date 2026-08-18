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
using Bunyi.Core.Models;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Spec §3b end to end, against a real HTTP server.
/// </summary>
public sealed class ModelDownloaderTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private FakeModelServer _server = null!;
    private HttpClient _http = null!;
    private RecordingLog _log = null!;

    /// <summary>A small export: two required files, one of them with external data.</summary>
    private static ModelLayout Layout { get; } = new(
        "test-export",
        [
            new ModelFile("embeddings/config.json", Required: true),
            new ModelFile("model.onnx", Required: true),
            new ModelFile("model.onnx.data", Required: true),
            new ModelFile("tokenizer/vocab.json"),
        ]);

    public async Task InitializeAsync()
    {
        _server = await FakeModelServer.StartAsync();
        _http = new HttpClient();
        _log = new RecordingLog();

        _server.Add("embeddings/config.json", "{\"talker\":{}}")
               .AddBinary("model.onnx", 4_096)
               .AddBinary("model.onnx.data", 512_000)
               .Add("tokenizer/vocab.json", "{}");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private ModelDownloader NewDownloader(TimeProvider? time = null) =>
        new(_http, _log, time);

    private ModelSource Source => new ModelSource.BaseUrl(_server.BaseUrl);

    private string ModelFolder => ModelDownloader.FolderFor(Source, _root);

    [Fact]
    public async Task A_model_downloads_and_reports_itself_complete()
    {
        var folder = await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        Assert.True(File.Exists(Path.Combine(folder, "model.onnx")));
        Assert.True(File.Exists(Path.Combine(folder, "model.onnx.data")));
        Assert.True(File.Exists(Path.Combine(folder, "embeddings", "config.json")));
        Assert.True(ModelDownloader.Inspect(folder, Layout).IsComplete);
    }

    [Fact]
    public async Task Progress_follows_bytes_not_files()
    {
        // §3b is explicit: a model is one enormous file and a dozen small ones,
        // so a fraction driven by completed files sits still for minutes and
        // then jumps. Here model.onnx.data is 99% of the bytes and 25% of the
        // files, so a file-counting implementation could not produce a reading
        // between those two.
        var seen = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => { lock (seen) seen.Add(p); });

        await NewDownloader().EnsureModelAsync(Source, Layout, _root, progress, default);
        await Task.Delay(100);   // Progress<T> posts asynchronously

        var downloading = seen.Where(p => p.Phase == DownloadPhase.Downloading).ToList();
        Assert.NotEmpty(downloading);
        Assert.Contains(downloading, p => p.BytesTotal > 500_000);
        Assert.All(downloading, p => Assert.InRange(p.Fraction, 0, 1));
    }

    [Fact]
    public async Task A_complete_model_is_reused_with_no_network_at_all()
    {
        var downloader = NewDownloader();
        await downloader.EnsureModelAsync(Source, Layout, _root, null, default);
        var firstPass = _server.RequestCount("model.onnx.data");

        await downloader.EnsureModelAsync(Source, Layout, _root, null, default);

        // Offline reuse (§3b): not one more request, not even a HEAD. A
        // complete model must be usable with no network whatsoever.
        Assert.Equal(firstPass, _server.RequestCount("model.onnx.data"));
        Assert.Contains(_log.Lines, l => l.Contains("no download needed"));
    }

    [Fact]
    public async Task Files_already_on_disk_are_not_fetched_again()
    {
        // Incremental (§3b): stopping and starting must not re-fetch what is
        // there. Deleting one file leaves the model incomplete, so the run
        // proceeds — but only the missing file should move.
        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        // A required file, so the model is genuinely incomplete and the run
        // proceeds. Deleting an optional one would leave it complete, and the
        // downloader would rightly do nothing at all.
        var small = Path.Combine(ModelFolder, "embeddings", "config.json");
        File.Delete(small);
        var before = _server.BodyRequestCount("model.onnx.data");

        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        Assert.True(File.Exists(small), "the missing file is fetched");
        // A HEAD to size the job is fine; re-transferring the bytes is not.
        Assert.Equal(before, _server.BodyRequestCount("model.onnx.data"));
    }

    [Fact]
    public async Task A_manifest_with_checksums_is_preferred_and_verified()
    {
        _server.Add("manifest.sha256", _server.Sha256Manifest(
            "embeddings/config.json", "model.onnx", "model.onnx.data"));
        _server.Add("manifest.txt", "embeddings/config.json\nmodel.onnx\nmodel.onnx.data");

        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        Assert.Contains(_log.Lines, l => l.Contains("manifest.sha256"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("Using manifest.txt"));
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_and_the_bad_file_is_discarded()
    {
        // The file must not be left where a retry would find it and skip it.
        _server.Add("manifest.sha256", $"{new string('0', 64)}  model.onnx");

        var error = await Assert.ThrowsAsync<ChecksumMismatchException>(() =>
            NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default));

        Assert.Equal("model.onnx", error.File);
        Assert.False(File.Exists(Path.Combine(ModelFolder, "model.onnx")));
        Assert.Empty(Directory.Exists(ModelFolder)
            ? Directory.GetFiles(ModelFolder, "*.incomplete", SearchOption.AllDirectories)
            : []);
    }

    [Fact]
    public async Task A_digest_decides_reuse_where_a_matching_size_would_have_lied()
    {
        // The reason digests replace the size test: a corrupted file has
        // exactly the size it should. Same length, different bytes.
        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        var path = Path.Combine(ModelFolder, "model.onnx");
        var corrupted = File.ReadAllBytes(path);
        corrupted[0] ^= 0xFF;
        File.WriteAllBytes(path, corrupted);

        _server.Add("manifest.sha256", _server.Sha256Manifest(
            "embeddings/config.json", "model.onnx", "model.onnx.data"));

        // Corruption alone does not make a model incomplete — digests are
        // deliberately not part of that test, because hashing gigabytes on
        // every launch is worse than the problem it detects (§11 keeps it on
        // demand). Removing a required file is what puts us on the download
        // path, where the digest then decides reuse.
        File.Delete(Path.Combine(ModelFolder, "embeddings", "config.json"));

        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        Assert.Equal(_server.Sha256Of("model.onnx"), await HttpFileDownloader.Sha256OfFileAsync(path, default));
        Assert.Contains(_log.Lines, l => l.Contains("checksum does not match"));
    }

    [Fact]
    public async Task A_missing_required_file_is_reported_as_a_missing_file_not_a_broken_model()
    {
        _server.Remove("model.onnx");

        var error = await Assert.ThrowsAsync<RequiredFileMissingException>(() =>
            NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default));

        // §10: the message names the file and what to check.
        Assert.Contains("model.onnx", error.Message);
        Assert.Contains("404", error.Message);
    }

    [Fact]
    public async Task An_optional_file_that_is_missing_is_skipped_and_logged()
    {
        _server.Remove("tokenizer/vocab.json");

        var folder = await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        Assert.True(ModelDownloader.Inspect(folder, Layout).IsComplete);
        Assert.Contains(_log.Lines, l => l.Contains("Skipped tokenizer/vocab.json"));
    }

    [Fact]
    public async Task An_unsafe_manifest_entry_is_skipped_and_the_rest_downloads()
    {
        _server.Add("manifest.txt",
            "embeddings/config.json\n../escape.json\nmodel.onnx\nmodel.onnx.data");

        var folder = await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        Assert.True(ModelDownloader.Inspect(folder, Layout).IsComplete);
        Assert.Contains(_log.Lines, l => l.Contains("Ignoring unsafe"));
        Assert.False(File.Exists(Path.Combine(_root, "models", "escape.json")));
    }

    [Fact]
    public async Task An_interrupted_transfer_resumes_from_where_it_stopped()
    {
        // The point of ranged resume: the unit that must not be re-fetched is
        // the byte, not the file. One file is usually most of a model.
        _server.AbortAfterBytes = 100_000;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default));

        var partial = Path.Combine(ModelFolder, "model.onnx.data.incomplete");
        Assert.True(File.Exists(partial), "the partial file is what makes resuming possible");
        var carriedOver = new FileInfo(partial).Length;
        Assert.InRange(carriedOver, 1, 512_000);

        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        var finished = Path.Combine(ModelFolder, "model.onnx.data");
        Assert.True(File.Exists(finished));
        Assert.Equal(512_000, new FileInfo(finished).Length);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task A_resumed_file_is_still_correct_end_to_end()
    {
        // Resume is only worth having if the result is the same bytes. The
        // digest covers the whole file, including the part carried over.
        _server.Add("manifest.sha256", _server.Sha256Manifest(
            "embeddings/config.json", "model.onnx", "model.onnx.data"));
        _server.AbortAfterBytes = 100_000;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default));
        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        var path = Path.Combine(ModelFolder, "model.onnx.data");
        Assert.Equal(
            _server.Sha256Of("model.onnx.data"),
            await HttpFileDownloader.Sha256OfFileAsync(path, default));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_makes_the_client_start_again_cleanly()
    {
        // A 200 to a ranged request means the whole file is coming. Appending
        // it to what is on disk would produce a file of the right length made
        // of the wrong bytes.
        _server.AbortAfterBytes = 100_000;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default));

        _server.IgnoreRangeRequests = true;
        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        var path = Path.Combine(ModelFolder, "model.onnx.data");
        Assert.Equal(512_000, new FileInfo(path).Length);
        Assert.Equal(
            _server.Sha256Of("model.onnx.data"),
            await HttpFileDownloader.Sha256OfFileAsync(path, default));
        Assert.Contains(_log.Lines, l => l.Contains("ignored the resume request"));
    }

    [Fact]
    public async Task Cancelling_leaves_the_partial_file_so_the_next_run_can_resume()
    {
        // Cancelled from the progress callback rather than on a timer: half a
        // megabyte over loopback finishes long before any timer worth waiting
        // for, and a race is not a test.
        using var cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.BytesReceived > 0) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewDownloader().EnsureModelAsync(Source, Layout, _root, progress, cts.Token));

        // What matters is that stopping leaves the folder in a state the next
        // run can finish from, with the right bytes at the end of it.
        //
        // Deliberately NOT asserting that the model is incomplete at this
        // point: Progress<T> posts asynchronously, so on a fast machine the
        // cancellation can land after the last required file has already
        // arrived, and the model is legitimately complete. That is a race in
        // the test, not a defect in the app — CI on a quicker runner found it.
        await NewDownloader().EnsureModelAsync(Source, Layout, _root, null, default);

        var path = Path.Combine(ModelFolder, "model.onnx.data");
        Assert.Equal(512_000, new FileInfo(path).Length);
        Assert.Equal(
            _server.Sha256Of("model.onnx.data"),
            await HttpFileDownloader.Sha256OfFileAsync(path, default));
        Assert.True(ModelDownloader.Inspect(ModelFolder, Layout).IsComplete);
        Assert.Empty(Directory.GetFiles(ModelFolder, "*.incomplete", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_redirecting_server_that_hides_content_length_still_gets_a_real_total()
    {
        // A Hugging Face resolve/main URL answers HEAD with Content-Length: 0
        // and publishes the real figure in x-linked-size. Reading the zero would
        // give a bar that never moves.
        _server.HideContentLengthOnHead = true;
        _server.UseLinkedSizeHeader = true;

        var seen = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => { lock (seen) seen.Add(p); });

        await NewDownloader().EnsureModelAsync(Source, Layout, _root, progress, default);
        await Task.Delay(100);

        Assert.Contains(seen, p => p.BytesTotal >= 512_000);
    }

    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }
        public void Log(string message) { lock (_lines) _lines.Add(message); }
    }
}
