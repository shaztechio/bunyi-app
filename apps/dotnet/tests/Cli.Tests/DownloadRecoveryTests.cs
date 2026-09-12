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
using System.Text.Json;
using Bunyi.Cli.Commands;
using Bunyi.Cli.Protocol;
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Models;
using Bunyi.Core.Runtime;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.Cli.Tests;

public sealed class DownloadRecoveryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("bunyi-cli-recovery-").FullName;
    public void Dispose() => Directory.Delete(root, true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mirror_failure_suggests_an_explicit_config_command_only_for_builtin_source(bool builtin)
    {
        var log = new SilentLog();
        var store = new SettingsStore(log, Path.Combine(root, "settings.json"));
        var source = builtin ? ModelConfigLibrary.BunyiMirror.For(TtsMode.VoiceClone) : "https://custom.example/clone";
        store.SaveStrict(new AppSettings { ModelsFolder = root }.WithSourceFor(TtsMode.VoiceClone, source));
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        await using var runtime = new BunyiRuntime(log, store, http);
        var request = CommandParser.Parse(["models", "download", "--mode", "clone"]);
        var result = await Program.ExecuteSafelyAsync(request, runtime, new(log), _ => { }, default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, CliProtocol.Json));
        Assert.Equal("download_service_unavailable", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(10, CliProtocol.ExitCode(result));
        Assert.Equal(source, store.Load().SourceFor(TtsMode.VoiceClone));
        if (builtin)
        {
            var command = json.RootElement.GetProperty("recovery").GetProperty("command").GetString()!;
            var arguments = json.RootElement.GetProperty("recovery").GetProperty("arguments")
                .EnumerateArray().Select(a => a.GetString()!).ToArray();
            var parsed = CommandParser.Parse(arguments);
            Assert.Equal("config.set", parsed.Operation);
            Assert.Equal(store.Path, parsed.Get("config"));
            Assert.Contains("modelSource.clone", command);
            Assert.Contains(BunyiRuntime.HuggingFaceSourceFor(TtsMode.VoiceClone), command);
        }
        else Assert.False(json.RootElement.TryGetProperty("recovery", out _));
        // The failed operation releases its lease and the resident runtime stays usable.
        var next = await Program.ExecuteSafelyAsync(CommandParser.Parse(["models", "status"]), runtime, new(log), _ => { }, default);
        Assert.Equal(0, CliProtocol.ExitCode(next));
    }

    [Fact]
    public async Task Rate_limit_wait_is_flushed_as_jsonl_and_cancellation_has_one_terminal_error()
    {
        var log = new SilentLog();
        var store = new SettingsStore(log, Path.Combine(root, "settings.json"));
        store.SaveStrict(new AppSettings { ModelsFolder = root });
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "60");
            response.Headers.TryAddWithoutValidation("RateLimit", "\"resolvers\";r=0;t=90");
            return response;
        }));
        await using var runtime = new BunyiRuntime(log, store, http);
        using var cancel = new CancellationTokenSource();
        var stdout = new StringWriter();
        var output = new CliOutput(stdout, new StringWriter(), false, true);
        var request = CommandParser.Parse(["models", "download", "--mode", "clone"]);
        var result = await Program.ExecuteSafelyAsync(request, runtime, new(log), message =>
        {
            output.Event(message);
            if (message.GetValueOrDefault("type")?.ToString() == "waiting") cancel.Cancel();
        }, cancel.Token);
        output.Finish(result);
        var events = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var wait = Assert.Single(events, e => e.RootElement.GetProperty("type").GetString() == "waiting").RootElement;
            Assert.Equal("huggingface.co", wait.GetProperty("host").GetString());
            Assert.InRange(wait.GetProperty("retryAfterSeconds").GetInt32(), 89, 90);
            Assert.Equal(1, wait.GetProperty("retryAttempt").GetInt32());
            Assert.True(wait.GetProperty("retryAt").TryGetDateTimeOffset(out _));
            Assert.Equal(0, wait.GetProperty("bytesCompleted").GetInt64());
            var terminal = events.Last().RootElement;
            Assert.Equal("cancelled", terminal.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(5, terminal.GetProperty("exitCode").GetInt32());
            Assert.Equal(1, calls);
        }
        finally { foreach (var e in events) e.Dispose(); }
    }

    [Fact]
    public void Exhausted_rate_limit_exposes_retry_timing_without_signed_query_values()
    {
        var error = new DownloadServiceException("download_rate_limited", "Try later",
            new("https://huggingface.co/file?secret=private"), DateTimeOffset.UtcNow.AddHours(1));
        var result = Program.Failure(CommandParser.Parse(["models", "download", "--all"]), error);
        var json = JsonSerializer.Serialize(result, CliProtocol.Json);
        Assert.DoesNotContain("private", json);
        Assert.NotNull(result["retryAt"]);
        Assert.Equal(10, CliProtocol.ExitCode(result));
        Assert.InRange((double)result["retryAfterSeconds"]!, 3500, 3600);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response(request));
    }
    private sealed class SilentLog : ILogSink { public void Log(string message) { } }
}
