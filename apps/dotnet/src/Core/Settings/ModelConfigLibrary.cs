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

    /// <summary>
    /// Where this configuration points, in a few words (spec §3a).
    /// </summary>
    /// <remarks>
    /// A name alone does not say what a configuration contains, and the three
    /// values are long URLs that would not fit beside it. The host is the part
    /// that answers "which of my sets is this" — <c>models.bunyi.app</c>,
    /// <c>huggingface.co</c>, or the org of a repo id — so that is what is
    /// shown, with a count of however many modes are still on their defaults.
    /// Ported from macOS's <c>summary</c>, which has always shown this.
    /// </remarks>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            string[] values = [PresetVoice, VoiceDesign, VoiceClone];
            if (values.All(string.IsNullOrWhiteSpace)) return "All three on the defaults";

            var hosts = values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(Host)
                .Where(h => h.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.CurrentCultureIgnoreCase);

            var where = string.Join(", ", hosts);
            var blanks = values.Count(string.IsNullOrWhiteSpace);

            if (blanks == 0) return where;

            return $"{where}, {blanks} on the default{(blanks == 1 ? "" : "s")}";
        }
    }

    /// <summary>A URL's host, or a repo id's org.</summary>
    private static string Host(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && !string.IsNullOrEmpty(url.Host)
            ? url.Host
            : value.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

    /// <summary>
    /// Whether the app ships this entry rather than the user having saved it.
    /// </summary>
    /// <remarks>
    /// <c>JsonIgnore</c> because it is derived from the id, not stored: without
    /// it every saved configuration would gain an <c>"isBuiltIn": false</c> in
    /// <c>configs.json</c>, which is a fact about the row rather than about the
    /// sources, and one a future reader could contradict by editing.
    /// </remarks>
    [JsonIgnore]
    public bool IsBuiltIn => Id == ModelConfigLibrary.BunyiMirror.Id;

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
/// <b>There is a built-in mirror entry, and there did not use to be.</b> §3a
/// permits one but gates it: "A platform ships this only if its mirror
/// publishes <c>manifest.sha256</c>". For a long time the project mirror served
/// only the MLX weight set, so this app had nothing to point at and nothing
/// whose bytes it could verify — and an entry the app itself endorses that 404s
/// on every file is worse than no entry.
/// </para>
/// <para>
/// The ONNX exports are now mirrored at <c>models.bunyi.app/onnx/*</c>, each
/// prefix publishing its own <c>manifest.sha256</c>, and all three modes have
/// been downloaded and generated from them. The gate is met by measurement
/// rather than intention, which is the standard §3a set (#100).
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
    /// The project's own mirror of the ONNX exports (spec §3a, #100).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hugging Face is unreachable on some networks and blocked outright in
    /// mainland China. For a Qwen model whose second language is Chinese and
    /// whose default speakers include Uncle_Fu and Ono_Anna, that is not a
    /// footnote — it is the difference between the app working and not. macOS
    /// has shipped this entry since it had a mirror to point at; this is the
    /// same thing for the ONNX family.
    /// </para>
    /// <para>
    /// <b>Not the default, deliberately.</b> Hugging Face is where the weights
    /// actually come from, and a default pointing at project-run infrastructure
    /// would make that infrastructure a single point of failure for every
    /// install. Each output records the source that produced it, so it has to
    /// be one the user chose. It is not an automatic fallback when the Hub is
    /// slow either, for the same reason.
    /// </para>
    /// <para>
    /// The <c>Guid</c> is fixed rather than generated. The row has to keep its
    /// identity across launches without being written to disk, and a fresh one
    /// each time would make the list treat it as a different row on every read.
    /// It matches macOS's UUID for the same entry, which costs nothing and
    /// means the two apps describe one thing rather than two.
    /// </para>
    /// </remarks>
    public static ModelConfig BunyiMirror { get; } = new()
    {
        Id = new Guid("B0000000-0000-4000-A000-000000000001"),
        Name = "Bunyi mirror",
        PresetVoice = "https://models.bunyi.app/onnx/customvoice",
        VoiceDesign = "https://models.bunyi.app/onnx/voicedesign",
        VoiceClone = "https://models.bunyi.app/onnx/voiceclone",
        SavedAt = DateTimeOffset.MinValue,
    };

    /// <summary>Whether a configuration is one the app ships rather than one saved here.</summary>
    /// <remarks>
    /// A built-in has no Delete button: there is nothing on disk to remove, and
    /// offering the action would promise something the library cannot do.
    /// </remarks>
    public static bool IsBuiltIn(ModelConfig config) =>
        config is not null && config.Id == BunyiMirror.Id;

    /// <summary>
    /// What Settings lists: the built-in mirror, then everything saved here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror is never persisted, so it cannot be edited or deleted.
    /// <b>Saving a configuration of your own under the same name is how you
    /// override it</b> — yours then stands in for the built-in entirely,
    /// because a real entry the user chose to write beats one the app shipped.
    /// Delete yours and the built-in returns; it was never gone, only hidden.
    /// </para>
    /// <para>
    /// An override keeps its ordinary alphabetical place rather than being
    /// pinned to the top. Once it is a saved configuration it behaves like
    /// every other one, which is the whole point of letting a name shadow.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ModelConfig> Listed
    {
        get
        {
            var saved = Configs;
            var overridden = saved.Any(
                c => string.Equals(c.Name, BunyiMirror.Name, StringComparison.OrdinalIgnoreCase));

            return overridden ? saved : [BunyiMirror, .. saved];
        }
    }

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
    /// <remarks>
    /// The built-in is refused rather than quietly ignored. It is never in
    /// <c>_configs</c>, so removing it would be a no-op that wrote the file
    /// again and logged a deletion that did not happen — and the UI hides its
    /// Delete button, so reaching here with one is a bug worth hearing about.
    /// </remarks>
    public void Delete(ModelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (IsBuiltIn(config))
        {
            _log.Log($"“{config.Name}” is built in and has nothing on disk to delete.");
            return;
        }

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
