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

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Bunyi.App.Infrastructure;
using Bunyi.Core.Platform;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>Settings (spec §7, §3a, §3d).</summary>
public sealed class SettingsTests : HeadlessWindows
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();
    private readonly List<Appearance> _applied = [];

    public SettingsTests() => Directory.CreateDirectory(_folder);

    protected override void DisposeCore()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private SettingsViewModel NewModel()
    {
        var store = new SettingsStore(_log, Path.Combine(_folder, "settings.json"));
        var configs = new ModelConfigLibrary(_log, Path.Combine(_folder, "configs.json"));
        return new SettingsViewModel(store, configs, _log, _applied.Add, DefaultFor);
    }

    private static string DefaultFor(TtsMode mode) => mode switch
    {
        TtsMode.PresetVoice => "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        TtsMode.VoiceDesign => "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX",
        _ => "wavekat/Qwen3-TTS-0.6B-Base-ONNX",
    };

    [AvaloniaFact]
    public void The_window_opens_with_the_tabs_the_spec_names()
    {
        // §7: General, Models, Storage, Backup — plus About, which §9a puts
        // here because Windows and Linux have no About panel of their own.
        var window = Open(new SettingsWindow { DataContext = NewModel() });

        var tabs = window.GetLogicalDescendants().OfType<TabItem>().ToList();
        Assert.Equal(
            ["General", "Models", "Storage", "Backup", "About"],
            tabs.Select(t => t.Header as string));
    }

    // ---- About (spec §9a) ----

    [AvaloniaFact]
    public void The_about_tab_names_the_app_its_version_and_its_platform()
    {
        // Before this there was nowhere to see the version without generating
        // something first — no menu bar, and Avalonia gives nothing free the
        // way AppKit does.
        var window = Open(new SettingsWindow { DataContext = NewModel() });

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 4;
        window.UpdateLayout();

        var shown = window.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .ToList();

        Assert.Contains(AboutInfo.Name, shown);
        Assert.Contains(AboutInfo.VersionLine, shown);
        Assert.Contains(AboutInfo.Copyright, shown);
    }

    [AvaloniaFact]
    public void The_about_tab_titles_the_window()
    {
        var window = Open(new SettingsWindow { DataContext = NewModel() });

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 4;

        Assert.Equal("About", window.Title);
    }

    [Fact]
    public void The_version_line_carries_both_the_version_and_the_platform()
    {
        // Windows and Linux are one codebase and look identical, so a version
        // alone does not say which build a bug report is about.
        Assert.Contains(AboutInfo.Version, AboutInfo.VersionLine, StringComparison.Ordinal);
        Assert.Contains(AboutInfo.Platform, AboutInfo.VersionLine, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_comes_from_the_build_rather_than_a_constant()
    {
        // This project has already shipped a build whose version said 1.0
        // because the number lived in two places.
        Assert.Equal(
            typeof(AboutInfo).Assembly.GetName().Version?.ToString(3),
            AboutInfo.Version);
    }

    [AvaloniaFact]
    public void The_about_tab_credits_the_software_it_is_built_on()
    {
        var window = Open(new SettingsWindow { DataContext = NewModel() });

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 4;
        window.UpdateLayout();

        var shown = window.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .ToList();

        foreach (var credit in AboutInfo.Credits.Concat(AboutInfo.ModelCredits))
        {
            Assert.Contains(credit.Name, shown);
        }
    }

    [Fact]
    public void Everything_the_app_ships_or_downloads_is_credited()
    {
        // The things a user actually runs, each named once.
        var names = AboutInfo.Credits.Select(c => c.Name).ToList();

        Assert.Contains("Avalonia", names);
        Assert.Contains("ONNX Runtime", names);
        Assert.Contains("SoundFlow", names);
        Assert.Contains(names, n => n.Contains("whisper.cpp", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("QwenTTS", StringComparison.Ordinal));

        // And the models, which arrive after the app does.
        Assert.Contains(AboutInfo.ModelCredits, c => c.Name.Contains("Qwen3-TTS", StringComparison.Ordinal));
        Assert.Contains(AboutInfo.ModelCredits, c => c.Name.Contains("Whisper", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_credit_names_a_licence_and_somewhere_to_look()
    {
        // A credits list that says "MIT" for something that is not is a licence
        // claim this project cannot support, so each entry has to carry both —
        // the licence, and the place someone can check it.
        Assert.NotEmpty(AboutInfo.Credits);

        foreach (var credit in AboutInfo.Credits.Concat(AboutInfo.ModelCredits))
        {
            Assert.False(string.IsNullOrWhiteSpace(credit.Name));
            Assert.False(string.IsNullOrWhiteSpace(credit.Does));
            Assert.False(string.IsNullOrWhiteSpace(credit.Licence));
            Assert.StartsWith("https://", credit.Home, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void Every_credit_link_is_clickable()
    {
        // A URL you have to select and paste is a URL nobody follows.
        var window = Open(new SettingsWindow { DataContext = NewModel() });

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 4;
        window.UpdateLayout();

        var links = window.GetLogicalDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("link"))
            .Select(b => b.Content as string)
            .ToList();

        foreach (var credit in AboutInfo.Credits.Concat(AboutInfo.ModelCredits))
        {
            Assert.Contains(credit.Home, links);
        }

        // The project's own link too.
        Assert.Contains(AboutInfo.Home, links);
    }

    [Fact]
    public void Every_link_the_about_tab_offers_is_one_that_will_open()
    {
        // WebLink refuses anything that is not https. A credit carrying such a
        // link would render a button that quietly does nothing.
        foreach (var credit in AboutInfo.Credits.Concat(AboutInfo.ModelCredits))
        {
            Assert.True(WebLink.IsSafe(credit.Home), $"{credit.Name} has an unopenable link");
        }

        Assert.True(WebLink.IsSafe(AboutInfo.Home));
    }

    [Fact]
    public void No_credit_is_listed_twice()
    {
        var all = AboutInfo.Credits.Concat(AboutInfo.ModelCredits).Select(c => c.Name).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_platform_matches_the_stamp_written_into_every_clip()
    {
        // One wording, so a file's "Made with" line and the About tab can never
        // disagree about what this build is.
        Assert.Equal(Bunyi.Core.Audio.OutputMetadata.CurrentPlatform, AboutInfo.Platform);
    }

    [AvaloniaFact]
    public void The_window_title_follows_the_selected_tab()
    {
        // §7: "the window title reflects the selected tab (platform convention)".
        var window = Open(new SettingsWindow { DataContext = NewModel() });

        var tabControl = window.GetLogicalDescendants().OfType<TabControl>().First();
        tabControl.SelectedIndex = 2;

        Assert.Equal("Storage", window.Title);
    }

    [AvaloniaFact]
    public void Appearance_defaults_to_System_and_is_applied_when_changed()
    {
        // §7: applies immediately, to every window the app owns.
        var model = NewModel();
        Assert.Equal(Appearance.System, model.Appearance);

        model.Appearance = Appearance.Dark;

        Assert.Equal(Appearance.Dark, Assert.Single(_applied));
    }

    [AvaloniaFact]
    public void Appearance_survives_reopening_the_window()
    {
        NewModel().Appearance = Appearance.Light;

        Assert.Equal(Appearance.Light, NewModel().Appearance);
    }

    [AvaloniaFact]
    public void All_three_appearance_choices_are_offered()
    {
        Assert.Equal(
            [Appearance.System, Appearance.Light, Appearance.Dark],
            NewModel().Appearances);
    }

    [AvaloniaFact]
    public void A_per_mode_source_is_saved_as_it_is_typed()
    {
        var model = NewModel();

        model.PresetVoiceSource = "some-org/some-repo";

        Assert.Equal("some-org/some-repo", NewModel().PresetVoiceSource);
    }

    [AvaloniaFact]
    public void Clearing_a_source_goes_back_to_the_default()
    {
        // §3a: a blank field means the built-in default for that mode.
        var model = NewModel();
        model.PresetVoiceSource = "some-org/some-repo";

        model.PresetVoiceSource = string.Empty;

        Assert.Equal(string.Empty, NewModel().PresetVoiceSource);
    }

    [AvaloniaFact]
    public void Reset_clears_all_three_at_once()
    {
        var model = NewModel();
        model.PresetVoiceSource = "a/b";
        model.VoiceDesignSource = "c/d";
        model.VoiceCloneSource = "e/f";

        model.ResetSourcesCommand.Execute(null);

        Assert.Equal(string.Empty, model.PresetVoiceSource);
        Assert.Equal(string.Empty, model.VoiceDesignSource);
        Assert.Equal(string.Empty, model.VoiceCloneSource);
    }

    [AvaloniaFact]
    public void A_configuration_saves_and_restores_all_three_together()
    {
        // §3a: they belong together — switching between the Hub and a mirror
        // means changing all three, and each must match its mode or the app
        // loads a model that runs and produces nonsense.
        var model = NewModel();
        model.PresetVoiceSource = "org/preset";
        model.VoiceDesignSource = "org/design";
        model.VoiceCloneSource = "org/clone";
        model.NewConfigName = "Mine";
        model.SaveConfigCommand.Execute(null);

        model.ResetSourcesCommand.Execute(null);
        Assert.Equal(string.Empty, model.PresetVoiceSource);

        model.UseConfigCommand.Execute(model.Configs.Single());

        Assert.Equal("org/preset", model.PresetVoiceSource);
        Assert.Equal("org/design", model.VoiceDesignSource);
        Assert.Equal("org/clone", model.VoiceCloneSource);
    }

    [AvaloniaFact]
    public void There_is_no_built_in_mirror_configuration()
    {
        // §3a gates it: "A platform ships this only if its mirror publishes
        // manifest.sha256". The project mirror serves the MLX weight set and
        // has no ONNX files, so an entry here would 404 on every file.
        Assert.Empty(NewModel().Configs);
    }

    [AvaloniaFact]
    public void Saving_over_a_name_replaces_rather_than_duplicates()
    {
        var model = NewModel();
        model.NewConfigName = "Mine";
        model.SaveConfigCommand.Execute(null);
        model.NewConfigName = "mine";
        model.SaveConfigCommand.Execute(null);

        Assert.Single(model.Configs);
    }

    [AvaloniaFact]
    public void Deleting_a_configuration_removes_it()
    {
        var model = NewModel();
        model.NewConfigName = "Mine";
        model.SaveConfigCommand.Execute(null);

        model.DeleteConfigCommand.Execute(model.Configs.Single());

        Assert.Empty(model.Configs);
    }

    [AvaloniaFact]
    public void The_models_folder_starts_at_the_default_and_can_be_moved()
    {
        var model = NewModel();
        Assert.False(model.IsCustomModelsFolder);

        model.ChooseModelsFolder = () => Task.FromResult<string?>(_folder);
        model.ChooseFolderCommand.Execute(null);

        Assert.True(model.IsCustomModelsFolder);
        Assert.Equal(_folder, model.ModelsFolder);
    }

    [AvaloniaFact]
    public void The_models_folder_can_be_put_back_to_the_default()
    {
        var model = NewModel();
        model.ChooseModelsFolder = () => Task.FromResult<string?>(_folder);
        model.ChooseFolderCommand.Execute(null);

        model.ResetFolderCommand.Execute(null);

        Assert.False(model.IsCustomModelsFolder);
    }

    [AvaloniaFact]
    public void Cancelling_the_folder_picker_changes_nothing()
    {
        var model = NewModel();
        model.ChooseModelsFolder = () => Task.FromResult<string?>(null);

        model.ChooseFolderCommand.Execute(null);

        Assert.False(model.IsCustomModelsFolder);
    }

    [AvaloniaFact]
    public void Downloaded_models_are_listed_with_their_size()
    {
        // §3d: reclaiming several gigabytes must not require knowing where the
        // app keeps its files.
        var models = Path.Combine(_folder, "models", "org", "repo");
        Directory.CreateDirectory(models);
        File.WriteAllBytes(Path.Combine(models, "model.onnx"), new byte[4096]);

        var model = NewModel();
        model.ChooseModelsFolder = () => Task.FromResult<string?>(_folder);
        model.ChooseFolderCommand.Execute(null);

        var row = Assert.Single(model.Models);
        Assert.Equal("org/repo", row.Name);
        Assert.Contains("4.1 KB", row.SizeText);
        Assert.Contains("1 model", model.StorageSummary);
    }

    [AvaloniaFact]
    public async Task Deleting_a_model_evicts_it_from_memory_first()
    {
        // §3d is explicit, and on Windows it is not merely tidy: a loaded
        // session holds its weights open, so a delete without eviction fails.
        var models = Path.Combine(_folder, "models", "org", "repo");
        Directory.CreateDirectory(models);
        File.WriteAllBytes(Path.Combine(models, "model.onnx"), new byte[1024]);

        var model = NewModel();
        model.ChooseModelsFolder = () => Task.FromResult<string?>(_folder);
        model.ChooseFolderCommand.Execute(null);

        var order = new List<string>();
        model.EvictLoadedModel = () => { order.Add("evict"); return Task.CompletedTask; };
        model.ConfirmDelete = _ => Task.FromResult(true);

        await model.DeleteModelCommand.ExecuteAsync(model.Models.Single());

        Assert.Equal("evict", order.Single());
        Assert.False(Directory.Exists(models));
        Assert.Empty(model.Models);
    }

    [AvaloniaFact]
    public async Task Declining_the_confirmation_keeps_the_model()
    {
        var models = Path.Combine(_folder, "models", "org", "repo");
        Directory.CreateDirectory(models);
        File.WriteAllBytes(Path.Combine(models, "model.onnx"), new byte[1024]);

        var model = NewModel();
        model.ChooseModelsFolder = () => Task.FromResult<string?>(_folder);
        model.ChooseFolderCommand.Execute(null);
        model.ConfirmDelete = _ => Task.FromResult(false);

        await model.DeleteModelCommand.ExecuteAsync(model.Models.Single());

        Assert.True(Directory.Exists(models));
    }

    [AvaloniaFact]
    public void The_pre_download_commands_name_the_real_folder()
    {
        var model = NewModel();
        model.ChooseModelsFolder = () => Task.FromResult<string?>(_folder);
        model.ChooseFolderCommand.Execute(null);

        Assert.Equal(3, model.PreDownloadCommands.Count);
        Assert.Contains(model.PreDownloadCommands, c => c.Contains("hf download") && c.Contains(_folder));
    }

    [AvaloniaFact]
    public void A_mode_on_your_own_server_is_named_rather_than_given_a_broken_command()
    {
        // A command with a URL where a repository id belongs cannot work — the
        // macOS app shipped that once and had to fix it.
        var model = NewModel();
        model.PresetVoiceSource = "https://models.example.com/customvoice";

        Assert.Contains(model.PreDownloadCommands, c => c.Contains("your own server"));
        Assert.DoesNotContain(model.PreDownloadCommands, c => c.Contains("hf download https://"));
    }
}
