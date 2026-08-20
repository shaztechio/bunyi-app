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
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (SettingsStore Store, RecordingLog Log) NewStore()
    {
        var log = new RecordingLog();
        return (new SettingsStore(log, SettingsPath), log);
    }

    [Fact]
    public void A_first_run_gets_defaults_and_says_nothing()
    {
        var (store, log) = NewStore();

        var settings = store.Load();

        Assert.Equal(Appearance.System, settings.Appearance);   // spec §7 default
        Assert.Empty(settings.ModelRepo);
        Assert.Null(settings.ModelsFolder);
        Assert.Empty(log.Lines);   // a missing file is not a problem worth reporting
    }

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var (store, _) = NewStore();
        var saved = new AppSettings { Appearance = Appearance.Dark, ModelsFolder = _folder }
            .WithSourceFor(TtsMode.PresetVoice, "some-org/some-repo")
            .WithSourceFor(TtsMode.VoiceClone, "https://models.example.com/clone");

        store.Save(saved);
        var loaded = store.Load();

        Assert.Equal(Appearance.Dark, loaded.Appearance);
        Assert.Equal(_folder, loaded.ModelsFolder);
        Assert.Equal("some-org/some-repo", loaded.SourceFor(TtsMode.PresetVoice));
        Assert.Equal("https://models.example.com/clone", loaded.SourceFor(TtsMode.VoiceClone));
        Assert.Equal(string.Empty, loaded.SourceFor(TtsMode.VoiceDesign));
    }

    [Fact]
    public void The_persisted_keys_are_the_ones_the_spec_pins()
    {
        // DATA-FORMATS: "the keys (appearance, modelRepo.<Mode>) are the
        // contract, not the storage". They match macOS's UserDefaults keys, so
        // they are asserted against the literal JSON rather than a round trip,
        // which would pass even if both sides were renamed together.
        var (store, _) = NewStore();
        store.Save(new AppSettings { Appearance = Appearance.Light }
            .WithSourceFor(TtsMode.VoiceDesign, "org/design"));

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var root = document.RootElement;

        Assert.Equal("light", root.GetProperty("appearance").GetString());
        Assert.Equal(
            "org/design",
            root.GetProperty("modelRepo").GetProperty("modelRepo.Voice design").GetString());
    }

    [Fact]
    public void Freeing_memory_on_a_mode_switch_is_on_until_it_is_turned_off()
    {
        // §3e. The default is the whole point of the setting: the models
        // are several gigabytes, and someone who never opens Settings is
        // exactly who should not be holding one for a mode they left.
        Assert.True(new AppSettings().UnloadOnModeSwitch);
    }

    [Fact]
    public void A_settings_file_from_before_the_setting_existed_reads_as_on()
    {
        // An upgrade must not quietly turn it off. An absent key is not false;
        // it is someone who has never been asked.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, """{"appearance":"dark"}""");

        var (store, _) = NewStore();
        var loaded = store.Load();

        Assert.True(loaded.UnloadOnModeSwitch);
        Assert.Equal(Appearance.Dark, loaded.Appearance);
    }

    [Fact]
    public void Turning_it_off_is_persisted_under_the_key_the_spec_pins()
    {
        // Asserted against the literal JSON rather than a round trip, which
        // would pass even if both sides were renamed together. macOS keeps the
        // same key in UserDefaults.
        var (store, _) = NewStore();
        store.Save(new AppSettings { UnloadOnModeSwitch = false });

        using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));

        Assert.False(document.RootElement.GetProperty("unloadOnModeSwitch").GetBoolean());
        Assert.False(store.Load().UnloadOnModeSwitch);
    }

    [Theory]
    [InlineData(TtsMode.PresetVoice, "modelRepo.Preset voice")]
    [InlineData(TtsMode.VoiceDesign, "modelRepo.Voice design")]
    [InlineData(TtsMode.VoiceClone, "modelRepo.Voice clone")]
    public void Each_mode_has_the_macOS_key(TtsMode mode, string expected) =>
        Assert.Equal(expected, SettingsKeys.ModelRepo(mode));

    [Fact]
    public void Clearing_a_source_removes_it_rather_than_storing_a_blank()
    {
        // A blank field means "use the built-in default" (spec §3a). Storing an
        // empty string would say the same thing in a second way, which is how
        // two readers end up disagreeing.
        var settings = new AppSettings()
            .WithSourceFor(TtsMode.PresetVoice, "org/repo")
            .WithSourceFor(TtsMode.PresetVoice, "   ");

        Assert.Empty(settings.ModelRepo);
        Assert.Equal(string.Empty, settings.SourceFor(TtsMode.PresetVoice));
    }

    [Fact]
    public void A_source_is_trimmed_before_it_is_stored()
    {
        var settings = new AppSettings().WithSourceFor(TtsMode.PresetVoice, "  org/repo\t");

        Assert.Equal("org/repo", settings.SourceFor(TtsMode.PresetVoice));
    }

    [Fact]
    public void A_corrupt_file_gives_defaults_and_is_reported()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{ this is not json");
        var (store, log) = NewStore();

        var settings = store.Load();

        Assert.Equal(Appearance.System, settings.Appearance);
        // The user is about to see their preferences reset; the log is the only
        // place that can say why.
        Assert.Contains(log.Lines, l => l.Contains("Could not read settings"));
    }

    [Fact]
    public void An_empty_file_gives_defaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "");
        var (store, _) = NewStore();

        Assert.Equal(Appearance.System, store.Load().Appearance);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        var (store, _) = NewStore();

        store.Save(new AppSettings { Appearance = Appearance.Dark });

        Assert.True(File.Exists(SettingsPath));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void Saving_creates_the_folder_if_it_is_not_there()
    {
        var (store, _) = NewStore();
        Assert.False(Directory.Exists(_folder));

        store.Save(new AppSettings());

        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void A_models_folder_that_is_gone_falls_back_to_the_default_and_says_so()
    {
        // A chosen folder can be on a drive that is not plugged in (spec §3d
        // allows an external drive). Failing every operation against a path
        // that is not there would be worse than downloading again.
        var (store, log) = NewStore();
        var missing = Path.Combine(_folder, "not-there");

        var resolved = store.ResolveModelsFolder(new AppSettings { ModelsFolder = missing });

        Assert.Equal(Bunyi.Core.Infrastructure.AppPaths.DefaultModelsFolder, resolved);
        Assert.Contains(log.Lines, l => l.Contains("not available"));
    }

    [Fact]
    public void A_models_folder_that_exists_is_used()
    {
        Directory.CreateDirectory(_folder);
        var (store, log) = NewStore();

        var resolved = store.ResolveModelsFolder(new AppSettings { ModelsFolder = _folder });

        Assert.Equal(_folder, resolved);
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void No_chosen_folder_means_the_default()
    {
        var (store, _) = NewStore();

        Assert.Equal(
            Bunyi.Core.Infrastructure.AppPaths.DefaultModelsFolder,
            store.ResolveModelsFolder(new AppSettings()));
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Lines { get; } = [];
        public void Log(string message) => Lines.Add(message);
    }
}
