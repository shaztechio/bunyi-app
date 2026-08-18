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
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Infrastructure;

namespace Bunyi.Core.Settings;

/// <summary>
/// One named set of the three per-mode sources (spec §3a).
/// </summary>
/// <remarks>
/// An empty string means that mode uses its built-in default, which is the same
/// meaning a blank field has in Settings.
/// </remarks>
public sealed record ModelConfig
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("presetVoice")]
    public string PresetVoice { get; init; } = string.Empty;

    [JsonPropertyName("voiceDesign")]
    public string VoiceDesign { get; init; } = string.Empty;

    [JsonPropertyName("voiceClone")]
    public string VoiceClone { get; init; } = string.Empty;

    [JsonPropertyName("savedAt")]
    public DateTimeOffset SavedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The source this configuration gives a mode.</summary>
    public string For(TtsMode mode) => mode switch
    {
        TtsMode.PresetVoice => PresetVoice,
        TtsMode.VoiceDesign => VoiceDesign,
        TtsMode.VoiceClone => VoiceClone,
        _ => string.Empty,
    };
}

/// <summary>
/// Saved model configurations, in <c>configs.json</c> (spec §3a).
/// </summary>
/// <remarks>
/// <para>
/// The three sources are saved and restored <b>as a set</b> because they belong
/// together: switching between the Hub and a self-hosted mirror means changing
/// all three, the values are long and easy to mistype, and each must match its
/// mode or the app loads a model that runs and produces nonsense.
/// </para>
/// <para>
/// <b>There is no built-in mirror entry here, deliberately.</b> §3a permits one
/// — and macOS ships it — but gates it: "A platform ships this only if its
/// mirror publishes <c>manifest.sha256</c>". The project mirror serves the MLX
/// weight set; it has no ONNX files, so there is nothing for this app to point
/// at and nothing whose bytes it could verify. Offering a source the app itself
/// endorses is a higher bar than documenting one a user picked, and an entry
/// that 404s on every file would not clear it. Add it here when the mirror
/// serves checksummed ONNX exports, and not before.
/// </para>
/// <para>
/// Stored with the app's own data rather than in the models folder: it
/// describes <i>where models come from</i>, so it must survive relocating or
/// deleting that folder.
/// </para>
/// </remarks>
public sealed class ModelConfigLibrary
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogSink _log;
    private readonly string _path;
    private readonly object _gate = new();
    private List<ModelConfig> _configs = [];

    public ModelConfigLibrary(ILogSink log, string? path = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        // Qualified: the Path property below shadows System.IO.Path.
        _path = path ?? System.IO.Path.Combine(AppPaths.ModelConfigs, "configs.json");
        Load();
    }

    /// <summary>Where the file lives.</summary>
    public string Path => _path;

    /// <summary>
    /// What Settings lists: saved configurations, alphabetically.
    /// </summary>
    public IReadOnlyList<ModelConfig> Configs
    {
        get { lock (_gate) return [.. _configs.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)]; }
    }

    /// <summary>Reads the file, tolerating a missing or damaged one.</summary>
    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) { _configs = []; return; }

                var json = File.ReadAllText(_path);
                _configs = string.IsNullOrWhiteSpace(json)
                    ? []
                    : JsonSerializer.Deserialize<List<ModelConfig>>(json, Json) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Log($"Could not read saved model configurations from {_path}. {ex.Message}");
                _configs = [];
            }
        }
    }

    /// <summary>
    /// Saves a configuration, replacing any of the same name.
    /// </summary>
    /// <remarks>
    /// Names are unique case-insensitively: saving over one replaces it rather
    /// than accumulating near-duplicates, which is what a list of long URLs
    /// would otherwise become.
    /// </remarks>
    public ModelConfig Save(string name, string presetVoice, string voiceDesign, string voiceClone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var config = new ModelConfig
        {
            Name = name.Trim(),
            PresetVoice = presetVoice?.Trim() ?? string.Empty,
            VoiceDesign = voiceDesign?.Trim() ?? string.Empty,
            VoiceClone = voiceClone?.Trim() ?? string.Empty,
        };

        lock (_gate)
        {
            _configs.RemoveAll(c => string.Equals(c.Name, config.Name, StringComparison.OrdinalIgnoreCase));
            _configs.Add(config);
            Write();
        }

        _log.Log($"Saved the model configuration “{config.Name}”.");
        return config;
    }

    /// <summary>Removes a configuration.</summary>
    public void Delete(ModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_gate)
        {
            _configs.RemoveAll(c => c.Id == config.Id);
            Write();
        }

        _log.Log($"Deleted the model configuration “{config.Name}”.");
    }

    private void Write()
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // Same directory, so the move is a rename within one filesystem and
            // therefore atomic — a crash cannot leave malformed JSON that loses
            // every configuration rather than the one being changed.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_configs, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Log($"Could not save model configurations to {_path}. {ex.Message}");
        }
    }
}
