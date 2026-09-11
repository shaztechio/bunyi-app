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
using Bunyi.Core.Models;
using Bunyi.Core.Runtime;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class DownloadServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bunyi-service-tests", Guid.NewGuid().ToString("N"));
    private static readonly Uri Source = new("https://models.example/model");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-11T12:00:00Z");
    public DownloadServiceTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData("60", null, 60)]
    [InlineData("Fri, 11 Sep 2026 12:02:00 GMT", null, 120)]
    [InlineData("10", "\"resolvers\";r=0;t=75", 75)]
    [InlineData("120", "\"resolvers\";r=0;t=75", 120)]
    [InlineData("broken", "\"resolvers\";r=0;t=40", 40)]
    [InlineData("0", null, 1)]
    [InlineData("259200", null, 259200)]
    public void Honors_server_timing(string retry, string? limit, double seconds)
    {
        using var response = Limited(retry, limit);
        Assert.Equal(seconds, DownloadHttp.DelayFor(response, Start, 1).TotalSeconds);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    public void Invalid_timing_uses_exponential_delay_with_jitter(int attempt, int minimum)
    {
        using var response = Limited("invalid", "t=not-a-number");
        Assert.InRange(DownloadHttp.DelayFor(response, Start, attempt).TotalSeconds, minimum, minimum + 1);
    }

    [Fact]
    public async Task Retries_original_request_at_most_three_times_and_never_before_reset()
    {
        var clock = new FakeTimeProvider(Start);
        var times = new List<DateTimeOffset>();
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(Source, request.RequestUri);
            Assert.Equal("bytes=2-", request.Headers.Range!.ToString());
            times.Add(clock.GetUtcNow());
            return Limited("2");
        }));
        var waits = new List<DownloadWait?>();
        var task = DownloadHttp.SendAsync(http, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Source);
            request.Headers.Range = new RangeHeaderValue(2, null);
            return request;
        }, default, waits.Add, clock);
        await AdvanceUntilComplete(task, clock);
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => task);
        Assert.Equal("download_rate_limited", error.Code);
        Assert.Equal(4, times.Count);
        for (var i = 1; i < times.Count; i++) Assert.True(times[i] - times[i - 1] >= TimeSpan.FromSeconds(2));
        Assert.Equal(3, waits.Count(w => w is null));
        Assert.Equal(3, waits.Max(w => w?.Attempt));
    }

    [Fact]
    public async Task Stop_during_wait_keeps_partial_and_does_not_retry()
    {
        var partial = Path.Combine(root, "model.incomplete");
        await File.WriteAllBytesAsync(partial, [1, 2]);
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Limited("60"); }));
        var downloader = new HttpFileDownloader(http, new RecordingLog());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.FetchAsync(Source,
            Path.Combine(root, "model"), null, 4, null, cancel.Token,
            onWait: wait => { if (wait is not null) cancel.Cancel(); }));
        Assert.Equal(1, calls);
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(Path.Combine(root, "model")));
    }

    [Fact]
    public async Task Resume_after_429_keeps_range_and_verifies_the_whole_file()
    {
        var destination = Path.Combine(root, "model");
        await File.WriteAllBytesAsync(destination + ".incomplete", [1, 2]);
        var clock = new FakeTimeProvider(Start);
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("bytes=2-", request.Headers.Range!.ToString());
            if (++calls == 1) return Limited("2");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([3, 4]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 3, 4);
            return response;
        }));
        var digest = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4 }));
        var task = new HttpFileDownloader(http, new RecordingLog(), clock).FetchAsync(Source,
            destination, digest, 4, null, default);
        await AdvanceUntilComplete(task, clock);
        var result = await task;
        Assert.Equal(2, result.BytesTransferred);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".incomplete"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(502)]
    public async Task Manifest_service_failures_do_not_fall_back_to_a_builtin_list(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Retry-After", "1800");
            return response;
        }));
        var downloader = new ModelDownloader(http, new RecordingLog());
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => downloader.ResolveFileListAsync(
            new ModelSource.BaseUrl(Source), new ModelLayout("test", [new("model.onnx", Required: true)]), null, default));
        Assert.Equal(status == 429 ? "download_rate_limited" : "download_service_unavailable", error.Code);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Head_service_failures_do_not_become_unknown_sizes(int status)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Retry-After", "1800");
            return response;
        }));
        await Assert.ThrowsAsync<DownloadServiceException>(() =>
            new HttpFileDownloader(http, new RecordingLog()).SizeOfAsync(Source, default));
    }

    [Theory]
    [InlineData(true, "paused")]
    [InlineData(false, "unavailable")]
    public async Task Only_the_explicit_header_identifies_a_pause(bool marked, string word)
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (marked) response.Headers.Add("X-Bunyi-Download-Status", "paused");
            return response;
        }));
        var error = await Assert.ThrowsAsync<DownloadServiceException>(() => DownloadHttp.SendAsync(http,
            () => new HttpRequestMessage(HttpMethod.Get, Source), default));
        Assert.Contains(word, error.Message);
    }

    [Fact]
    public async Task Aggregate_planning_reports_manifest_and_head_waits_then_finishes()
    {
        var clock = new FakeTimeProvider(Start);
        var seen = new HashSet<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var key = request.Method + request.RequestUri!.AbsolutePath;
            if (!seen.Add(key)) return request.Method == HttpMethod.Head
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) }
                : new(HttpStatusCode.OK) { Content = request.RequestUri.AbsolutePath.EndsWith("manifest.sha256")
                    ? new StringContent("model.onnx") : new ByteArrayContent([1, 2, 3, 4]) };
            return Limited("1");
        }));
        var events = new List<AggregateDownloadProgress>();
        var task = new ModelDownloader(http, new RecordingLog(), clock).DownloadAssetsAsync(
            [new("test", new ModelSource.BaseUrl(Source), new("test", [new("model.onnx", Required: true)]))],
            root, new InlineProgress<AggregateDownloadProgress>(events.Add), default);
        await AdvanceUntilComplete(task, clock);
        Assert.Single(await task);
        Assert.Contains(events, p => p.Phase == DownloadPhase.Waiting && p.CurrentFile == "manifest.sha256");
        Assert.Contains(events, p => p.Phase == DownloadPhase.Waiting && p.CurrentFile == "model.onnx");
        Assert.All(events.Where(p => p.Phase == DownloadPhase.Waiting), p => Assert.NotNull(p.Download!.ServiceWait));
        Assert.Equal(4, events.Last().BytesCompleted);
    }

    private static HttpResponseMessage Limited(string retry, string? limit = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", retry);
        if (limit is not null) response.Headers.TryAddWithoutValidation("RateLimit", limit);
        return response;
    }

    private static async Task AdvanceUntilComplete(Task task, FakeTimeProvider clock)
    {
        // Real time only yields continuations; all policy deadlines use the fake clock.
        for (var i = 0; i < 1000 && !task.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1);
        }
        Assert.True(task.IsCompleted, "Download did not terminate within the bounded retry policy.");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response(request));
    }
    private sealed class RecordingLog : Bunyi.Core.Diagnostics.ILogSink
    {
        public void Log(string message) { }
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
