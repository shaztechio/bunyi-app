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
using System.Text.Json;
using Bunyi.Core.Audio;
using Xunit;

namespace Bunyi.Cli.Tests;

/// <summary>Exercises the real process and stdout boundary; no models or network are used.</summary>
public sealed class CommandProcessTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("bunyi-cli-process-").FullName;

    [Fact]
    public async Task InvalidPlaybackReturnsFailureWithoutModelOrHistoryWrites()
    {
        var path = Path.Combine(root, "not-audio.wav");
        await File.WriteAllTextAsync(path, "This is not audio.");
        var played = await Run("play", path);
        Assert.Equal(10, played.Exit);
        Assert.Equal("playback_failed", played.Result.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "Models")));
        Assert.False(Directory.Exists(Path.Combine(root, "Outputs")));
        Assert.Equal("This is not audio.", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConfigChangesSurviveProcessExitAndInvalidSourceFails()
    {
        var saved = await Run("config", "set", "unloadOnModeSwitch", "false");
        Assert.Equal(0, saved.Exit);
        var read = await Run("config", "get", "unloadOnModeSwitch");
        Assert.Equal(0, read.Exit);
        Assert.False(read.Result.GetProperty("value").GetBoolean());
        var invalid = await Run("config", "set", "modelSource.preset", "../../outside");
        Assert.Equal(2, invalid.Exit);
    }

    [Fact]
    public async Task ModelVerificationRejectsMissingModelWithoutDownload()
    {
        var read = await Run("models", "verify", "--mode", "preset", "--one-shot");
        Assert.Equal(3, read.Exit);
        Assert.Equal("model_incomplete", read.Result.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(root, "Models", "models")));
    }

    [Fact]
    public async Task ConfigurationCanRepairADisappearedModelsFolder()
    {
        var removed = Path.Combine(root, "removable-drive"); Directory.CreateDirectory(removed);
        Assert.Equal(0, (await Run("config", "set", "modelsFolder", removed)).Exit);
        Directory.Delete(removed);
        var replacement = Path.Combine(root, "new-models"); Directory.CreateDirectory(replacement);
        Assert.Equal(0, (await Run("config", "set", "modelsFolder", replacement)).Exit);
        Assert.Equal(replacement, (await Run("config", "get", "modelsFolder")).Result.GetProperty("value").GetString());
    }

    [Fact]
    public async Task HistoryUsesEmbeddedMetadataAndRejectsExternalDeletion()
    {
        var path = Path.Combine(root, "Outputs", "example.wav");
        WavWriter.Write(path, new short[240]);
        Assert.True(WavMetadata.TryWrite(path, new OutputMetadata { Mode = "Preset voice", Text = "Hello test", Language = "english", ModelRepo = "test/model", AppVersion = "1.0", Created = DateTimeOffset.UtcNow }));
        var listed = await Run("history", "list");
        Assert.Equal(0, listed.Exit);
        Assert.Equal("Hello test", listed.Result.GetProperty("outputs")[0].GetProperty("metadata").GetProperty("text").GetString());
        var shown = await Run("history", "show", path);
        Assert.Equal(path, shown.Result.GetProperty("output").GetProperty("path").GetString());
        var outside = Path.Combine(root, "outside.wav");
        File.Copy(path, outside);
        var deletion = await Run("history", "remove", outside);
        Assert.Equal(3, deletion.Exit);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task BackupRestoresModelsAndSkipsExistingRepositories()
    {
        var folder = Path.Combine(root, "Models", "models", "example", "tiny");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "config.json"), "fixture");
        var archive = Path.Combine(root, "backup.zip");
        var backup = await Run("backup", "create", archive, "--one-shot");
        Assert.Equal(0, backup.Exit);
        var destination = Path.Combine(root, "restored"); Directory.CreateDirectory(destination);
        Assert.Equal(0, (await Run("config", "set", "modelsFolder", destination)).Exit);
        var restore = await Run("backup", "restore", archive, "--one-shot");
        Assert.Equal(0, restore.Exit);
        Assert.Equal("fixture", await File.ReadAllTextAsync(Path.Combine(destination, "models", "example", "tiny", "config.json")));
        var second = await Run("backup", "restore", archive, "--one-shot");
        Assert.Equal("example/tiny", second.Result.GetProperty("skipped")[0].GetString());
    }

    [Theory]
    [InlineData("voices", "list")]
    [InlineData("models", "list")]
    [InlineData("models", "status")]
    [InlineData("logs", "path")]
    [InlineData("logs", "tail")]
    [InlineData("config", "list")]
    public async Task ReadCommandsReturnSingleJsonObject(string group, string action)
    {
        var read = await Run(group, action, "--one-shot");
        Assert.Equal(0, read.Exit);
        Assert.True(read.Result.GetProperty("ok").GetBoolean());
    }

    private async Task<(int Exit, JsonElement Result)> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--json");
        start.Environment["BUNYI_DATA_DIR"] = root;
        start.Environment.Remove("BUNYI_CONFIG_FILE");
        start.Environment.Remove("BUNYI_SERVER_BACKGROUND");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var text = await stdout;
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var result = JsonDocument.Parse(text);
        Assert.True(result.RootElement.TryGetProperty("operationId", out _), await stderr);
        return (process.ExitCode, result.RootElement.Clone());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
