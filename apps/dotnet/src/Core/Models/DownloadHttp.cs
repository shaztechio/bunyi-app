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
using System.Net;
using System.Text.RegularExpressions;

namespace Bunyi.Core.Models;

public sealed record DownloadWait(string Host, DateTimeOffset RetryAt, int Attempt, int SecondsRemaining);

/// <summary>Service errors must survive optional manifest/size probes.</summary>
public sealed class DownloadServiceException(string code, string message, Uri source,
    DateTimeOffset? retryAt = null) : Exception(message)
{
    public string Code { get; } = code;
    public Uri SourceUri { get; } = source;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

/// <summary>One bounded, cancellable HTTP policy for every model request.</summary>
internal static class DownloadHttp
{
    internal static TimeSpan DelayFor(HttpResponseMessage response, DateTimeOffset now, int attempt)
    {
        double? seconds = null;
        var maximum = (DateTimeOffset.MaxValue - now).TotalSeconds - 1;
        if (response.Headers.TryGetValues("Retry-After", out var retry))
        {
            foreach (var value in retry)
            {
                if (double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    && double.IsFinite(number) && number >= 0)
                    seconds = Math.Max(seconds ?? 0, Math.Min(number, maximum));
                else if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date))
                    seconds = Math.Max(seconds ?? 0, Math.Clamp((date - now).TotalSeconds, 0, maximum));
            }
        }
        if (response.Headers.TryGetValues("RateLimit", out var limits))
            foreach (var value in limits)
                foreach (Match match in Regex.Matches(value, @"(?:^|[;,])\s*t\s*=\s*(\d+)(?=\s*(?:[;,]|$))"))
                    if (double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var number))
                        seconds = Math.Max(seconds ?? 0, Math.Min(number, maximum));
        return TimeSpan.FromSeconds(Math.Max(1, seconds ?? Math.Pow(2, attempt) + Random.Shared.NextDouble()));
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http,
        Func<HttpRequestMessage> createRequest, CancellationToken ct,
        Action<DownloadWait?>? waiting = null, TimeProvider? time = null)
    {
        var clock = time ?? TimeProvider.System;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = createRequest(); // Always original URL, never a cached signed redirect.
            var source = request.RequestUri!;
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var delay = DelayFor(response, clock.GetUtcNow(), attempt + 1);
                var until = clock.GetUtcNow() + delay;
                response.Dispose();
                if (attempt >= 3 || delay > TimeSpan.FromMinutes(15))
                    throw new DownloadServiceException("download_rate_limited",
                        $"{source.Host} is limiting downloads. Try again after {until:yyyy-MM-dd HH:mm:ss} UTC. Your downloaded files have been kept.", source, until);
                try
                {
                    while (clock.GetUtcNow() < until)
                    {
                        var remaining = until - clock.GetUtcNow();
                        if (remaining <= TimeSpan.Zero) break;
                        waiting?.Invoke(new(source.Host, until, attempt + 1, (int)Math.Ceiling(remaining.TotalSeconds)));
                        await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), clock, ct).ConfigureAwait(false);
                    }
                }
                finally { waiting?.Invoke(null); }
                continue;
            }
            if ((int)response.StatusCode >= 500)
            {
                var paused = response.Headers.TryGetValues("X-Bunyi-Download-Status", out var values)
                    && values.Contains("paused", StringComparer.OrdinalIgnoreCase);
                DateTimeOffset? until = response.Headers.Contains("Retry-After")
                    ? clock.GetUtcNow() + DelayFor(response, clock.GetUtcNow(), 1) : null;
                response.Dispose();
                throw new DownloadServiceException("download_service_unavailable",
                    paused ? "Model downloads are temporarily paused. Your downloaded files have been kept."
                        : $"{source.Host} is temporarily unavailable. Your downloaded files have been kept. Try again later.", source, until);
            }
            return response;
        }
    }
}
