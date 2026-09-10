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
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Bunyi.Cli.Protocol;

namespace Bunyi.Cli.Server;

public sealed class ServerHost(ServerEndpoint? endpoint = null)
{
    private readonly ServerEndpoint endpoint = endpoint ?? new();
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly Channel<Job> queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private long nextConnection;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private Job? active;
    private int queueLength;

    public async Task RunAsync(
        Func<CommandRequest, Action<Dictionary<string, object?>>, CancellationToken, Task<Dictionary<string, object?>>> dispatch,
        Func<object?> runtimeStatus, Func<Task> unload, CancellationToken ct, Action? ready = null)
    {
        endpoint.PrepareDirectory();
        FileStream lease;
        try { lease = new FileStream(endpoint.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ServerException("bunyi_busy", "A Bunyi server is already running or starting."); }
        using (lease)
        using (var accepting = new CancellationTokenSource())
        using (ct.Register(() => stop.TrySetResult()))
        {
            var worker = WorkAsync(dispatch);
            var listener = AcceptAsync(runtimeStatus, accepting.Token, ready);
            var first = await Task.WhenAny(stop.Task, listener);
            if (first == listener && listener.IsFaulted) stop.TrySetResult();
            accepting.Cancel();
            queue.Writer.TryComplete();
            foreach (var job in jobs.Values) job.Cancel();
            try
            {
                await worker;
                // The supplied unload must wait for native inference to quiesce.
                await unload();
                stopped.TrySetResult();
            }
            catch (Exception ex) { stopped.TrySetException(ex); throw; }
            finally
            {
                try { await listener; } catch (OperationCanceledException) { }
                await Task.WhenAll(connections.Values);
                if (!OperatingSystem.IsWindows() && File.Exists(endpoint.SocketPath)) File.Delete(endpoint.SocketPath);
                foreach (var job in jobs.Values) job.Dispose();
            }
        }
    }

    private async Task AcceptAsync(Func<object?> status, CancellationToken ct, Action? ready)
    {
        if (OperatingSystem.IsWindows())
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(endpoint.Name, PipeDirection.InOut, 32, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                ready?.Invoke();
                ready = null;
                try { await pipe.WaitForConnectionAsync(ct); }
                catch { pipe.Dispose(); throw; }
                TrackConnection(ServeAsync(pipe, status, ct));
            }
        }
        else
        {
            // Only the process holding the lifetime lease may remove a stale socket.
            if (new FileInfo(endpoint.SocketPath).LinkTarget is not null)
                throw new ServerException("server_unavailable", "The server socket path cannot be a symbolic link.");
            if (File.Exists(endpoint.SocketPath)) File.Delete(endpoint.SocketPath);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(endpoint.SocketPath));
            File.SetUnixFileMode(endpoint.SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(32);
            ready?.Invoke();
            while (!ct.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(ct);
                try { ServerEndpoint.VerifyPeer(socket); }
                catch { socket.Dispose(); continue; }
                TrackConnection(ServeAsync(new NetworkStream(socket, ownsSocket: true), status, ct));
            }
        }
    }

    private void TrackConnection(Task task)
    {
        var id = Interlocked.Increment(ref nextConnection);
        connections[id] = task;
        _ = task.ContinueWith(completed => { connections.TryRemove(id, out _); _ = completed.Exception; }, TaskScheduler.Default);
    }

    private async Task ServeAsync(Stream stream, Func<object?> runtimeStatus, CancellationToken accepting)
    {
        await using (stream)
        {
            try
            {
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(accepting);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var hello = JsonSerializer.Deserialize<WireRequest>(await ServerWire.ReadAsync(stream, handshakeTimeout.Token) ?? "null", ServerWire.Json);
                if (hello?.Action != "hello" || hello.ProtocolVersion != ServerWire.Version)
                {
                    await ServerWire.WriteAsync(stream, ServerWire.Error("server.handshake", null, "server_protocol_mismatch", "Client and server protocol versions differ."), handshakeTimeout.Token);
                    return;
                }
                await ServerWire.WriteAsync(stream, new { protocolVersion = ServerWire.Version }, handshakeTimeout.Token);
                var request = JsonSerializer.Deserialize<WireRequest>(await ServerWire.ReadAsync(stream, handshakeTimeout.Token) ?? "null", ServerWire.Json)
                    ?? throw new ServerException("invalid_arguments", "Missing server request.");
                if (request.ProtocolVersion != ServerWire.Version) throw new ServerException("server_protocol_mismatch", "Client and server protocol versions differ.");
                Dictionary<string, object?> response;
                switch (request.Action)
                {
                    case "status":
                        response = ServerWire.Result("server.status");
                        response["processId"] = Environment.ProcessId;
                        response["protocolVersion"] = ServerWire.Version;
                        var runtime = JsonSerializer.SerializeToElement(runtimeStatus(), ServerWire.Json);
                        if (runtime.ValueKind == JsonValueKind.Object)
                            foreach (var property in runtime.EnumerateObject()) response[property.Name] = property.Value.Clone();
                        response["activeOperation"] = active?.Request.OperationId;
                        response["queueLength"] = Volatile.Read(ref queueLength);
                        response["uptimeSeconds"] = (DateTimeOffset.UtcNow - started).TotalSeconds;
                        response["stopping"] = stop.Task.IsCompleted;
                        break;
                    case "stop":
                        stop.TrySetResult();
                        await stopped.Task;
                        response = ServerWire.Result("server.stop");
                        break;
                    case "execute":
                        if (stop.Task.IsCompleted) throw new ServerException("bunyi_busy", "The server is stopping.");
                        var command = request.Request ?? throw new ServerException("invalid_arguments", "Missing command.");
                        if (string.IsNullOrWhiteSpace(command.OperationId) || command.OperationId.Length > 128 || command.Arguments is null)
                            throw new ServerException("invalid_arguments", "Invalid operation identifier or arguments.");
                        // Bound retained terminal snapshots; active work is never evicted.
                        foreach (var old in jobs.Values.Where(j => j.IsComplete).OrderBy(j => j.Created).Take(Math.Max(0, jobs.Count - 255)))
                            if (jobs.TryRemove(old.Request.OperationId, out _)) old.Dispose();
                        var job = new Job(command);
                        if (!jobs.TryAdd(command.OperationId, job)) { job.Dispose(); throw new ServerException("invalid_arguments", "The operation identifier already exists."); }
                        Interlocked.Increment(ref queueLength);
                        if (!queue.Writer.TryWrite(job))
                        {
                            Interlocked.Decrement(ref queueLength);
                            jobs.TryRemove(command.OperationId, out _);
                            job.Dispose();
                            throw new ServerException("bunyi_busy", "The server job queue is full.");
                        }
                        if (!request.Detached) { await FollowAsync(stream, job, cancelOnDisconnect: true); return; }
                        response = ServerWire.Result(command.Operation, command.OperationId, "accepted");
                        break;
                    case "job.status":
                        response = FindJob(request.JobId).Snapshot();
                        break;
                    case "job.cancel":
                        var cancelledJob = FindJob(request.JobId);
                        cancelledJob.Cancel();
                        response = ServerWire.Result("jobs.cancel", request.JobId);
                        break;
                    case "job.follow":
                        await FollowAsync(stream, FindJob(request.JobId), cancelOnDisconnect: false);
                        return;
                    default: throw new ServerException("invalid_arguments", "Unknown server action.");
                }
                await ServerWire.WriteAsync(stream, response, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
            catch (Exception ex)
            {
                try { await ServerWire.WriteAsync(stream, ServerWire.Error("server.request", null, ex is ServerException sx ? sx.Code : "invalid_arguments", ex.Message), CancellationToken.None); }
                catch (IOException) { }
            }
        }
    }

    private Job FindJob(string? id) => id is not null && jobs.TryGetValue(id, out var job)
        ? job : throw new ServerException("job_not_found", "That job is not retained by this server.");

    private async Task WorkAsync(Func<CommandRequest, Action<Dictionary<string, object?>>, CancellationToken, Task<Dictionary<string, object?>>> dispatch)
    {
        await foreach (var job in queue.Reader.ReadAllAsync())
        {
            Interlocked.Decrement(ref queueLength);
            active = job;
            job.State = "running";
            try
            {
                job.Token.ThrowIfCancellationRequested();
                var result = await dispatch(job.Request, job.Publish, job.Token);
                job.Finish(result, CliProtocol.ExitCode(result) == 5 ? "cancelled" : ServerWire.Text(result, "type") == "error" ? "failed" : "succeeded");
            }
            catch (OperationCanceledException) { job.Finish(CliProtocol.Error(job.Request, "cancelled", "The operation was cancelled.", 5), "cancelled"); }
            catch (Exception ex)
            {
                job.Finish(CliProtocol.Error(job.Request, ex is CliException cx ? cx.Code : "operation_failed", ex.Message, ex is CliException error ? error.ExitCode : 10), "failed");
            }
            finally { active = null; }
        }
    }

    private static async Task FollowAsync(Stream stream, Job job, bool cancelOnDisconnect)
    {
        using var watching = new CancellationTokenSource();
        var reader = job.Subscribe();
        var disconnected = ServerWire.ReadAsync(stream, watching.Token);
        try
        {
            while (true)
            {
                var next = reader.Reader.ReadAsync(watching.Token).AsTask();
                if (await Task.WhenAny(next, disconnected) == disconnected) break;
                var message = await next;
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(watching.Token);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                await ServerWire.WriteAsync(stream, message, writeTimeout.Token);
                if (ServerWire.Text(message, "type") is "result" or "error") return;
            }
        }
        finally
        {
            if (cancelOnDisconnect && !job.IsComplete) job.Cancel();
            watching.Cancel();
            job.Unsubscribe(reader);
            try { await disconnected; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    private sealed class Job(CommandRequest request) : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation = new();
        private readonly List<Channel<Dictionary<string, object?>>> subscribers = [];
        private Dictionary<string, object?> latest = CliProtocol.Event(request, "queued");
        public CommandRequest Request { get; } = request;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "queued";
        public bool IsComplete { get; private set; }
        public CancellationToken Token => cancellation.Token;
        public void Publish(Dictionary<string, object?> message)
        {
            lock (gate) { if (IsComplete) return; latest = message; foreach (var channel in subscribers) channel.Writer.TryWrite(message); }
        }
        public void Finish(Dictionary<string, object?> result, string state)
        {
            lock (gate) { State = state; Publish(result); IsComplete = true; }
        }
        public void Cancel()
        {
            lock (gate)
            {
                if (IsComplete) return;
                Publish(CliProtocol.Event(Request, "stopping"));
                cancellation.Cancel();
            }
        }
        public Dictionary<string, object?> Snapshot()
        {
            lock (gate) { var result = ServerWire.Result("jobs.status", Request.OperationId); result["state"] = State; result["latestEvent"] = latest; return result; }
        }
        public Channel<Dictionary<string, object?>> Subscribe()
        {
            // A stalled progress consumer retains only the latest snapshots, including terminal state.
            var channel = Channel.CreateBounded<Dictionary<string, object?>>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
            lock (gate) { channel.Writer.TryWrite(latest); subscribers.Add(channel); }
            return channel;
        }
        public void Unsubscribe(Channel<Dictionary<string, object?>> channel) { lock (gate) subscribers.Remove(channel); }
        public void Dispose() => cancellation.Dispose();
    }
}
