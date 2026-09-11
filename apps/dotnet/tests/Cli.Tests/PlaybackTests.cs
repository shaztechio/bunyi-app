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
using Bunyi.Core.Audio;
using Xunit;

namespace Bunyi.Cli.Tests;

public sealed class PlaybackTests : IDisposable
{
    private readonly string path = Path.GetTempFileName();

    [Theory]
    [InlineData("play")]
    [InlineData("play a.wav b.wav")]
    [InlineData("play a.wav --detach")]
    [InlineData("play a.wav --require-server")]
    [InlineData("play a.wav --speaker Ryan")]
    public void InvalidSyntaxFailsBeforePlayback(string args) =>
        Assert.Throws<Bunyi.Cli.Protocol.CliException>(() => CommandParser.Parse(args.Split(' ')));

    [Fact]
    public async Task MissingFileNeverStartsPlayback()
    {
        var fake = new FakePlayback();
        var output = new StringWriter();
        Assert.Equal(3, await Program.RunAsync(["play", path + ".missing", "--json"], TextReader.Null, output, new StringWriter(), playback: fake));
        Assert.False(fake.Started.Task.IsCompleted);
        Assert.Contains("missing_input", output.ToString());
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--jsonl")]
    public async Task WaitsForCompletionAndCleanupBeforeSuccess(string format)
    {
        var fake = new FakePlayback();
        var output = new StringWriter();
        var request = CommandParser.Parse(["play", path, "--one-shot"]);
        Assert.False(CommandParser.ServerCapable(request));
        var task = Program.RunAsync(["play", path, format], TextReader.Null, output, new StringWriter(), playback: fake);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        fake.Complete.TrySetResult();
        Assert.Equal(0, await task);
        Assert.True(fake.Cleaned);
        Assert.Equal(Path.GetFullPath(path), fake.Path);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(format == "--json" ? 1 : 2, lines.Length);
        using var final = JsonDocument.Parse(lines[^1]);
        Assert.True(final.RootElement.GetProperty("played").GetBoolean());
        Assert.Equal("play", final.RootElement.GetProperty("operation").GetString());
        Assert.Equal(Path.GetFullPath(path), final.RootElement.GetProperty("inputPath").GetString());
        Assert.Equal(2, final.RootElement.GetProperty("durationSeconds").GetDouble());
        if (format == "--jsonl")
        {
            using var progress = JsonDocument.Parse(lines[0]);
            Assert.Equal("playing", progress.RootElement.GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task CancellationWaitsForCleanupAndReturnsOnlyAnError()
    {
        var fake = new FakePlayback();
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter();
        var task = Program.RunAsync(["play", path, "--json"], TextReader.Null, output, new StringWriter(), cancellation.Token, fake);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(5, await task);
        Assert.True(fake.Cleaned);
        using var final = JsonDocument.Parse(output.ToString());
        Assert.False(final.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("cancelled", final.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AudioFailuresAreStructuredAndRedacted()
    {
        var fake = new FakePlayback { Failure = new IOException("Unable to play https://user:password@example.com/a?token=secret") };
        var output = new StringWriter();
        Assert.Equal(10, await Program.RunAsync(["play", path, "--json"], TextReader.Null, output, new StringWriter(), playback: fake));
        Assert.Contains("playback_failed", output.ToString());
        Assert.DoesNotContain("password", output.ToString());
        Assert.DoesNotContain("secret", output.ToString());
        Assert.True(fake.Cleaned);
    }

    [Fact]
    public async Task HumanResultNamesThePlayedFile()
    {
        var fake = new FakePlayback();
        fake.Complete.TrySetResult();
        var output = new StringWriter();
        Assert.Equal(0, await Program.RunAsync(["play", path], TextReader.Null, output, new StringWriter(), playback: fake));
        Assert.Equal($"Played {Path.GetFullPath(path)}{Environment.NewLine}", output.ToString());
    }

    private sealed class FakePlayback : IFileAudioPlayback
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cleaned { get; private set; }
        public string? Path { get; private set; }
        public Exception? Failure { get; init; }
        public async Task<double> PlayAsync(string path, IProgress<AudioPlaybackProgress>? progress, CancellationToken ct)
        {
            Path = path;
            try
            {
                progress?.Report(new(0, 2));
                Started.TrySetResult();
                if (Failure is not null) throw Failure;
                await Complete.Task.WaitAsync(ct);
                return 2;
            }
            finally { Cleaned = true; }
        }
    }

    public void Dispose() => File.Delete(path);
}
