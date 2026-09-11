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
using System.Text.RegularExpressions;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Infrastructure;

namespace Bunyi.Cli.Protocol;

/// <summary>Caps repetitive events at four per second without delaying phase changes.</summary>
public sealed class ProgressEmitter(Action<Dictionary<string, object?>> emit, TimeProvider? time = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private string? previous;
    private long last;

    public void Report(Dictionary<string, object?> message)
    {
        lock (gate)
        {
            var kind = message.GetValueOrDefault("type")?.ToString();
            var key = kind + ":" + message.GetValueOrDefault("currentFile") + ":" + message.GetValueOrDefault("item");
            var now = clock.GetTimestamp();
            if (kind is not ("result" or "error" or "accepted") && key == previous && clock.GetElapsedTime(last, now) < TimeSpan.FromMilliseconds(250)) return;
            previous = key; last = now;
            emit(message);
        }
    }
}

public sealed class CliOutput(TextWriter output, TextWriter error, bool json, bool jsonl)
{
    private readonly object gate = new();
    private bool finished;
    public void Event(Dictionary<string, object?> message)
    {
        lock (gate)
        {
            if (finished || json) return;
            if (jsonl) WriteJson(message);
            else { error.WriteLine(message.GetValueOrDefault("detail") ?? message.GetValueOrDefault("type")); error.Flush(); }
        }
    }
    public void Finish(Dictionary<string, object?> message)
    {
        lock (gate)
        {
            if (finished) return;
            finished = true;
            if (json || jsonl) { WriteJson(message); return; }
            if (CliProtocol.ExitCode(message) != 0)
            {
                error.WriteLine(JsonSerializer.Serialize(message.GetValueOrDefault("error"), CliProtocol.Json));
                if (message.TryGetValue("recovery", out var recovery))
                    error.WriteLine(JsonSerializer.Serialize(recovery, CliProtocol.Json));
                error.Flush(); return;
            }
            foreach (var key in new[] { "help", "outputPath", "transcript", "path", "version" })
                if (message.TryGetValue(key, out var simple)) { output.WriteLine(simple); output.Flush(); return; }
            if (message.GetValueOrDefault("played") is true)
            {
                output.WriteLine($"Played {message.GetValueOrDefault("inputPath")}"); output.Flush(); return;
            }
            output.WriteLine(JsonSerializer.Serialize(message, CliProtocol.Json)); output.Flush();
        }
    }
    private void WriteJson(Dictionary<string, object?> message) { output.WriteLine(JsonSerializer.Serialize(message, CliProtocol.Json)); output.Flush(); }
}

/// <summary>A private durable log; it never writes diagnostic bytes to stdout.</summary>
public sealed partial class CliLog : ILogSink
{
    private readonly object gate = new();
    public static string Path => System.IO.Path.Combine(AppPaths.LogsFolder, "bunyi-cli.log");
    public void Log(string message)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(AppPaths.LogsFolder);
                File.AppendAllText(Path, $"{DateTimeOffset.UtcNow:O} {Redact(message)}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Logging must not fail an operation. */ }
    }
    public static string Redact(string text) => Authorization().Replace(Url().Replace(text, match =>
    {
        if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[redacted URL]";
        return uri.GetLeftPart(UriPartial.Authority).Replace(uri.UserInfo + "@", "", StringComparison.Ordinal) + uri.AbsolutePath;
    }), "[redacted authorization]");
    [GeneratedRegex(@"https?://[^\s\""<>]+", RegexOptions.IgnoreCase)] private static partial Regex Url();
    [GeneratedRegex(@"(?:authorization\s*[:=]\s*|\bBearer\s+)[^\r\n]+", RegexOptions.IgnoreCase)] private static partial Regex Authorization();
}
