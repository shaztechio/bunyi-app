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

using System.Text.Json;
using Bunyi.Cli.Commands;
using Bunyi.Cli.Protocol;
using Xunit;

namespace Bunyi.Cli.Tests;

public sealed class CommandTests
{
    [Theory]
    [InlineData("generate preset --text hello --voice wrong")]
    [InlineData("generate design --text hello --speaker Ryan")]
    [InlineData("generate clone --text hello --style happy")]
    [InlineData("models download --all --mode preset")]
    [InlineData("models remove --all")]
    [InlineData("models download --mode invalid")]
    [InlineData("server preload")]
    [InlineData("server preload --mode preset --one-shot")]
    [InlineData("server unload --one-shot")]
    [InlineData("server run --one-shot")]
    [InlineData("server start --one-shot")]
    [InlineData("server status --one-shot")]
    [InlineData("server stop --one-shot")]
    [InlineData("jobs status sample-id --one-shot")]
    [InlineData("jobs follow sample-id --one-shot")]
    [InlineData("jobs cancel sample-id --one-shot")]
    [InlineData("config set modelsFolder")]
    [InlineData("--json --jsonl speakers")]
    [InlineData("speakers --one-shot --require-server")]
    [InlineData("speakers --one-shot --detach")]
    [InlineData("speakers --json --json")]
    public void RejectsInvalidCommands(string command) => Assert.Equal(2, Assert.Throws<CliException>(() => CommandParser.Parse(command.Split(' '))).ExitCode);

    [Theory]
    [InlineData("generate preset", 3)]
    [InlineData("generate preset --text hello --stdin", 2)]
    [InlineData("generate design --text hello", 3)]
    [InlineData("generate clone --text hello --reference missing.wav --transcript hello", 3)]
    [InlineData("generate clone --text hello --reference missing.wav --auto-transcribe --transcript hello", 2)]
    [InlineData("generate preset --text hello --language madeup", 2)]
    [InlineData("transcribe definitely-does-not-exist.wav", 3)]
    [InlineData("voices add --name example --reference missing.wav", 3)]
    public async Task InputFailuresReturnOneStructuredObjectBeforeRuntimeWork(string command, int exit)
    {
        var stdout = new StringWriter(); var stderr = new StringWriter();
        var actual = await Program.RunAsync([.. command.Split(' '), "--json"], TextReader.Null, stdout, stderr);
        Assert.Equal(exit, actual);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("error", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task StdinIsResolvedBeforeServerSerialization()
    {
        var request = CommandParser.Parse(["generate", "preset", "--stdin"]);
        await CommandParser.NormalizeInputsAsync(request, new StringReader("Hello from stdin\n"), default);
        Assert.Equal("Hello from stdin\n", request.Get("text"));
        Assert.False(request.Has("stdin"));
    }

    [Fact]
    public async Task TextFilesAreReadAndReferencePathsMadeAbsolute()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "Read my text");
            var request = CommandParser.Parse(["generate", "clone", "--text-file", path, "--reference", path, "--transcript", "Reference words"]);
            await CommandParser.NormalizeInputsAsync(request, TextReader.Null, default);
            Assert.Equal("Read my text", request.Get("text"));
            Assert.True(Path.IsPathFullyQualified(request.Get("reference")!));
            Assert.False(request.Has("text-file"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void JsonlIsFlushedAndNoEventsFollowTerminal()
    {
        var stdout = new StringWriter(); var stderr = new StringWriter();
        var output = new CliOutput(stdout, stderr, false, true);
        var request = CommandParser.Parse(["models", "download", "--all"]);
        output.Event(CliProtocol.Event(request, "downloading", ("bytesCompleted", 123L), ("bytesTotal", null)));
        output.Finish(CliProtocol.Result(request));
        output.Event(CliProtocol.Event(request, "downloading"));
        output.Finish(CliProtocol.Result(request));
        var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var progress = JsonDocument.Parse(lines[0]);
        Assert.Equal(JsonValueKind.Null, progress.RootElement.GetProperty("bytesTotal").ValueKind);
        using var final = JsonDocument.Parse(lines[1]);
        Assert.Equal("result", final.RootElement.GetProperty("type").GetString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void JsonSuppressesProgress()
    {
        var stdout = new StringWriter(); var output = new CliOutput(stdout, new StringWriter(), true, false);
        var request = CommandParser.Parse(["speakers"]);
        output.Event(CliProtocol.Event(request, "loading"));
        output.Finish(CliProtocol.Result(request));
        Assert.Single(stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void RepeatedProgressIsThrottledButPhaseChangesAndTerminalAreImmediate()
    {
        var request = CommandParser.Parse(["models", "download", "--all"]);
        var events = new List<Dictionary<string, object?>>();
        var progress = new ProgressEmitter(events.Add, new FrozenTime());
        for (var i = 0; i < 30; i++) progress.Report(CliProtocol.Event(request, "downloading", ("bytesCompleted", i)));
        progress.Report(CliProtocol.Event(request, "verifying"));
        progress.Report(CliProtocol.Result(request));
        Assert.Equal(3, events.Count);
        Assert.Equal("downloading", events[0]["type"]);
        Assert.Equal("verifying", events[1]["type"]);
        Assert.Equal("result", events[2]["type"]);
    }

    [Fact]
    public void ErrorsRedactCredentialsAndTokens()
    {
        var redacted = CliLog.Redact("Failed https://user:password@example.com/file?token=secret Authorization: Bearer abc123");
        Assert.DoesNotContain("password", redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("abc123", redacted);
    }

    [Fact]
    public void Checking_and_finalizing_are_immediate_nonterminal_jsonl_events()
    {
        var request = CommandParser.Parse(["generate", "preset", "--text", "Hello"]);
        var stdout = new StringWriter();
        var output = new CliOutput(stdout, new StringWriter(), false, true);
        var emitter = new ProgressEmitter(output.Event, new FrozenTime());
        foreach (var phase in new[] { "checking", "loading", "generating", "finalizing" })
            emitter.Report(CliProtocol.Event(request, phase));
        output.Finish(CliProtocol.Result(request));
        var types = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.GetProperty("type").GetString();
            });
        Assert.Equal(new[] { "checking", "loading", "generating", "finalizing", "result" }, types);
    }

    [Theory]
    [InlineData("voices list")]
    [InlineData("history list")]
    [InlineData("models status")]
    [InlineData("models list")]
    [InlineData("config list")]
    [InlineData("logs path")]
    [InlineData("logs tail --lines 5")]
    [InlineData("doctor --mode preset")]
    [InlineData("backup create backup.zip")]
    [InlineData("backup restore backup.zip")]
    [InlineData("server run")]
    [InlineData("server start")]
    [InlineData("server status")]
    [InlineData("server stop")]
    [InlineData("jobs status sample-id")]
    [InlineData("jobs follow sample-id")]
    [InlineData("jobs cancel sample-id")]
    public void AllCommandGroupsHaveStrictSyntax(string command) => Assert.NotNull(CommandParser.Parse(command.Split(' ')));

    private sealed class FrozenTime : TimeProvider { public override long GetTimestamp() => 0; }
}
