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
using System.IO.Pipes;
using System.Text.Json;
using Bunyi.Cli.Protocol;
using Bunyi.Cli.Server;
using Xunit;

namespace Bunyi.Cli.Tests;

public sealed class ServerTests
{
    private static CommandRequest Request(string operation = "generate.preset") => new(operation, [], Guid.NewGuid().ToString("N"));
    private static string? Text(Dictionary<string, object?> message, string key) => message.GetValueOrDefault(key)?.ToString();

    [Fact]
    public async Task DetachedJobsAreFifoAndProgressIsObservable()
    {
        var calls = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RunningServer.Start(async (request, emit, ct) =>
        {
            calls.Enqueue(request.OperationId);
            emit(CliProtocol.Event(request, "generating", ("frames", 12)));
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return CliProtocol.Result(request, ("outputPath", "/output.wav"));
        });
        var first = Request();
        var second = Request();
        Assert.Equal("accepted", Text(await server.Client.ExecuteAsync(first, true, _ => { }, default), "type"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.Client.ExecuteAsync(second, true, _ => { }, default);
        var state = await server.Client.JobStatusAsync(second.OperationId, default);
        Assert.Equal("queued", Text(state, "state"));
        var status = await server.Client.StatusAsync(default);
        Assert.Equal(first.OperationId, Text(status, "activeOperation"));
        Assert.Equal("preset", Text(status, "loadedMode"));
        Assert.Equal("1", Text(status, "queueLength"));
        var events = new List<Dictionary<string, object?>>();
        var following = server.Client.FollowAsync(first.OperationId, events.Add, default);
        release.SetResult();
        Assert.Equal("result", Text(await following, "type"));
        await server.Client.FollowAsync(second.OperationId, _ => { }, default);
        Assert.Equal(new[] { first.OperationId, second.OperationId }, calls.ToArray());
        Assert.Equal("succeeded", Text(await server.Client.JobStatusAsync(first.OperationId, default), "state"));
    }

    [Fact]
    public async Task Unfinished_speech_fails_its_job_and_the_next_job_can_succeed()
    {
        var calls = 0;
        await using var server = await RunningServer.Start((request, _, _) => Task.FromResult(
            ++calls == 1
                ? Program.Failure(request, new Bunyi.Core.Qwen.GenerationDidNotFinishException())
                : CliProtocol.Result(request, ("outputPath", "/next.wav"))));
        var failed = Request("generate.clone");
        await server.Client.ExecuteAsync(failed, true, _ => { }, default);
        var result = await server.Client.FollowAsync(failed.OperationId, _ => { }, default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, CliProtocol.Json));
        Assert.Equal("generation_did_not_finish", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(10, CliProtocol.ExitCode(result));
        Assert.False(result.ContainsKey("outputPath"));
        Assert.Equal("failed", Text(await server.Client.JobStatusAsync(failed.OperationId, default), "state"));
        var next = await server.Client.ExecuteAsync(Request(), false, _ => { }, default);
        Assert.Equal("result", Text(next, "type"));
    }

    [Fact]
    public async Task AttachedDisconnectCancelsButStopWaitsForActualWorkCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiesce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unloaded = false;
        await using var server = await RunningServer.Start(async (_, _, ct) =>
        {
            entered.SetResult();
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await quiesce.Task;
            ct.ThrowIfCancellationRequested();
            return [];
        }, () => { unloaded = true; return Task.CompletedTask; });
        using var clientCancellation = new CancellationTokenSource();
        var execute = server.Client.ExecuteAsync(Request(), false, _ => { }, clientCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clientCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execute);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = server.Client.StopAsync(default);
        await Task.Delay(100);
        Assert.False(stopping.IsCompleted);
        Assert.False(unloaded);
        quiesce.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(unloaded);
    }

    [Fact]
    public async Task QueuedCancellationDoesNotExecuteDispatcher()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var server = await RunningServer.Start(async (request, _, ct) => { Interlocked.Increment(ref calls); await block.Task.WaitAsync(ct); return CliProtocol.Result(request); });
        var first = Request();
        var second = Request();
        await server.Client.ExecuteAsync(first, true, _ => { }, default);
        await server.Client.ExecuteAsync(second, true, _ => { }, default);
        await server.Client.CancelAsync(second.OperationId, default);
        block.SetResult();
        await server.Client.FollowAsync(first.OperationId, _ => { }, default);
        var result = await server.Client.FollowAsync(second.OperationId, _ => { }, default);
        Assert.Equal("error", Text(result, "type"));
        Assert.Equal(5, CliProtocol.ExitCode(result));
        Assert.Equal(1, calls);
        Assert.Equal("cancelled", Text(await server.Client.JobStatusAsync(second.OperationId, default), "state"));
    }

    [Fact]
    public async Task UnavailableEndpointIsDistinctFromProtocolMismatch()
    {
        var endpoint = new ServerEndpoint(Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<ServerException>(() => new ServerClient(endpoint).StatusAsync(default));
        Assert.Equal("server_unavailable", error.Code);
        if (!OperatingSystem.IsWindows()) return;
        await using var pipe = new NamedPipeServerStream(endpoint.Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new ServerClient(endpoint).StatusAsync(default);
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await reader.ReadLineAsync();
        await pipe.WriteAsync("{\"protocolVersion\":999}\n"u8.ToArray());
        await pipe.FlushAsync();
        error = await Assert.ThrowsAsync<ServerException>(() => client);
        Assert.Equal("server_protocol_mismatch", error.Code);
    }

    [Fact]
    public async Task DispatcherCancellationEnvelopeBecomesCancelledJobState()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RunningServer.Start(async (request, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { return CliProtocol.Error(request, "cancelled", "The operation was cancelled.", 5); }
            return CliProtocol.Result(request);
        });
        var request = Request();
        await server.Client.ExecuteAsync(request, true, _ => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.Client.CancelAsync(request.OperationId, default);
        var terminal = await server.Client.FollowAsync(request.OperationId, _ => { }, default);
        Assert.Equal(5, CliProtocol.ExitCode(terminal));
        Assert.Equal("cancelled", Text(await server.Client.JobStatusAsync(request.OperationId, default), "state"));
    }

    [Fact]
    public async Task FullQueueRejectsWithoutExecutingExtraJob()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await RunningServer.Start(async (request, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return CliProtocol.Result(request);
        });
        await server.Client.ExecuteAsync(Request(), true, _ => { }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 32; index++)
            Assert.Equal("accepted", Text(await server.Client.ExecuteAsync(Request(), true, _ => { }, default), "type"));
        var refused = await server.Client.ExecuteAsync(Request(), true, _ => { }, default);
        Assert.Equal("error", Text(refused, "type"));
        Assert.Equal(4, CliProtocol.ExitCode(refused));
        Assert.Equal("32", Text(await server.Client.StatusAsync(default), "queueLength"));
    }

    [Fact]
    public async Task StatusHonorsCancellationAfterSuccessfulHandshake()
    {
        if (!OperatingSystem.IsWindows()) return;
        var endpoint = new ServerEndpoint(Guid.NewGuid().ToString("N"));
        await using var pipe = new NamedPipeServerStream(endpoint.Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var cancellation = new CancellationTokenSource();
        var client = new ServerClient(endpoint).StatusAsync(cancellation.Token);
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await reader.ReadLineAsync();
        await pipe.WriteAsync("{\"protocolVersion\":1}\n"u8.ToArray());
        await pipe.FlushAsync();
        await reader.ReadLineAsync();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task LostAcceptedConnectionNeverBecomesUnavailableRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var endpoint = new ServerEndpoint(Guid.NewGuid().ToString("N"));
        var pipe = new NamedPipeServerStream(endpoint.Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new ServerClient(endpoint).ExecuteAsync(Request(), true, _ => { }, default);
        await pipe.WaitForConnectionAsync();
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await reader.ReadLineAsync();
        await pipe.WriteAsync("{\"protocolVersion\":1}\n"u8.ToArray());
        await pipe.FlushAsync();
        await reader.ReadLineAsync();
        await pipe.DisposeAsync();
        var error = await Assert.ThrowsAsync<ServerException>(() => client);
        Assert.Equal("server_disconnected", error.Code);
    }

    [Fact]
    public async Task LinuxRepairsStaleSocketAndRejectsPublicDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var endpoint = new ServerEndpoint(Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(endpoint.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (var stale = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
            stale.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(endpoint.SocketPath));
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new ServerHost(endpoint).RunAsync((request, _, _) => Task.FromResult(CliProtocol.Result(request)), () => null, () => Task.CompletedTask, cancellation.Token, () => ready.SetResult());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await new ServerClient(endpoint).StopAsync(default);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        File.SetUnixFileMode(endpoint.DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        var error = await Assert.ThrowsAsync<ServerException>(() => new ServerHost(endpoint).RunAsync((request, _, _) => Task.FromResult(CliProtocol.Result(request)), () => null, () => Task.CompletedTask, default));
        Assert.Equal("server_unavailable", error.Code);
        Directory.Delete(endpoint.DirectoryPath, recursive: true);
    }

    [Fact]
    public async Task SecondHostCannotTakeOverLiveEndpoint()
    {
        await using var server = await RunningServer.Start((request, _, _) => Task.FromResult(CliProtocol.Result(request)));
        var second = new ServerHost(server.Endpoint);
        var error = await Assert.ThrowsAsync<ServerException>(() => second.RunAsync((request, _, _) => Task.FromResult(CliProtocol.Result(request)), () => null, () => Task.CompletedTask, default));
        Assert.Equal("bunyi_busy", error.Code);
        Assert.Equal("result", Text(await server.Client.StatusAsync(default), "type"));
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        public ServerEndpoint Endpoint { get; } = new(Guid.NewGuid().ToString("N"));
        public ServerClient Client => new(Endpoint);
        private readonly CancellationTokenSource cancellation = new();
        private Task run = Task.CompletedTask;
        public static async Task<RunningServer> Start(Func<CommandRequest, Action<Dictionary<string, object?>>, CancellationToken, Task<Dictionary<string, object?>>> dispatch, Func<Task>? unload = null)
        {
            var server = new RunningServer();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.run = new ServerHost(server.Endpoint).RunAsync(dispatch, () => new { loadedMode = "preset" }, unload ?? (() => Task.CompletedTask), server.cancellation.Token, () => ready.SetResult());
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return server;
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Dispose();
            if (Directory.Exists(Endpoint.DirectoryPath)) Directory.Delete(Endpoint.DirectoryPath, recursive: true);
        }
    }
}
