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
/// Reads and writes <c>settings.json</c> — this platform's answer to the
/// <c>UserDefaults</c> the macOS app uses (spec §7, §3a, §3d).
/// </summary>
/// <remarks>
/// <para>
/// <b>Settings never stop the app.</b> A missing file is a first run; an
/// unreadable or malformed one is a bad file, not a reason to refuse to start.
/// Both give defaults, and the malformed case says so in the log. Nothing here
/// is worth more than the ability to launch: the worst outcome of ignoring a
/// corrupt settings file is that the user picks their appearance again.
/// </para>
/// <para>
/// <b>Writes are atomic.</b> The file is written beside its destination and
/// moved over it, so a crash or a full disk cannot leave a half-written
/// settings file — which, being JSON, would fail to parse and lose every
/// setting rather than the one being changed.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly ILogSink _log;
    private readonly object _gate = new();

    /// <summary>Creates a store over <paramref name="path"/>, or the default location.</summary>
    public SettingsStore(ILogSink log, string? path = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _path = path ?? AppPaths.SettingsFile;
    }

    /// <summary>Where the settings are read from and written to.</summary>
    public string Path => _path;

    /// <summary>
    /// Loads the settings, falling back to defaults for anything missing or
    /// unreadable.
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new AppSettings();

                var json = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(json)) return new AppSettings();

                return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Named rather than swallowed: the user is about to see their
                // preferences reset, and the log is the only place that can say
                // why.
                _log.Log($"Could not read settings from {_path} — using defaults. {ex.Message}");
                return new AppSettings();
            }
        }
    }

    /// <summary>Writes the settings, replacing whatever was there.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            try
            {
                var folder = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                var json = JsonSerializer.Serialize(settings, Json);

                // Same directory as the destination, so the move is a rename
                // within one filesystem and therefore atomic. A temp file on
                // another volume would make this a copy, which is exactly the
                // half-written file this avoids.
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Log($"Could not save settings to {_path}. {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The models folder to use: the one the user chose if it still resolves,
    /// otherwise the default (spec §3d).
    /// </summary>
    /// <remarks>
    /// A folder can move, or live on a drive that is not plugged in. Falling
    /// back keeps the app working — it will download again rather than fail
    /// every operation against a path that is not there — and the log says
    /// which folder is actually in use, because silently using a different one
    /// looks like the models vanished.
    /// </remarks>
    public string ResolveModelsFolder(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var chosen = settings.ModelsFolder;
        if (string.IsNullOrWhiteSpace(chosen)) return AppPaths.DefaultModelsFolder;

        if (Directory.Exists(chosen)) return chosen;

        _log.Log(
            $"The chosen models folder is not available ({chosen}) — " +
            $"using the default at {AppPaths.DefaultModelsFolder}.");
        return AppPaths.DefaultModelsFolder;
    }
}
