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
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Models;
using Bunyi.Core.Settings;

const long sampleBytes = 1 << 20;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
using var http = new HttpClient(new ReceiptHandler()) { Timeout = Timeout.InfiniteTimeSpan };
var log = new ConsoleLog();
var models = new ModelDownloader(http, log);
var files = new HttpFileDownloader(http, log);
// Never read user settings or an existing model cache.
var scratch = Directory.CreateTempSubdirectory("bunyi-mirror-smoke-");
var failures = 0;
try
{
    foreach (var mode in Enum.GetValues<TtsMode>())
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var source = new ModelSource.BaseUrl(new Uri(ModelConfigLibrary.BunyiMirror.For(mode)));
            var layout = ModelLayout.For(mode);
            Console.WriteLine($"START {mode}: {source.Url} at {DateTimeOffset.UtcNow:O}");
            var manifest = await models.ResolveFileListAsync(source, layout, null, deadline.Token);
            // Reject fallback to the built-in list, missing entries, and manifests without hashes.
            foreach (var required in layout.RequiredFiles)
                Require(manifest.Any(f => f.RelativePath == required.RelativePath && f.Sha256 is not null),
                    $"Manifest missing required file/checksum: {required.RelativePath}");

            Uri Url(ModelFile file) => new(source.Url.AbsoluteUri.TrimEnd('/') + "/" + file.RelativePath);
            string Destination(ModelFile file) => Path.Combine(scratch.FullName, mode.ToString(), file.RelativePath);

            var small = manifest.First(f => f.Required && f.RelativePath.EndsWith("config.json", StringComparison.Ordinal));
            var smallSize = await files.SizeOfAsync(Url(small), deadline.Token);
            Require(smallSize is > 0 and <= sampleBytes, "Config HEAD must report 1 byte to 1 MiB");
            long smallReceived = 0;
            using (var smallStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token))
            {
                var result = await files.FetchAsync(Url(small), Destination(small), small.Sha256, smallSize,
                    count => { smallReceived += count; if (smallReceived > sampleBytes) smallStop.Cancel(); }, smallStop.Token);
                Require(result.Outcome == FileOutcome.Downloaded && result.BytesTransferred == smallSize,
                    "Config must be downloaded and checksum-verified, not reused or missing");
                Console.WriteLine($"CHECKSUM OK {mode}: {small.RelativePath}, {result.BytesTransferred} bytes");
            }

            var weight = manifest.First(f => f.Required && f.RelativePath.EndsWith(".onnx.data", StringComparison.Ordinal));
            var weightSize = await files.SizeOfAsync(Url(weight), deadline.Token);
            Require(weightSize > 2 * sampleBytes, "Weight HEAD must report more than 2 MiB");
            long received = 0;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            try
            {
                await files.FetchAsync(Url(weight), Destination(weight), weight.Sha256, weightSize,
                    count =>
                    {
                        received += count;
                        // The callback precedes the write. Strictly greater leaves at least
                        // one earlier chunk on disk even if the first read fills the 1 MiB buffer.
                        if (received > sampleBytes) stop.Cancel();
                    }, stop.Token);
                throw new InvalidOperationException("Weight transfer unexpectedly completed without cancellation");
            }
            catch (OperationCanceledException) when (received > sampleBytes && !deadline.IsCancellationRequested)
            {
                var partial = new FileInfo(Destination(weight) + HttpFileDownloader.PartialExtension);
                Require(partial.Exists && partial.Length > 0 && partial.Length < weightSize,
                    "Cancellation must retain a nonempty partial weight file");
                Require(!File.Exists(Destination(weight)), "Partial weight must not be marked complete");
                Console.WriteLine($"WEIGHT BYTES OK {mode}: {received} received, {partial.Length} retained");
            }
            Console.WriteLine($"PASS {mode} in {timer.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception error)
        {
            failures++;
            // HTTP exception text may contain a signed URL. Emit only safe classifications.
            var reason = error switch
            {
                DownloadServiceException service => service.Code,
                HttpRequestException request => $"HTTP {request.StatusCode?.ToString() ?? "transport failure"}; {request.HttpRequestError}",
                OperationCanceledException => "deadline exceeded or response exceeded byte budget",
                InvalidOperationException invalid => invalid.Message,
                _ => error.GetType().Name,
            };
            Console.WriteLine($"FAIL {mode} in {timer.Elapsed.TotalSeconds:F1}s: {reason}");
        }
    }
}
finally
{
    scratch.Delete(recursive: true);
}
Console.WriteLine($"Mirror smoke test: {3 - failures}/3 modes passed. This checks download access, not inference or MSIX packaging.");
return failures == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class ConsoleLog : ILogSink
{
    // The manifest is remote input: omit its raw rejected entries from CI output.
    public void Log(string message) { }
}

sealed class ReceiptHandler : DelegatingHandler
{
    public ReceiptHandler() : base(new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var original = request.RequestUri!;
        var timer = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (HttpRequestException error)
        {
            // Manifest resolution intentionally tolerates transport failures. Record
            // their cause here before it falls back to the built-in file list.
            Console.WriteLine($"HTTP {request.Method} {original.GetLeftPart(UriPartial.Path)} failed: {error.HttpRequestError}");
            throw;
        }
        Console.WriteLine($"HTTP {request.Method} {original.GetLeftPart(UriPartial.Path)} -> {(int)response.StatusCode} " +
            $"host={response.RequestMessage?.RequestUri?.Host}, headers in {timer.Elapsed.TotalSeconds:F2}s");
        foreach (var name in new[] { "CF-Ray", "Retry-After", "X-Bunyi-Download-Status" })
            if (response.Headers.TryGetValues(name, out var values))
                Console.WriteLine($"  {name}: {string.Join(", ", values)}");
        return response;
    }
}
