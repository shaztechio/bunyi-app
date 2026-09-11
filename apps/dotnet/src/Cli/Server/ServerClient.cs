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
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Bunyi.Cli.Protocol;

namespace Bunyi.Cli.Server;

public sealed class ServerClient(ServerEndpoint? endpoint = null)
{
    private readonly ServerEndpoint endpoint = endpoint ?? new();

    public Task<Dictionary<string, object?>> ExecuteAsync(CommandRequest request, bool detach, Action<Dictionary<string, object?>> emit, CancellationToken ct) =>
        SendAsync(new(ServerWire.Version, "execute", request, Detached: detach), emit, ct);
    public async Task<Dictionary<string, object?>> StatusAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await SendAsync(new(ServerWire.Version, "status"), null, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerException("server_unavailable", "The server did not answer its status request within five seconds.");
        }
    }
    public Task<Dictionary<string, object?>> JobStatusAsync(string id, CancellationToken ct) => SendAsync(new(ServerWire.Version, "job.status", JobId: id), null, ct);
    public Task<Dictionary<string, object?>> FollowAsync(string id, Action<Dictionary<string, object?>> emit, CancellationToken ct) => SendAsync(new(ServerWire.Version, "job.follow", JobId: id), emit, ct);
    public Task<Dictionary<string, object?>> CancelAsync(string id, CancellationToken ct) => SendAsync(new(ServerWire.Version, "job.cancel", JobId: id), null, ct);
    public Task<Dictionary<string, object?>> StopAsync(CancellationToken ct) => SendAsync(new(ServerWire.Version, "stop"), null, ct);

    public async Task<Dictionary<string, object?>> StartAsync(CancellationToken ct)
    {
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readiness.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await StartCoreAsync(readiness.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerException("server_unavailable", "The server did not become ready within 30 seconds.");
        }
    }

    private async Task<Dictionary<string, object?>> StartCoreAsync(CancellationToken ct)
    {
        static Dictionary<string, object?> Started(Dictionary<string, object?> status) { status["operation"] = "server.start"; return status; }
        try { return Started(await StatusAsync(ct)); }
        catch (ServerException ex) when (ex.Code == "server_unavailable") { }
        using var process = BackgroundProcess.Start(CreateStartInfo());
        // Drain redirected handles in this process; the child itself never writes runtime logs
        // to these streams. This also keeps host tools from waiting on inherited stdout.
        if (!OperatingSystem.IsWindows())
        {
            _ = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
        }
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(30))
        {
            ct.ThrowIfCancellationRequested();
            try { return Started(await StatusAsync(ct)); }
            catch (ServerException ex) when (ex.Code == "server_unavailable") { }
            if (process.HasExited) throw new ServerException("server_unavailable", $"The server exited before becoming ready (exit {process.ExitCode}).");
            await Task.Delay(100, ct);
        }
        throw new ServerException("server_unavailable", "The server did not become ready within 30 seconds.");
    }

    public static ProcessStartInfo CreateStartInfo()
    {
        var executable = Environment.ProcessPath ?? throw new ServerException("server_unavailable", "The executable path is unavailable.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        // A framework-dependent `dotnet bunyi.dll` needs the assembly argument;
        // a published apphost already knows its entry assembly.
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new ServerException("server_unavailable", "The CLI assembly path is unavailable."));
        info.ArgumentList.Add("server");
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--json");
        info.Environment["BUNYI_SERVER_BACKGROUND"] = "1";
        return info;
    }

    private async Task<Dictionary<string, object?>> SendAsync(WireRequest request, Action<Dictionary<string, object?>>? emit, CancellationToken ct)
    {
        Stream stream;
        try { stream = await endpoint.ConnectAsync(ct); }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            throw new ServerException("server_unavailable", "No Bunyi server is available for this user. Run bunyi server start first.");
        }
        await using (stream)
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await ServerWire.WriteAsync(stream, new WireRequest(ServerWire.Version, "hello"), handshake.Token);
                var hello = await ServerWire.ReadAsync(stream, handshake.Token);
                if (hello is null) throw new ServerException("server_protocol_mismatch", "The endpoint did not complete the Bunyi protocol handshake.");
                using var document = JsonDocument.Parse(hello);
                if (!document.RootElement.TryGetProperty("protocolVersion", out var version) || !version.TryGetInt32(out var number) || number != ServerWire.Version)
                    throw new ServerException("server_protocol_mismatch", "Client and server protocol versions differ. Restart the server with this version of Bunyi.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ServerException("server_protocol_mismatch", "The endpoint did not complete the Bunyi protocol handshake."); }
            catch (JsonException) { throw new ServerException("server_protocol_mismatch", "The endpoint sent an invalid Bunyi protocol handshake."); }
            catch (IOException) { throw new ServerException("server_protocol_mismatch", "The endpoint closed before completing the Bunyi protocol handshake."); }
            try
            {
                // From this point connection errors must NEVER trigger one-shot retry:
                // the server may have accepted an operation even if its reply was lost.
                await ServerWire.WriteAsync(stream, request, ct);
                while (true)
                {
                    var line = await ServerWire.ReadAsync(stream, ct) ?? throw new ServerException("server_disconnected", "The server disconnected. The operation's success is unknown; inspect its job before retrying.");
                    var message = JsonSerializer.Deserialize<Dictionary<string, object?>>(line, ServerWire.Json) ?? throw new IOException("Empty server response.");
                    if (ServerWire.Text(message, "type") is "result" or "error" or "accepted") return message;
                    emit?.Invoke(message);
                }
            }
            catch (IOException) { throw new ServerException("server_disconnected", "The server connection was lost. The operation's success is unknown; inspect its job before retrying."); }
        }
    }
}
