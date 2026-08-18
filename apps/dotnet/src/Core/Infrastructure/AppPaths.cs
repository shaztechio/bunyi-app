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

namespace Bunyi.Core.Infrastructure;

/// <summary>
/// Where this app keeps a user's files. Pinned by /spec/DATA-FORMATS.md
/// ("Per-user app data"), which is what makes a models folder or a backup
/// interchangeable between installs.
/// </summary>
/// <remarks>
/// <para>
/// The subfolder names — <c>Models</c>, <c>Outputs</c>, <c>Voices</c>,
/// <c>ModelConfigs</c> — are identical on every platform, and match what the
/// macOS app writes inside its sandbox container. Only the root differs.
/// </para>
/// <para>
/// <b>Settings are separated from data</b>, on the platforms that separate
/// them. Windows roams <c>%APPDATA%</c> and does not roam
/// <c>%LOCALAPPDATA%</c>; a multi-gigabyte models folder under the former
/// would be handed to a domain roaming profile to synchronise at every logon.
/// Settings are a few hundred bytes and worth carrying between machines;
/// models are not, and cannot be. XDG draws the same line under different
/// names, which is why <c>$XDG_DATA_HOME</c> and <c>$XDG_CONFIG_HOME</c> are
/// read separately rather than one being derived from the other.
/// </para>
/// </remarks>
public static class AppPaths
{
    /// <summary>The product name, and the folder name under every root.</summary>
    public const string ProductName = "Bunyi";

    /// <summary>
    /// Per-user data: models, outputs, saved voices, model configurations.
    /// Windows <c>%LOCALAPPDATA%\Bunyi</c>; Linux <c>$XDG_DATA_HOME/Bunyi</c>,
    /// defaulting to <c>~/.local/share/Bunyi</c>.
    /// </summary>
    public static string DataRoot => Path.Combine(DataHome(), ProductName);

    /// <summary>
    /// Per-user configuration. Windows <c>%APPDATA%\Bunyi</c>; Linux
    /// <c>$XDG_CONFIG_HOME/Bunyi</c>, defaulting to <c>~/.config/Bunyi</c>.
    /// </summary>
    public static string ConfigRoot => Path.Combine(ConfigHome(), ProductName);

    /// <summary>The default models folder. The user may point elsewhere (spec §3d).</summary>
    public static string DefaultModelsFolder => Path.Combine(DataRoot, "Models");

    /// <summary>Generated audio (spec §2).</summary>
    public static string Outputs => Path.Combine(DataRoot, "Outputs");

    /// <summary>Saved voices library: voices.json plus copied clips (spec §5).</summary>
    public static string Voices => Path.Combine(DataRoot, "Voices");

    /// <summary>Saved model configurations: configs.json (spec §3a).</summary>
    public static string ModelConfigs => Path.Combine(DataRoot, "ModelConfigs");

    /// <summary>The settings file — this platform's answer to macOS's UserDefaults.</summary>
    public static string SettingsFile => Path.Combine(ConfigRoot, "settings.json");

    /// <summary>Where the log mirror is written (spec §8).</summary>
    public static string LogsFolder => Path.Combine(DataRoot, "Logs");

    /// <summary>
    /// Creates a folder if it is not there and returns it, so callers can use
    /// the result directly. Creating a folder that exists is not an error.
    /// </summary>
    public static string EnsureFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// XDG says a relative <c>$XDG_*_HOME</c> "must be ignored", because a
    /// relative path here would resolve against whatever directory the process
    /// happens to be started in — which for a desktop app is arbitrary, and
    /// would scatter a user's models across the filesystem.
    /// </summary>
    private static string? Xdg(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Path.IsPathRooted(value) ? value : null;
    }

    private static string DataHome()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Read XDG before asking .NET: SpecialFolder does consult these
            // variables, but the mapping is a platform-implementation detail
            // and this is a format the spec pins, so it is read directly.
            var xdg = Xdg("XDG_DATA_HOME");
            if (xdg is not null) return xdg;
            return Path.Combine(Home(), ".local", "share");
        }

        // %LOCALAPPDATA%, the non-roaming one.
        return NonEmptyFolder(Environment.SpecialFolder.LocalApplicationData);
    }

    private static string ConfigHome()
    {
        if (!OperatingSystem.IsWindows())
        {
            var xdg = Xdg("XDG_CONFIG_HOME");
            if (xdg is not null) return xdg;
            return Path.Combine(Home(), ".config");
        }

        // %APPDATA%, the roaming one — settings are worth carrying between
        // machines, which is exactly what models are not.
        return NonEmptyFolder(Environment.SpecialFolder.ApplicationData);
    }

    private static string Home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home)) return home;

        home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home)) return home;

        // Nothing sensible left. Failing loudly beats writing a user's models
        // into the process's working directory.
        throw new InvalidOperationException(
            "Could not determine the user's home directory.");
    }

    private static string NonEmptyFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(
                $"Could not determine the location of {folder}.");
        }
        return path;
    }
}
