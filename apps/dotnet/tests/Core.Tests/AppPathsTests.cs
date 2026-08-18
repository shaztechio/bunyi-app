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

using Bunyi.Core.Infrastructure;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// /spec/DATA-FORMATS.md ("Per-user app data") pins these. They are a
/// cross-app contract, not an implementation detail: a models folder or a
/// backup is only interchangeable if both apps agree where things live.
/// </summary>
/// <remarks>
/// These tests branch on the operating system rather than skipping, so each
/// one asserts something true wherever it runs. A skipped test on the platform
/// that matters reads as a pass in CI, which is the failure mode worth
/// avoiding when the whole point is cross-platform agreement.
/// </remarks>
public class AppPathsTests
{
    [Fact]
    public void Every_subfolder_sits_under_the_data_root_with_its_pinned_name()
    {
        Assert.Equal(Path.Combine(AppPaths.DataRoot, "Models"), AppPaths.DefaultModelsFolder);
        Assert.Equal(Path.Combine(AppPaths.DataRoot, "Outputs"), AppPaths.Outputs);
        Assert.Equal(Path.Combine(AppPaths.DataRoot, "Voices"), AppPaths.Voices);
        Assert.Equal(Path.Combine(AppPaths.DataRoot, "ModelConfigs"), AppPaths.ModelConfigs);
    }

    [Fact]
    public void The_roots_are_absolute_and_end_in_the_product_name()
    {
        Assert.True(Path.IsPathRooted(AppPaths.DataRoot));
        Assert.True(Path.IsPathRooted(AppPaths.ConfigRoot));
        Assert.True(Path.IsPathRooted(AppPaths.SettingsFile));
        Assert.Equal("Bunyi", Path.GetFileName(AppPaths.DataRoot));
        Assert.Equal("Bunyi", Path.GetFileName(AppPaths.ConfigRoot));
    }

    [Fact]
    public void Settings_live_in_the_config_root_not_the_data_root()
    {
        // Deliberate: on Windows %APPDATA% roams and %LOCALAPPDATA% does not.
        // Settings are worth carrying between machines; a multi-gigabyte models
        // folder handed to a roaming profile is a support incident.
        Assert.Equal(Path.Combine(AppPaths.ConfigRoot, "settings.json"), AppPaths.SettingsFile);
        Assert.NotEqual(AppPaths.DataRoot, AppPaths.ConfigRoot);
    }

    [Fact]
    public void The_roots_follow_the_contract_for_this_platform()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            Assert.Equal(Path.Combine(local, "Bunyi"), AppPaths.DataRoot);
            Assert.Equal(Path.Combine(roaming, "Bunyi"), AppPaths.ConfigRoot);
            return;
        }

        using var data = new EnvironmentVariable("XDG_DATA_HOME", null);
        using var config = new EnvironmentVariable("XDG_CONFIG_HOME", null);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, ".local", "share", "Bunyi"), AppPaths.DataRoot);
        Assert.Equal(Path.Combine(home, ".config", "Bunyi"), AppPaths.ConfigRoot);
    }

    [Fact]
    public void An_absolute_XDG_variable_is_honoured_where_XDG_applies()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\custom\data" : "/custom/data";
        using var data = new EnvironmentVariable("XDG_DATA_HOME", root);

        if (OperatingSystem.IsWindows())
        {
            // Windows has its own answer and must not start reading XDG.
            Assert.DoesNotContain("custom", AppPaths.DataRoot);
            return;
        }

        Assert.Equal(Path.Combine(root, "Bunyi"), AppPaths.DataRoot);
    }

    [Fact]
    public void A_relative_XDG_variable_is_ignored()
    {
        // The XDG spec says a relative value "must be ignored". It would
        // otherwise resolve against the process's working directory, which for
        // a desktop app is arbitrary — scattering a user's models wherever the
        // app happened to be launched from.
        using var data = new EnvironmentVariable("XDG_DATA_HOME", "relative/path");

        Assert.True(Path.IsPathRooted(AppPaths.DataRoot));
        Assert.DoesNotContain("relative", AppPaths.DataRoot);
    }

    [Fact]
    public void Ensuring_a_folder_creates_it_and_is_safe_to_repeat()
    {
        var path = Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(path, AppPaths.EnsureFolder(path));
            Assert.True(Directory.Exists(path));

            AppPaths.EnsureFolder(path);   // again: not an error
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>Sets an environment variable for the life of the test.</summary>
    private sealed class EnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentVariable(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
    }
}
