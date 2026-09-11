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
using System.Text.Json.Serialization;

namespace Bunyi.Cli.Protocol;

public sealed record CommandRequest(string Operation, Dictionary<string, string?> Arguments, string OperationId)
{
    public bool Has(string name) => Arguments.ContainsKey(name);
    public string? Get(string name) => Arguments.GetValueOrDefault(name);
}

public sealed class CliException(string code, string message, int exitCode = 2) : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}

public static class CliProtocol
{
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Dictionary<string, object?> Result(CommandRequest request, params (string Key, object? Value)[] fields)
    {
        var result = Envelope(request, "result");
        result["ok"] = true;
        foreach (var (key, value) in fields) result[key] = value;
        return result;
    }

    public static Dictionary<string, object?> Event(CommandRequest request, string type, params (string Key, object? Value)[] fields)
    {
        var result = Envelope(request, type);
        result["timestamp"] = DateTimeOffset.UtcNow;
        foreach (var (key, value) in fields) result[key] = value;
        return result;
    }

    public static Dictionary<string, object?> Error(CommandRequest request, string code, string message, int exitCode = 10)
    {
        var result = Envelope(request, "error");
        result["ok"] = false;
        result["error"] = new { code, message };
        result["exitCode"] = exitCode;
        return result;
    }

    public static Dictionary<string, object?> Envelope(CommandRequest request, string type) => new()
    {
        ["schemaVersion"] = 1, ["type"] = type,
        ["operation"] = request.Operation, ["operationId"] = request.OperationId
    };

    public static int ExitCode(Dictionary<string, object?> result)
    {
        if (result.TryGetValue("exitCode", out var value))
            return value is JsonElement element ? element.GetInt32() : Convert.ToInt32(value);
        return result.TryGetValue("ok", out var ok) && ok?.ToString()?.Equals("false", StringComparison.OrdinalIgnoreCase) == true ? 10 : 0;
    }
}

/// <summary>Progress callbacks run synchronously so none can overtake the terminal result.</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
