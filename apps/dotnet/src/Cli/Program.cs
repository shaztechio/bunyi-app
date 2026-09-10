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

using Bunyi.Cli.Commands;
using Bunyi.Cli.Protocol;
using Bunyi.Cli.Server;
using Bunyi.Core.Engine;
using Bunyi.Core.Models;
using Bunyi.Core.Runtime;
using Bunyi.Core.Settings;

namespace Bunyi.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("BUNYI_SERVER_BACKGROUND") == "1")
        {
            BackgroundStreams.Detach();
        }
        using var cancelled = new CancellationTokenSource();
        using var termination = ServerSignals.RegisterTermination(cancelled);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancelled.Cancel(); };
        Console.CancelKeyPress += cancel;
        try { return await RunAsync(args, Console.In, Console.Out, Console.Error, cancelled.Token); }
        finally { Console.CancelKeyPress -= cancel; }
    }

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter stdout, TextWriter stderr, CancellationToken ct = default)
    {
        var output = new CliOutput(stdout, stderr, args.Contains("--json"), args.Contains("--jsonl"));
        var request = new CommandRequest("command", [], Guid.NewGuid().ToString("N"));
        Dictionary<string, object?> result;
        try
        {
            request = CommandParser.Parse(args);
            await CommandParser.NormalizeInputsAsync(request, input, ct);
            if (request.Has("config")) Environment.SetEnvironmentVariable("BUNYI_CONFIG_FILE", request.Get("config"));
            var log = new CliLog();
            var dispatcher = new CommandDispatcher(log);
            var client = new ServerClient();
            if (request.Operation == "server.start") result = await client.StartAsync(ct);
            else if (request.Operation == "server.status") result = await client.StatusAsync(ct);
            else if (request.Operation == "server.stop") result = await client.StopAsync(ct);
            else if (request.Operation == "jobs.status") result = await client.JobStatusAsync(request.Get("target")!, ct);
            else if (request.Operation == "jobs.follow") result = await client.FollowAsync(request.Get("target")!, output.Event, ct);
            else if (request.Operation == "jobs.cancel") result = await client.CancelAsync(request.Get("target")!, ct);
            else if (request.Operation == "server.run")
            {
                await using var runtime = Runtime();
                var host = new ServerHost();
                await host.RunAsync((job, emit, token) => ExecuteSafelyAsync(job, runtime, dispatcher, emit, token),
                    () => new { loadedMode = runtime.Engine.LoadedMode is { } mode ? CommandParser.ModeName(mode) : null, loadedFolder = runtime.Engine.LoadedFolder },
                    () => runtime.Engine.UnloadAsync(), ct,
                    () => output.Event(CliProtocol.Event(request, "ready", ("pid", Environment.ProcessId), ("protocolVersion", 1))));
                result = CliProtocol.Result(request, ("stopped", true));
            }
            else if (CommandParser.ServerCapable(request) && !request.Has("one-shot"))
            {
                try { result = await client.ExecuteAsync(request, request.Has("detach"), output.Event, ct); }
                catch (ServerException ex) when (ex.Code == "server_unavailable" && !request.Has("require-server") && !request.Has("detach") && !request.Operation.StartsWith("server.", StringComparison.Ordinal))
                {
                    await using var runtime = Runtime();
                    result = await ExecuteSafelyAsync(request, runtime, dispatcher, output.Event, ct);
                }
            }
            else
            {
                if (request.Has("require-server")) throw new CliException("invalid_arguments", "--require-server applies to server-capable commands.");
                await using var runtime = Runtime();
                result = await ExecuteSafelyAsync(request, runtime, dispatcher, output.Event, ct);
            }
            BunyiRuntime Runtime() => new(log, new SettingsStore(log, Environment.GetEnvironmentVariable("BUNYI_CONFIG_FILE")), strictSettings: true);
        }
        catch (Exception ex) { result = Failure(request, ex); }
        output.Finish(result);
        return CliProtocol.ExitCode(result);
    }

    public static async Task<Dictionary<string, object?>> ExecuteSafelyAsync(CommandRequest request, BunyiRuntime runtime,
        CommandDispatcher dispatcher, Action<Dictionary<string, object?>> emit, CancellationToken ct)
    {
        try { return await dispatcher.ExecuteAsync(request, runtime, emit, ct); }
        catch (Exception ex) { return Failure(request, ex); }
    }

    public static Dictionary<string, object?> Failure(CommandRequest request, Exception ex)
    {
        var (code, exit) = ex switch
        {
            CliException error => (error.Code, error.ExitCode),
            ServerException error => (error.Code, 4),
            OperationCanceledException => ("cancelled", 5),
            PreflightFailedException => ("preflight_blocked", 3),
            EngineBusyException => ("bunyi_busy", 4),
            BunyiBusyException => ("bunyi_busy", 4),
            ChecksumMismatchException => ("checksum_mismatch", 10),
            RequiredFileMissingException => ("required_file_missing", 10),
            ArgumentException => ("invalid_arguments", 2),
            FileNotFoundException or DirectoryNotFoundException => ("missing_input", 3),
            HttpRequestException => ("download_failed", 10),
            IOException or UnauthorizedAccessException => ("filesystem_failed", 10),
            _ => ("operation_failed", 10)
        };
        var result = CliProtocol.Error(request, code, CliLog.Redact(ex.Message), exit);
        if (ex is PreflightFailedException preflight)
            result["report"] = preflight.Report with { Findings = preflight.Report.Findings.Select(f => f with { Detail = CliLog.Redact(f.Detail) }).ToArray() };
        return result;
    }
}
