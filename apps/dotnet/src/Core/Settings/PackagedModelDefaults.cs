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
using Bunyi.Core.Diagnostics;

namespace Bunyi.Core.Settings;

/// <summary>Read-only distribution defaults; user source overrides always take precedence.</summary>
public sealed record PackagedModelDefaults(bool UseMirror = false)
{
    public const string FileName = "bunyi.defaults.json";

    public static PackagedModelDefaults Load(ILogSink log, string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, FileName);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("modelDownloadSource", out var source) || source.ValueKind != JsonValueKind.String)
                throw new JsonException("Expected schemaVersion 1 and modelDownloadSource.");
            var defaults = source.GetString() switch
            {
                "huggingFace" => new PackagedModelDefaults(),
                "mirror" => new PackagedModelDefaults(true),
                _ => throw new JsonException("Unknown modelDownloadSource."),
            };
            log.Log($"Packaged model download default: {(defaults.UseMirror ? "Bunyi mirror" : "Hugging Face")}.");
            return defaults;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Log($"Could not read packaged model defaults from {path}; using Hugging Face. {ex.Message}");
            return new();
        }
    }
}
