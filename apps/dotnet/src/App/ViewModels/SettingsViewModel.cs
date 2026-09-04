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

    /// <summary>Spec §3e: whether leaving a mode unloads its model.</summary>
    [ObservableProperty] private bool _unloadOnModeSwitch = true;
    [ObservableProperty] private string _presetVoiceSource = string.Empty;
    [ObservableProperty] private string _voiceDesignSource = string.Empty;
    [ObservableProperty] private string _voiceCloneSource = string.Empty;
    [ObservableProperty] private string _newConfigName = string.Empty;

    /// <summary>
    /// What each box falls back to when it is empty — shown as its placeholder.
    /// </summary>
    /// <remarks>
    /// The repo itself, not the words "Built-in default", which is what these
    /// said before. Clearing a box means "use the default", so the one moment a
    /// person needs to know what that default *is* is exactly when the box is
    /// empty and the placeholder is showing. Naming it also makes the box
    /// self-documenting for anyone comparing a mirror against the original, or
    /// typing a variant of it.
    ///
    /// macOS has done this since it shipped — <c>prompt: Text(mode.repoID)</c>
    /// in <c>SettingsView.swift</c> — so this is parity, not invention.
    /// </remarks>
    public string PresetVoiceDefault => _defaultSourceFor(TtsMode.PresetVoice);

    /// <inheritdoc cref="PresetVoiceDefault"/>
    public string VoiceDesignDefault => _defaultSourceFor(TtsMode.VoiceDesign);

    /// <inheritdoc cref="PresetVoiceDefault"/>
    public string VoiceCloneDefault => _defaultSourceFor(TtsMode.VoiceClone);
    [ObservableProperty] private string _modelsFolder = string.Empty;
    [ObservableProperty] private string _storageSummary = string.Empty;

    /// <summary>How far a backup or restore has got, 0 to 1 (spec §6).</summary>
    [ObservableProperty] private double _backupProgress;

    /// <summary>
    /// How the last backup or restore went.
    /// </summary>
    /// <remarks>
    /// Only ever written when one finishes, is stopped, or fails. Progress
    /// writes to <see cref="BackupDetail" /> instead, and the two are separate
    /// on purpose: <c>Progress&lt;T&gt;</c> delivers on another thread, so a
    /// report can arrive after the run is over. Sharing one field meant
    /// "Backed up to backup.zip" could be overwritten by a late
    /// "Backing up… 100%" — about one run in fifteen.
    /// </remarks>
    [ObservableProperty] private string _backupStatus = string.Empty;

    /// <summary>
    /// The running commentary, shown only while one is going.
    /// </summary>
    /// <remarks>
    /// Transient by design. A report can still arrive here after the run is
    /// over — <c>Progress&lt;T&gt;</c> delivers on another thread — and that is
    /// harmless precisely because the tab shows this only while
    /// <see cref="BackupRunning" />. The outcome lives in
    /// <see cref="BackupStatus" />, which progress never writes to.
    /// </remarks>
    [ObservableProperty] private string _backupDetail = string.Empty;

    /// <summary>Whether one is running, which is what Stop is for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackupIsIdle))]
    [NotifyCanExecuteChangedFor(nameof(BackUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopBackupCommand))]
    private bool _backupRunning;

    /// <summary>Whether the buttons that start one can be pressed.</summary>
    public bool BackupIsIdle => !BackupRunning;

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

    /// <summary>Asks the window where to write a backup (spec §6).</summary>
    public Func<Task<string?>>? ChooseBackupDestination { get; set; }

    /// <summary>Asks the window which backup to restore (spec §6).</summary>
    public Func<Task<string?>>? ChooseBackupSource { get; set; }

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
        UnloadOnModeSwitch = _settings.UnloadOnModeSwitch;
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

    /// <summary>
    /// Spec §3e. Nothing is loaded or unloaded here: the setting only says
    /// what the next mode switch should do, and acting on it now would unload
    /// the model of the mode the user is about to go back to.
    /// </summary>
    partial void OnUnloadOnModeSwitchChanged(bool value)
    {
        if (_loading) return;
        Persist(_settings with { UnloadOnModeSwitch = value });
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

    private CancellationTokenSource? _backupCancel;

    /// <summary>
    /// Collects the models folder into one zip (spec §6).
    /// </summary>
    /// <remarks>
    /// The point is not tidiness. A models folder is gigabytes fetched over a
    /// slow link, and the alternative on the next machine is fetching them
    /// again.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(BackupIsIdle))]
    private async Task BackUpAsync()
    {
        if (ChooseBackupDestination is null) return;

        var destination = await ChooseBackupDestination();
        if (string.IsNullOrWhiteSpace(destination)) return;

        await RunAsync(
            "Backing up",
            async (backup, progress, ct) =>
            {
                await backup.BackupAsync(
                    _store.ResolveModelsFolder(_settings), destination, progress, ct);

                return $"Backed up to {Path.GetFileName(destination)}.";
            });
    }

    /// <summary>
    /// Merges a backup into the models folder (spec §6).
    /// </summary>
    [RelayCommand(CanExecute = nameof(BackupIsIdle))]
    private async Task RestoreAsync()
    {
        if (ChooseBackupSource is null) return;

        var source = await ChooseBackupSource();
        if (string.IsNullOrWhiteSpace(source)) return;

        await RunAsync(
            "Restoring",
            async (backup, progress, ct) =>
            {
                var skipped = await backup.RestoreAsync(
                    source, _store.ResolveModelsFolder(_settings), progress, ct);

                Reload();

                return skipped.Count == 0
                    ? "Restored everything in that backup."
                    : $"Restored what was missing. {skipped.Count} model(s) already here were kept.";
            });
    }

    /// <summary>Stops a backup or restore, for real (spec §6).</summary>
    [RelayCommand(CanExecute = nameof(BackupRunning))]
    private void StopBackup()
    {
        BackupDetail = "Stopping…";
        _backupCancel?.Cancel();
    }

    /// <summary>
    /// Runs one of the two, with the progress, cancelling and reporting they
    /// share.
    /// </summary>
    /// <remarks>
    /// Written once because the difference between them is one call. Both are
    /// long, both must not block the window, and both have to say something
    /// useful whether they finish, are stopped, or fail.
    /// </remarks>
    private async Task RunAsync(
        string what,
        Func<BackupManager, IProgress<Bunyi.Core.BackupProgress>, CancellationToken, Task<string>> work)
    {
        _backupCancel?.Dispose();
        _backupCancel = new CancellationTokenSource();

        BackupRunning = true;
        BackupProgress = 0;
        BackupDetail = $"{what}…";
        BackupStatus = string.Empty;

        // Every run gets a number, and a report from an older one is ignored.
        // Progress<T> posts to the captured context rather than running inline,
        // so a report can be delivered *after* the run has finished and
        // overwrite "Backed up to backup.zip" with "Backing up… 100%". The
        // engine hit the same thing with downloads and left a comment about it;
        // this is that comment being useful.
        var progress = new System.Progress<Bunyi.Core.BackupProgress>(p =>
        {
            BackupProgress = p.Fraction;
            BackupDetail = $"{what}… {p.Fraction:P0}";
        });

        try
        {
            // Off the window's thread: this reads and writes gigabytes, and §6
            // is explicit that it must never block the UI.
            var message = await Task.Run(
                () => work(new BackupManager(_log), progress, _backupCancel.Token),
                _backupCancel.Token);

            BackupProgress = 1;
            BackupStatus = message;
        }
        catch (OperationCanceledException)
        {
            BackupProgress = 0;
            BackupStatus = $"{what} stopped. Nothing was left half-finished.";
        }
        catch (Exception ex)
        {
            BackupProgress = 0;
            BackupStatus = ex.Message;
            _log.Log($"{what} failed: {ex}");
        }
        finally
        {
            BackupRunning = false;
            BackupDetail = string.Empty;
        }
    }

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
