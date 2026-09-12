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

using System.Text.Json.Serialization;

namespace Bunyi.Core.Settings;

/// <summary>How the app follows, or ignores, the system appearance (spec §7).</summary>
public enum Appearance
{
    /// <summary>Follow the operating system. The default.</summary>
    System,
    Light,
    Dark,
}

/// <summary>
/// Everything the app remembers between launches, other than the files it
/// writes. macOS keeps the same values in <c>UserDefaults</c>; per
/// /spec/DATA-FORMATS.md the <b>keys</b> are the contract, not the storage,
/// so the names here match the macOS ones exactly.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Spec §7. Persisted under <c>appearance</c>, defaulting to System.</summary>
    [JsonPropertyName("appearance")]
    public Appearance Appearance { get; init; } = Appearance.System;

    /// <summary>
    /// Per-mode model source overrides (spec §3a), keyed
    /// <c>modelRepo.&lt;Mode&gt;</c> — the same keys macOS uses, with the mode's
    /// display name: <c>modelRepo.Preset voice</c> and so on.
    /// </summary>
    /// <remarks>
    /// A missing or empty value means "use the built-in default for that mode",
    /// which is the same meaning a blank field has in Settings. Absent rather
    /// than blank is the normal shape: nothing writes an empty override.
    /// </remarks>
    [JsonPropertyName("modelRepo")]
    public IReadOnlyDictionary<string, string> ModelRepo { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A models folder the user chose (spec §3d), or null for the default.
    /// </summary>
    /// <remarks>
    /// An absolute path, not a bookmark: only macOS needs one of those, and
    /// only because its sandbox will not otherwise re-grant access. A path that
    /// no longer resolves falls back to the default and is logged — see
    /// <see cref="SettingsStore"/>.
    /// </remarks>
    [JsonPropertyName("modelsFolder")]
    public string? ModelsFolder { get; init; }

    /// <summary>
    /// Whether leaving a mode unloads its model (spec §3e).
    /// </summary>
    /// <remarks>
    /// On by default: the models are several gigabytes and nothing asks for the
    /// one being left behind again. Off keeps it resident so returning to that
    /// mode is instant, and the unload happens at the next generate in another
    /// mode instead. Absent from the file means on — which is what an
    /// install that predates this setting gets.
    /// </remarks>
    [JsonPropertyName("unloadOnModeSwitch")]
    public bool UnloadOnModeSwitch { get; init; } = true;

    /// <summary>
    /// The configured source for a mode, or the empty string when it has none.
    /// </summary>
    public string SourceFor(TtsMode mode) =>
        ModelRepo.TryGetValue(SettingsKeys.ModelRepo(mode), out var value) ? value : string.Empty;

    /// <summary>Returns a copy with <paramref name="mode"/>'s source set, or cleared when blank.</summary>
    public AppSettings WithSourceFor(TtsMode mode, string? value)
    {
        var map = new Dictionary<string, string>(ModelRepo, StringComparer.Ordinal);
        var key = SettingsKeys.ModelRepo(mode);

        if (string.IsNullOrWhiteSpace(value)) map.Remove(key);
        else map[key] = value.Trim();

        return this with { ModelRepo = map };
    }
}

/// <summary>The persisted key names, which /spec/DATA-FORMATS.md pins.</summary>
public static class SettingsKeys
{
    /// <summary>Spec §7.</summary>
    public const string Appearance = "appearance";

    /// <summary>Spec §3a — <c>modelRepo.Preset voice</c>, and so on.</summary>
    public static string ModelRepo(TtsMode mode) => $"modelRepo.{mode.DisplayName()}";

    /// <summary>Spec §3e.</summary>
    public const string UnloadOnModeSwitch = "unloadOnModeSwitch";
}
