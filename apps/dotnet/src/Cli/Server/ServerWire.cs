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

using System.Text;
using System.Text.Json;
using Bunyi.Cli.Protocol;

namespace Bunyi.Cli.Server;

internal sealed record WireRequest(int ProtocolVersion, string Action, CommandRequest? Request = null, string? JobId = null, bool Detached = false);

internal static class ServerWire
{
    internal const int Version = 1;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task WriteAsync(Stream stream, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync(new byte[] { 10 }, ct);
        await stream.FlushAsync(ct);
    }

    // Bounded framing prevents a peer from allocating an unbounded ReadLine string.
    internal static async Task<string?> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var line = new MemoryStream();
        var single = new byte[1];
        while (await stream.ReadAsync(single, ct) != 0)
        {
            if (single[0] == 10) return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
            if (line.Length >= 1024 * 1024) throw new ServerException("invalid_arguments", "The server request exceeds the one-megabyte limit.");
            line.WriteByte(single[0]);
        }
        return line.Length == 0 ? null : throw new IOException("Incomplete server message.");
    }

    internal static Dictionary<string, object?> Result(string operation, string? id = null, string type = "result") => new()
    {
        ["schemaVersion"] = 1, ["ok"] = true, ["type"] = type,
        ["operation"] = operation, ["operationId"] = id ?? Guid.NewGuid().ToString("N")
    };

    internal static Dictionary<string, object?> Error(string operation, string? id, string code, string message)
    {
        var value = Result(operation, id, "error");
        value["ok"] = false;
        value["error"] = new { code, message };
        value["exitCode"] = code == "invalid_arguments" ? 2 : 4;
        return value;
    }

    internal static string? Text(Dictionary<string, object?> value, string key) => value.TryGetValue(key, out var item) ? item?.ToString() : null;
}
