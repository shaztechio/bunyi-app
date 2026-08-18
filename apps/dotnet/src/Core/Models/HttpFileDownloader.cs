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

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Bunyi.Core.Diagnostics;

namespace Bunyi.Core.Models;

/// <summary>A downloaded file did not match the digest its server published.</summary>
public sealed class ChecksumMismatchException(string file, string expected, string actual)
    : Exception(BuildMessage(file, expected, actual))
{
    public string File { get; } = file;
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;

    // Digests are truncated for the message a user sees: twelve characters is
    // plenty to tell two files apart, and sixty-four in a dialog is a wall. The
    // log records both in full (spec §10).
    private static string BuildMessage(string file, string expected, string actual) =>
        $"{file} downloaded but did not match the checksum your server published " +
        $"(expected {Short(expected)}…, got {Short(actual)}…). The file may be corrupt " +
        "or still uploading. Try again, and re-check the manifest if it persists.";

    private static string Short(string digest) =>
        digest.Length <= 12 ? digest : digest[..12];
}

/// <summary>What happened to one file.</summary>
public enum FileOutcome
{
    /// <summary>Fetched from the network.</summary>
    Downloaded,
    /// <summary>Already on disk and good — nothing transferred.</summary>
    Reused,
    /// <summary>The server does not have it, and it was not required.</summary>
    Missing,
}

/// <summary>The result of asking for one file.</summary>
public sealed record FileResult(FileOutcome Outcome, long BytesTransferred, long BytesOnDisk);

/// <summary>
/// Downloads one file, reporting bytes as they arrive and resuming a partial
/// transfer where the server allows it.
/// </summary>
public sealed class HttpFileDownloader(HttpClient http, ILogSink log)
{
    /// <summary>
    /// Extension for a transfer in flight.
    /// </summary>
    /// <remarks>
    /// The same token <see cref="ModelDownloader.Inspect"/> treats as an
    /// interrupted download, so a partial file can never be mistaken for a
    /// complete model. It is deliberately left behind on cancellation — that
    /// file is what makes resuming possible.
    /// </remarks>
    public const string PartialExtension = ".incomplete";

    private const int BufferSize = 1 << 20;   // 1 MiB: large enough that read syscalls
                                              // disappear against the hashing.

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>
    /// Ensures <paramref name="destination"/> holds the file at
    /// <paramref name="uri"/>.
    /// </summary>
    /// <param name="expectedSha256">
    /// The digest the manifest published, or null. Where present it decides
    /// both whether an existing file may be reused and whether a fresh one is
    /// accepted — size equality is precisely the test a truncated file passes.
    /// </param>
    /// <param name="onBytes">Called with each chunk's size, as it arrives.</param>
    public async Task<FileResult> FetchAsync(
        Uri uri,
        string destination,
        string? expectedSha256,
        long? expectedSize,
        Action<long>? onBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (await CanReuseAsync(destination, expectedSha256, expectedSize, ct).ConfigureAwait(false))
        {
            var length = new FileInfo(destination).Length;
            return new FileResult(FileOutcome.Reused, 0, length);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + PartialExtension;

        // Resume only makes sense with a digest or a known size to check the
        // result against; without either, a resumed file could be a mixture of
        // two different server-side versions and nothing would notice.
        var resumeFrom = 0L;
        if (File.Exists(partial))
        {
            resumeFrom = new FileInfo(partial).Length;
            if (expectedSize is { } size && resumeFrom >= size) resumeFrom = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new FileResult(FileOutcome.Missing, 0, 0);
        }

        response.EnsureSuccessStatusCode();

        // A 200 to a ranged request means the server ignored the range: it is
        // sending the whole file, so whatever is on disk must be discarded
        // rather than appended to.
        var appending = resumeFrom > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !appending)
        {
            _log.Log($"{Path.GetFileName(destination)}: the server ignored the resume request, starting again.");
            resumeFrom = 0;
        }

        var hash = expectedSha256 is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Hashing what is already on disk, so the digest covers the whole file
        // and not merely the part fetched this time.
        if (appending && hash is not null)
        {
            await using var existing = File.OpenRead(partial);
            await HashStreamAsync(existing, hash, ct).ConfigureAwait(false);
        }

        var transferred = 0L;
        await using (var file = new FileStream(
            partial,
            appending ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true))
        {
            await using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[BufferSize];

            while (true)
            {
                var read = await network.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0) break;

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash?.AppendData(buffer, 0, read);

                transferred += read;
                onBytes?.Invoke(read);
            }
        }

        if (hash is not null && expectedSha256 is not null)
        {
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                // Discarded, not left for a retry to find and skip. A failed
                // file that survives is one the next run treats as present.
                TryDelete(partial);
                _log.Log(
                    $"Checksum mismatch for {Path.GetFileName(destination)}: " +
                    $"expected {expectedSha256}, got {actual}. The file was discarded.");
                throw new ChecksumMismatchException(
                    Path.GetFileName(destination), expectedSha256, actual);
            }
        }

        File.Move(partial, destination, overwrite: true);
        return new FileResult(FileOutcome.Downloaded, transferred, new FileInfo(destination).Length);
    }

    /// <summary>
    /// Whether a file already on disk can be used without fetching it again.
    /// </summary>
    /// <remarks>
    /// With a digest the digest decides; that is the whole point of publishing
    /// one, since a truncated file has exactly the size it should. Without one,
    /// size is the best test available.
    /// </remarks>
    private async Task<bool> CanReuseAsync(
        string destination, string? expectedSha256, long? expectedSize, CancellationToken ct)
    {
        if (!File.Exists(destination)) return false;

        var info = new FileInfo(destination);
        if (info.Length == 0) return false;

        if (expectedSha256 is not null)
        {
            var actual = await Sha256OfFileAsync(destination, ct).ConfigureAwait(false);
            if (string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase)) return true;

            _log.Log($"Re-fetching {Path.GetFileName(destination)} — on disk but the checksum does not match.");
            return false;
        }

        if (expectedSize is { } size) return info.Length == size;

        // Nothing to check against. Keeping it is the incremental behaviour
        // §3b asks for; the completeness rule is what catches a bad file later.
        return true;
    }

    /// <summary>The SHA-256 of a file, read in chunks.</summary>
    /// <remarks>
    /// Chunked because the weights are gigabytes: reading one into memory to
    /// hash it would cost more than the download.
    /// </remarks>
    public static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

        await HashStreamAsync(stream, hash, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task HashStreamAsync(
        Stream stream, IncrementalHash hash, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
    }

    /// <summary>
    /// Asks the server how large a file is, for a real progress total.
    /// </summary>
    /// <remarks>
    /// Two traps this has to survive. A Hugging Face <c>resolve/main</c> URL
    /// redirects to a CDN and answers with <c>Content-Length: 0</c>, publishing
    /// the real figure in <c>x-linked-size</c>; and a compressed response
    /// reports a length that will not match the bytes written to disk. In both
    /// cases the wrong answer is worse than none — it would produce a bar that
    /// never reaches its end, or reaches it early and sits there.
    /// </remarks>
    public async Task<long?> SizeOfAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            if (response.Headers.TryGetValues("x-linked-size", out var linked)
                && long.TryParse(linked.FirstOrDefault(), out var linkedSize)
                && linkedSize > 0)
            {
                return linkedSize;
            }

            if (response.Content.Headers.ContentEncoding.Count > 0) return null;

            var length = response.Content.Headers.ContentLength;
            return length > 0 ? length : null;
        }
        catch (HttpRequestException)
        {
            return null;   // an unknown size is not a failed download
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* a retry will overwrite it */ }
        catch (UnauthorizedAccessException) { }
    }
}
