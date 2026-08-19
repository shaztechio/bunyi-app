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

using System.Collections.ObjectModel;
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Models;
using Bunyi.Core.Platform;
using Bunyi.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bunyi.App.ViewModels;

/// <summary>One model on disk, in the Storage tab (spec §3d).</summary>
public sealed record DownloadedModelRow(DownloadedModel Model)
{
    public string Name => Model.Name;
    public string SizeText => Model.SizeText();
    public string OriginText => Model.OriginText();
}

/// <summary>
/// Settings (spec §7): General, Models, Storage and Backup.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ModelConfigLibrary _configs;
    private readonly ILogSink _log;
    private readonly Action<Appearance> _applyAppearance;
    private readonly Func<TtsMode, string> _defaultSourceFor;

    private AppSettings _settings;
    private bool _loading;

    [ObservableProperty] private Appearance _appearance;
    [ObservableProperty] private string _presetVoiceSource = string.Empty;
    [ObservableProperty] private string _voiceDesignSource = string.Empty;
    [ObservableProperty] private string _voiceCloneSource = string.Empty;
    [ObservableProperty] private string _newConfigName = string.Empty;
    [ObservableProperty] private string _modelsFolder = string.Empty;
    [ObservableProperty] private string _storageSummary = string.Empty;

    public SettingsViewModel(
        SettingsStore store,
        ModelConfigLibrary configs,
        ILogSink log,
        Action<Appearance> applyAppearance,
        Func<TtsMode, string> defaultSourceFor)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _applyAppearance = applyAppearance ?? throw new ArgumentNullException(nameof(applyAppearance));
        _defaultSourceFor = defaultSourceFor ?? throw new ArgumentNullException(nameof(defaultSourceFor));

        _settings = _store.Load();
        Reload();
    }

    /// <summary>System, Light and Dark (spec §7).</summary>
    public IReadOnlyList<Appearance> Appearances { get; } =
        [Appearance.System, Appearance.Light, Appearance.Dark];

    /// <summary>Saved configurations (spec §3a).</summary>
    public ObservableCollection<ModelConfig> Configs { get; } = [];

    /// <summary>What is on disk (spec §3d).</summary>
    public ObservableCollection<DownloadedModelRow> Models { get; } = [];

    /// <summary>Copyable pre-download commands, one per mode that has one.</summary>
    public ObservableCollection<string> PreDownloadCommands { get; } = [];

    /// <summary>Whether the models folder is somewhere the user chose.</summary>
    public bool IsCustomModelsFolder => !string.IsNullOrWhiteSpace(_settings.ModelsFolder);

    /// <summary>Asks the user to pick a folder, supplied by the view.</summary>
    public Func<Task<string?>>? ChooseModelsFolder { get; set; }

    /// <summary>Asks the user to confirm deleting a model, supplied by the view.</summary>
    public Func<DownloadedModelRow, Task<bool>>? ConfirmDelete { get; set; }

    /// <summary>
    /// Called before a model's files are removed, so it can be evicted first.
    /// </summary>
    /// <remarks>
    /// §3d: "Deleting the model that is currently loaded evicts it from memory
    /// first; otherwise the app keeps generating from a model whose files are
    /// gone and silently re-downloads on next launch." On Windows this is not
    /// merely tidy — a loaded ONNX session holds its <c>.onnx.data</c> open, and
    /// the delete fails outright.
    /// </remarks>
    public Func<Task>? EvictLoadedModel { get; set; }

    /// <summary>Re-reads everything that can change outside this window.</summary>
    public void Reload()
    {
        _loading = true;

        _settings = _store.Load();
        Appearance = _settings.Appearance;
        PresetVoiceSource = _settings.SourceFor(TtsMode.PresetVoice);
        VoiceDesignSource = _settings.SourceFor(TtsMode.VoiceDesign);
        VoiceCloneSource = _settings.SourceFor(TtsMode.VoiceClone);
        ModelsFolder = _store.ResolveModelsFolder(_settings);

        _configs.Load();
        Configs.Clear();
        foreach (var config in _configs.Configs) Configs.Add(config);

        RefreshStorage();

        _loading = false;
    }

    private void RefreshStorage()
    {
        var root = _store.ResolveModelsFolder(_settings);

        Models.Clear();
        foreach (var model in DownloadedModels.Read(root)) Models.Add(new DownloadedModelRow(model));

        var total = DownloadedModels.TotalBytes(root);
        StorageSummary = Models.Count == 0
            ? "No models downloaded yet."
            : $"{Models.Count} model{(Models.Count == 1 ? "" : "s")}, {DownloadProgress.Bytes(total)} in total.";

        PreDownloadCommands.Clear();
        foreach (var mode in new[] { TtsMode.PresetVoice, TtsMode.VoiceDesign, TtsMode.VoiceClone })
        {
            var source = ModelSource.Parse(_settings.SourceFor(mode), _defaultSourceFor(mode));
            var command = DownloadedModels.PreDownloadCommand(source, root);

            // A mode on the user's own server is named as such rather than
            // given a command with a URL where a repo id belongs.
            PreDownloadCommands.Add(command ?? $"{mode.DisplayName()}: served from your own server.");
        }

        OnPropertyChanged(nameof(IsCustomModelsFolder));
    }

    /// <summary>
    /// Applies the appearance to every window immediately (spec §7).
    /// </summary>
    partial void OnAppearanceChanged(Appearance value)
    {
        if (_loading) return;

        _applyAppearance(value);
        Persist(_settings with { Appearance = value });
    }

    partial void OnPresetVoiceSourceChanged(string value) => PersistSource(TtsMode.PresetVoice, value);
    partial void OnVoiceDesignSourceChanged(string value) => PersistSource(TtsMode.VoiceDesign, value);
    partial void OnVoiceCloneSourceChanged(string value) => PersistSource(TtsMode.VoiceClone, value);

    private void PersistSource(TtsMode mode, string value)
    {
        if (_loading) return;
        Persist(_settings.WithSourceFor(mode, value));
        RefreshStorage();
    }

    private void Persist(AppSettings settings)
    {
        _settings = settings;
        _store.Save(settings);
    }

    /// <summary>Saves the three sources under a name (spec §3a).</summary>
    /// <summary>
    /// Opens one of the credits links in the user's own browser (spec §9a).
    /// </summary>
    /// <remarks>
    /// The URLs come from a compiled-in list, and <see cref="WebLink" /> still
    /// checks each one is https before handing it to a shell handler. Nothing
    /// else in the app opens a link, so the whole surface is this method and
    /// that list.
    /// </remarks>
    [RelayCommand]
    private void OpenLink(string? url) => WebLink.Open(url, _log);

    [RelayCommand]
    private void SaveConfig()
    {
        if (string.IsNullOrWhiteSpace(NewConfigName)) return;

        _configs.Save(NewConfigName, PresetVoiceSource, VoiceDesignSource, VoiceCloneSource);
        NewConfigName = string.Empty;
        Reload();
    }

    /// <summary>Applies a saved configuration to all three modes at once.</summary>
    [RelayCommand]
    private void UseConfig(ModelConfig? config)
    {
        if (config is null) return;

        // Set together, because they belong together: switching between the Hub
        // and a mirror means changing all three, and each must match its mode
        // or the app loads a model that runs and produces nonsense.
        PresetVoiceSource = config.PresetVoice;
        VoiceDesignSource = config.VoiceDesign;
        VoiceCloneSource = config.VoiceClone;
        _log.Log($"Using the model configuration “{config.Name}”.");
    }

    [RelayCommand]
    private void DeleteConfig(ModelConfig? config)
    {
        if (config is null) return;
        _configs.Delete(config);
        Reload();
    }

    /// <summary>Clears all three back to the built-in defaults (spec §3a).</summary>
    [RelayCommand]
    private void ResetSources()
    {
        PresetVoiceSource = string.Empty;
        VoiceDesignSource = string.Empty;
        VoiceCloneSource = string.Empty;
        _log.Log("Reset every model source to its default.");
    }

    /// <summary>Points the models folder somewhere else (spec §3d).</summary>
    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (ChooseModelsFolder is null) return;

        var folder = await ChooseModelsFolder();
        if (string.IsNullOrWhiteSpace(folder)) return;

        Persist(_settings with { ModelsFolder = folder });
        _log.Log($"Models will be kept in {folder}.");
        Reload();
    }

    /// <summary>Puts the models folder back to the default (spec §3d).</summary>
    [RelayCommand]
    private void ResetFolder()
    {
        Persist(_settings with { ModelsFolder = null });
        _log.Log("Models will be kept in the default folder.");
        Reload();
    }

    [RelayCommand]
    private void ShowFolder() => FileReveal.Reveal(ModelsFolder, _log);

    /// <summary>Deletes a downloaded model, after confirming (spec §3d).</summary>
    [RelayCommand]
    private async Task DeleteModelAsync(DownloadedModelRow? row)
    {
        if (row is null || ConfirmDelete is null) return;
        if (!await ConfirmDelete(row)) return;

        // Evict before removing the files, not after.
        if (EvictLoadedModel is not null) await EvictLoadedModel();

        DownloadedModels.TryDelete(row.Model, _log);
        Reload();
    }
}
