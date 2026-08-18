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
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bunyi.Core.Tests;

/// <summary>
/// A self-hosted model server, for testing the whole of spec §3b against a real
/// HTTP stack.
/// </summary>
/// <remarks>
/// Kestrel rather than a stubbed <c>HttpMessageHandler</c>, because most of
/// what §3b requires <i>is</i> HTTP behaviour: range requests, 206 against 200,
/// content lengths, redirects, a connection that dies mid-body. A handcrafted
/// handler would mostly assert that the handcrafted handler works.
/// </remarks>
public sealed class FakeModelServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _bodyRequests = new(StringComparer.Ordinal);

    /// <summary>Serve a 200 with the whole body even when a range was asked for.</summary>
    public bool IgnoreRangeRequests { get; set; }

    /// <summary>Refuse range requests outright, as a server without support would.</summary>
    public bool SupportsRanges { get; set; } = true;

    /// <summary>Cut the connection after this many bytes of a body, once.</summary>
    public int? AbortAfterBytes { get; set; }

    /// <summary>Answer HEAD with Content-Length: 0, as a redirecting CDN does.</summary>
    public bool HideContentLengthOnHead { get; set; }

    /// <summary>Publish the real size in x-linked-size, as the Hub does.</summary>
    public bool UseLinkedSizeHeader { get; set; }

    private FakeModelServer(IHost host) => _host = host;

    /// <summary>Starts a server on a free port.</summary>
    public static async Task<FakeModelServer> StartAsync()
    {
        FakeModelServer? server = null;

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.Configure(app => app.Run(context => server!.HandleAsync(context)));
            })
            .Build();

        server = new FakeModelServer(host);
        await host.StartAsync();
        return server;
    }

    /// <summary>The base URL a <c>ModelSource.BaseUrl</c> would point at.</summary>
    public Uri BaseUrl
    {
        get
        {
            var addresses = _host.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
            return new Uri(addresses.Addresses.First() + "/");
        }
    }

    /// <summary>Serves <paramref name="content"/> at <paramref name="path"/>.</summary>
    public FakeModelServer Add(string path, byte[] content)
    {
        _files[path] = content;
        return this;
    }

    /// <summary>Serves text at <paramref name="path"/>.</summary>
    public FakeModelServer Add(string path, string content) =>
        Add(path, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>Serves <paramref name="size"/> bytes of deterministic filler.</summary>
    public FakeModelServer AddBinary(string path, int size, byte seed = 7)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)((i * 31 + seed) & 0xFF);
        return Add(path, bytes);
    }

    /// <summary>Removes a file, so requests for it 404.</summary>
    public FakeModelServer Remove(string path)
    {
        _files.TryRemove(path, out _);
        return this;
    }

    /// <summary>The SHA-256 of a served file, as a manifest would publish it.</summary>
    public string Sha256Of(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(_files[path]));

    /// <summary>Every request for a path, HEAD included.</summary>
    public int RequestCount(string path) => _requests.GetValueOrDefault(path, 0);

    /// <summary>
    /// Requests that actually transferred a body.
    /// </summary>
    /// <remarks>
    /// The figure that matters for §3b. A second run legitimately issues a HEAD
    /// per file to size the job; what must not happen is the bytes moving
    /// again.
    /// </remarks>
    public int BodyRequestCount(string path) => _bodyRequests.GetValueOrDefault(path, 0);

    /// <summary>Builds a manifest.sha256 body for the given paths.</summary>
    public string Sha256Manifest(params string[] paths) =>
        string.Join("\n", paths.Select(p => $"{Sha256Of(p)}  {p}"));

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.TrimStart('/') ?? string.Empty;
        _requests.AddOrUpdate(path, 1, (_, count) => count + 1);

        if (!_files.TryGetValue(path, out var content))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            _bodyRequests.AddOrUpdate(path, 1, (_, count) => count + 1);
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            if (UseLinkedSizeHeader)
            {
                context.Response.Headers["x-linked-size"] =
                    content.Length.ToString(CultureInfo.InvariantCulture);
            }

            if (!HideContentLengthOnHead) context.Response.ContentLength = content.Length;
            return;
        }

        var start = 0;
        var rangeHeader = context.Request.Headers.Range.ToString();

        if (!string.IsNullOrEmpty(rangeHeader) && SupportsRanges && !IgnoreRangeRequests)
        {
            var from = rangeHeader.Replace("bytes=", string.Empty).Split('-')[0];
            if (int.TryParse(from, out var parsed) && parsed > 0 && parsed < content.Length)
            {
                start = parsed;
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.ContentRange =
                    $"bytes {start}-{content.Length - 1}/{content.Length}";
            }
        }

        var body = content.AsMemory(start);

        if (AbortAfterBytes is { } limit && body.Length > limit)
        {
            // Write a prefix, then drop the connection: what a flaky link does,
            // and the case resume exists for.
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body[..limit]);
            await context.Response.Body.FlushAsync();
            AbortAfterBytes = null;
            context.Abort();
            return;
        }

        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
