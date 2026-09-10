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
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Bunyi.Cli.Tests;

public sealed class ServerProcessTests
{
    [Fact]
    public async Task LinuxSupervisorSigtermProducesGracefulTerminalResult()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "bunyi-server-sigterm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("server");
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--jsonl");
        info.Environment["BUNYI_DATA_DIR"] = directory;
        info.Environment["BUNYI_CONFIG_FILE"] = Path.Combine(directory, "settings.json");
        info.Environment.Remove("BUNYI_SERVER_BACKGROUND");
        using var process = Process.Start(info)!;
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("ready", JsonDocument.Parse(ready!).RootElement.GetProperty("type").GetString());
            Assert.Equal(0, SendSignal(process.Id, 15));
            var terminal = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, process.ExitCode);
            var result = JsonDocument.Parse(terminal!).RootElement;
            Assert.Equal("result", result.GetProperty("type").GetString());
            Assert.True(result.GetProperty("stopped").GetBoolean());
            Assert.Null(await process.StandardOutput.ReadLineAsync());
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            Directory.Delete(directory, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundStartReleasesCapturedHandlesAndRetainsSameServer(bool apphost)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bunyi-server-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var assembly = typeof(Program).Assembly.Location;
        async Task<JsonElement> Run(string command)
        {
            var executable = apphost ? Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "bunyi.exe" : "bunyi") : "dotnet";
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (!apphost) info.ArgumentList.Add(assembly);
            info.ArgumentList.Add("server");
            info.ArgumentList.Add(command);
            info.ArgumentList.Add("--json");
            info.Environment["BUNYI_DATA_DIR"] = directory;
            info.Environment["BUNYI_CONFIG_FILE"] = Path.Combine(directory, "settings.json");
            info.Environment.Remove("BUNYI_SERVER_BACKGROUND");
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            // A background child retaining ANY inherited agent pipe leaves these pending
            // even after the startup process itself has exited.
            var output = await stdout.WaitAsync(TimeSpan.FromSeconds(3));
            var error = await stderr.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(process.ExitCode == 0, output + error);
            return JsonDocument.Parse(output).RootElement.Clone();
        }
        try
        {
            var started = await Run("start");
            Assert.Equal("server.start", started.GetProperty("operation").GetString());
            var repeated = await Run("start");
            Assert.Equal(started.GetProperty("processId").GetInt32(), repeated.GetProperty("processId").GetInt32());
            var status = await Run("status");
            Assert.Equal(started.GetProperty("processId").GetInt32(), status.GetProperty("processId").GetInt32());
        }
        finally
        {
            await Run("stop");
            Directory.Delete(directory, recursive: true);
        }
    }
}
