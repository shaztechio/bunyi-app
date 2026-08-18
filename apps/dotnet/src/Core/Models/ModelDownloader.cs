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

using System.Diagnostics;
using System.Security.Cryptography;
using Bunyi.Core.Diagnostics;

namespace Bunyi.Core.Models;

/// <summary>A required file the server does not have (spec §3c).</summary>
public sealed class RequiredFileMissingException(string file, int statusCode)
    : Exception(
        $"Your server is missing a required model file: {file} (HTTP {statusCode}). " +
        "Check the URL, and that every file in the manifest is actually served.")
{
    public string File { get; } = file;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Gets a model onto disk: from a Hugging Face repo, or from a base URL the
/// user self-hosts (spec §3b, §3c).
/// </summary>
public sealed class ModelDownloader(HttpClient http, ILogSink log, TimeProvider? time = null)
{
    private const string HubHost = "https://huggingface.co";

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HttpFileDownloader _files = new(http, log);

    /// <summary>
    /// Ensures the model is on disk and returns its folder.
    /// </summary>
    /// <remarks>
    /// A complete model is used without touching the network at all (spec §3b,
    /// offline reuse). That check comes first so an offline machine with the
    /// files already there simply works.
    /// </remarks>
    public async Task<string> EnsureModelAsync(
        ModelSource source,
        ModelLayout layout,
        string modelsRoot,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);

        var folder = FolderFor(source, modelsRoot);
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving));

        var state = Inspect(folder, layout);
        if (state.IsComplete)
        {
            _log.Log($"Using the model already in {folder} — no download needed.");
            progress?.Report(new DownloadProgress(DownloadPhase.Done));
            return folder;
        }

        _log.Log($"Preparing {layout.Id} in {folder} ({state.Describe()}).");

        var files = await ResolveFileListAsync(source, layout, progress, ct).ConfigureAwait(false);
        await DownloadAllAsync(source, files, folder, progress, ct).ConfigureAwait(false);

        var after = Inspect(folder, layout);
        if (!after.IsComplete)
        {
            throw new InvalidOperationException(
                $"The download finished but the model is not complete: {after.Describe()}.");
        }

        progress?.Report(new DownloadProgress(DownloadPhase.Done));
        return folder;
    }

    /// <summary>
    /// The file list to fetch: the server's manifest where it publishes one,
    /// otherwise the export's built-in list (spec §3c).
    /// </summary>
    /// <remarks>
    /// <c>manifest.sha256</c> is preferred over <c>manifest.txt</c> when both
    /// are served, because digests are strictly better than the size test they
    /// replace.
    /// </remarks>
    public async Task<IReadOnlyList<ModelFile>> ResolveFileListAsync(
        ModelSource source,
        ModelLayout layout,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (source is not ModelSource.BaseUrl baseUrl) return layout.Files;

        progress?.Report(new DownloadProgress(DownloadPhase.Manifest));

        foreach (var name in new[] { "manifest.sha256", "manifest.txt" })
        {
            var text = await TryGetStringAsync(Combine(baseUrl.Url, name), ct).ConfigureAwait(false);
            if (text is null) continue;

            var result = ManifestParser.Parse(text);
            if (result.Files.Count == 0) continue;

            foreach (var bad in result.Rejected)
            {
                // Skipped and logged, and the download continues: one bad line
                // must not cost a multi-gigabyte refetch (spec §3b).
                _log.Log($"Ignoring unsafe or duplicate manifest entry: {bad}");
            }

            var withDigests = result.Files.Count(f => f.Sha256 is not null);
            _log.Log($"Using {name} ({result.Files.Count} files, {withDigests} with checksums).");

            return MarkRequired(result.Files, layout);
        }

        _log.Log($"No manifest served — using the built-in file list for {layout.Id}.");
        return layout.Files;
    }

    /// <summary>
    /// Carries the built-in list's notion of "required" onto a server's
    /// manifest, which has no way to express it.
    /// </summary>
    private static IReadOnlyList<ModelFile> MarkRequired(
        IReadOnlyList<ModelFile> files, ModelLayout layout)
    {
        var required = layout.RequiredFiles
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return files
            .Select(f => required.Contains(f.RelativePath) ? f with { Required = true } : f)
            .ToList();
    }

    private async Task DownloadAllAsync(
        ModelSource source,
        IReadOnlyList<ModelFile> files,
        string folder,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        // Sizes first, so the bar has a real denominator rather than counting
        // files. Unknown sizes simply do not contribute (spec §3b).
        progress?.Report(new DownloadProgress(DownloadPhase.Sizing, FilesTotal: files.Count));

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var size = await _files.SizeOfAsync(UriFor(source, file.RelativePath), ct).ConfigureAwait(false);
            if (size is { } value) sizes[file.RelativePath] = value;
        }

        var total = sizes.Values.Sum();
        _log.Log($"Downloading {files.Count} files, about {DownloadProgress.Bytes(total)}.");

        using var monitor = new StallMonitor(_log, _time);
        var started = Stopwatch.StartNew();
        long received = 0, reused = 0;
        var done = 0;

        void Report(string? current) => progress?.Report(new DownloadProgress(
            DownloadPhase.Downloading,
            BytesReceived: received,
            BytesReused: reused,
            BytesTotal: total,
            BytesPerSecond: Rate(received, started.Elapsed),
            Eta: Eta(received, reused, total, started.Elapsed),
            CurrentFile: current,
            FilesDone: done,
            FilesTotal: files.Count));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (!ManifestPath.TryResolve(folder, file.RelativePath, out var destination))
            {
                _log.Log($"Ignoring unsafe manifest entry: {file.RelativePath}");
                continue;
            }

            Report(file.RelativePath);
            sizes.TryGetValue(file.RelativePath, out var expected);

            var result = await _files.FetchAsync(
                UriFor(source, file.RelativePath),
                destination,
                file.Sha256,
                expected > 0 ? expected : null,
                bytes =>
                {
                    Interlocked.Add(ref received, bytes);
                    monitor.Add(bytes);
                    Report(file.RelativePath);
                },
                ct).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case FileOutcome.Missing when file.Required:
                    throw new RequiredFileMissingException(file.RelativePath, 404);
                case FileOutcome.Missing:
                    // Best-effort by design: single-shard repos lack an index,
                    // and an absent tokenizer is backfilled later.
                    _log.Log($"Skipped {file.RelativePath} (not on the server).");
                    break;
                case FileOutcome.Reused:
                    reused += result.BytesOnDisk;
                    _log.Log($"Have {file.RelativePath} already ({DownloadProgress.Bytes(result.BytesOnDisk)}).");
                    break;
            }

            done++;
            Report(null);
        }
    }

    private static double Rate(long received, TimeSpan elapsed) =>
        elapsed.TotalSeconds > 0.5 ? received / elapsed.TotalSeconds : 0;

    private static TimeSpan? Eta(long received, long reused, long total, TimeSpan elapsed)
    {
        if (total <= 0 || elapsed.TotalSeconds <= 1) return null;

        var remaining = total - received - reused;
        if (remaining <= 0) return TimeSpan.Zero;

        var rate = received / elapsed.TotalSeconds;
        if (rate <= 0) return null;

        return TimeSpan.FromSeconds(remaining / rate);
    }

    /// <summary>
    /// Whether a model folder may be loaded without the network — the ONNX
    /// family's rule from /spec/DATA-FORMATS.md.
    /// </summary>
    /// <remarks>
    /// Every substantive clause of the MLX rule fails on real ONNX exports, so
    /// this is a different test rather than an adapted one: required entries
    /// present and non-empty, every graph's external-data sibling beside it,
    /// and no interrupted transfer anywhere in the tree. The external-data
    /// clause is the one that earns its keep — a graph is megabytes and its
    /// data is gigabytes, so an interrupted download usually leaves the small
    /// half, and every other check would pass.
    /// </remarks>
    /// <summary>
    /// Re-hashes what is on disk against the digests the source published, and
    /// returns the files that do not match (spec §11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever on demand. It reads every byte of the model — several gigabytes
    /// — which is why §11 keeps it out of the preflight that runs before every
    /// generation.
    /// </para>
    /// <para>
    /// A file the manifest gives no digest for is skipped rather than reported:
    /// there is nothing to compare it against, and calling that a mismatch would
    /// make the check useless against every source that publishes
    /// <c>manifest.txt</c>. A required file that is missing entirely is a
    /// mismatch — <see cref="Inspect" /> would say the same, but this is the
    /// check the user asked for.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> VerifyAsync(
        ModelSource source,
        ModelLayout layout,
        string folder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);

        var files = await ResolveFileListAsync(source, layout, null, ct).ConfigureAwait(false);
        var bad = new List<string>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var path = Path.Combine(folder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                if (file.Required) bad.Add(file.RelativePath);
                continue;
            }

            if (file.Sha256 is null) continue;

            var actual = await HashFileAsync(path, ct).ConfigureAwait(false);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Log($"Integrity: {file.RelativePath} expected {file.Sha256}, found {actual}.");
                bad.Add(file.RelativePath);
            }
        }

        return bad;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static ModelCompleteness Inspect(string folder, ModelLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(layout);

        var missing = new List<string>();
        var partial = new List<string>();

        if (!Directory.Exists(folder))
        {
            return new ModelCompleteness(false, layout.RequiredFiles.Select(f => f.RelativePath).ToList(), partial);
        }

        foreach (var file in layout.RequiredFiles)
        {
            var path = Path.Combine(folder, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length == 0) missing.Add(file.RelativePath);
        }

        foreach (var (graph, data) in layout.ExternalDataPairs)
        {
            var graphPath = Path.Combine(folder, graph);
            var dataPath = Path.Combine(folder, data);
            if (File.Exists(graphPath) && (!File.Exists(dataPath) || new FileInfo(dataPath).Length == 0))
            {
                partial.Add(data);
            }
        }

        foreach (var leftover in Directory.EnumerateFiles(
                     folder, "*" + HttpFileDownloader.PartialExtension, SearchOption.AllDirectories))
        {
            partial.Add(Path.GetRelativePath(folder, leftover));
        }

        return new ModelCompleteness(missing.Count == 0 && partial.Count == 0, missing, partial);
    }

    /// <summary>
    /// Where a source's files live under the models root
    /// (/spec/DATA-FORMATS.md, "Models folder").
    /// </summary>
    public static string FolderFor(ModelSource source, string modelsRoot) => source switch
    {
        ModelSource.Repo repo => Path.Combine(modelsRoot, "models", Path.Combine(repo.Id.Split('/'))),
        ModelSource.BaseUrl url => Path.Combine(modelsRoot, "models", "self-hosted", Slug(url.Url)),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>
    /// A filesystem-safe folder name for a base URL.
    /// </summary>
    /// <remarks>
    /// Pinned by /spec/DATA-FORMATS.md rather than left to taste: two apps must
    /// produce the same name for the same URL, or a models folder stops being
    /// interchangeable between them.
    /// </remarks>
    public static string Slug(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var raw = (string.IsNullOrEmpty(url.Host) ? "server" : url.Host) + url.AbsolutePath;
        var cleaned = new string(raw.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());

        var trimmed = cleaned.Trim('-');
        return trimmed.Length == 0 ? "server" : trimmed;
    }

    private static Uri UriFor(ModelSource source, string relativePath) => source switch
    {
        ModelSource.Repo repo => new Uri($"{HubHost}/{repo.Id}/resolve/main/{relativePath}"),
        ModelSource.BaseUrl url => Combine(url.Url, relativePath),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static Uri Combine(Uri baseUrl, string relative)
    {
        var text = baseUrl.AbsoluteUri;
        if (!text.EndsWith('/')) text += "/";
        return new Uri(text + relative);
    }

    private async Task<string?> TryGetStringAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;   // no manifest is normal, not an error
        }
    }
}
