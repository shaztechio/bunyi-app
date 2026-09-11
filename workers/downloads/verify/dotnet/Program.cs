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
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

if (args.Length < 3) throw new ArgumentException("Usage: <base URL> <object key> <SHA-256> [--expiry]");
var original = new Uri(args[0].TrimEnd('/') + "/" + args[1]);
using var direct = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(9));
var ct = deadline.Token;
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
using var redirect = await direct.GetAsync(original, HttpCompletionOption.ResponseHeadersRead, ct);
Require(redirect.StatusCode == HttpStatusCode.TemporaryRedirect, "Expected 307");
var signed = redirect.Headers.Location!;
Require(signed.Host.EndsWith(".r2.cloudflarestorage.com"), "Unexpected redirect host");
Require(redirect.Headers.CacheControl?.NoStore == true, "Redirect must not be cached");
using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, original), ct);
Require(head.StatusCode == HttpStatusCode.OK, "HEAD did not follow the method-specific redirect");
var size = head.Content.Headers.ContentLength ?? throw new Exception("No Content-Length");
Console.WriteLine($"HEAD OK: {args[1]}, {size} bytes");

async Task DownloadAndCheck(bool resume)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var timer = Stopwatch.StartNew();
    long received = 0;
    var buffer = new byte[131072];
    var stopAt = Math.Min(8 * 1024 * 1024, Math.Max(1, size / 2));
    using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct))
    using (var response = await client.GetAsync(original, HttpCompletionOption.ResponseHeadersRead, stop.Token))
    {
        Require(response.StatusCode == HttpStatusCode.OK, "Full GET failed");
        using var stream = await response.Content.ReadAsStreamAsync(stop.Token);
        while (true)
        {
            var capacity = resume ? (int)Math.Min(buffer.Length, stopAt - received) : buffer.Length;
            var count = await stream.ReadAsync(buffer.AsMemory(0, capacity), stop.Token);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
            received += count;
            if (resume && received >= stopAt)
            {
                stop.Cancel();
                try
                {
                    var unexpected = await stream.ReadAsync(buffer, stop.Token);
                    throw new Exception($"Stop was ignored: read {unexpected} bytes");
                }
                catch (OperationCanceledException) { }
                break;
            }
        }
    }
    if (resume)
    {
        var offset = received;
        using var request = new HttpRequestMessage(HttpMethod.Get, original);
        request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == HttpStatusCode.PartialContent, "Resume did not return 206");
        Require(response.Content.Headers.ContentRange?.From == offset, "Incorrect resume offset");
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            hash.AppendData(buffer, 0, count);
            received += count;
        }
    }
    var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    Require(received == size, "Wrong final size");
    Require(digest == args[2], "SHA-256 mismatch");
    Console.WriteLine($"{(resume ? "Stop/resume" : "Full file")} OK: {received} bytes, " +
        $"{timer.Elapsed.TotalSeconds:F2}s, {received / timer.Elapsed.TotalSeconds / 1_000_000:F2} MB/s, SHA-256 verified");
}
await DownloadAndCheck(false);
await DownloadAndCheck(true);

if (args.Contains("--expiry"))
{
    var query = signed.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
        .ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
    var issued = DateTimeOffset.ParseExact(query["X-Amz-Date"], "yyyyMMdd'T'HHmmss'Z'",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    var expiry = issued.AddSeconds(int.Parse(query["X-Amz-Expires"]) + 5);
    while (DateTimeOffset.UtcNow < expiry)
    {
        var left = expiry - DateTimeOffset.UtcNow;
        Console.WriteLine($"Waiting {Math.Ceiling(left.TotalSeconds)}s for original link expiry");
        await Task.Delay(left < TimeSpan.FromSeconds(30) ? left : TimeSpan.FromSeconds(30), ct);
    }
    using var oldRequest = new HttpRequestMessage(HttpMethod.Get, signed);
    oldRequest.Headers.Range = new RangeHeaderValue(100, 199);
    using var oldResponse = await client.SendAsync(oldRequest, HttpCompletionOption.ResponseHeadersRead, ct);
    Require(oldResponse.StatusCode == HttpStatusCode.Forbidden, "Expired link was not rejected");
    using var retry = new HttpRequestMessage(HttpMethod.Get, original);
    retry.Headers.Range = new RangeHeaderValue(100, 199);
    using var renewed = await client.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, ct);
    Require(renewed.StatusCode == HttpStatusCode.PartialContent, "Fresh redirect after expiry failed");
    Require(renewed.Content.Headers.ContentRange?.From == 100, "Fresh resume offset changed");
    Require((await renewed.Content.ReadAsByteArrayAsync(ct)).Length == 100, "Wrong resumed byte count");
    Console.WriteLine("Expiry OK: stale link rejected; original URL supplied a fresh working range redirect");
}
