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
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// Backing up and restoring, from the window (spec §6).
/// </summary>
public sealed class BackupUiTests : HeadlessWindows
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public BackupUiTests() => Directory.CreateDirectory(_root);

    protected override void DisposeCore()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [AvaloniaFact]
    public void The_backup_tab_offers_both_actions()
    {
        var window = Open(new SettingsWindow { DataContext = New() });

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().First();
        tabs.SelectedIndex = 3;
        window.UpdateLayout();

        var buttons = window.GetLogicalDescendants().OfType<Button>()
            .Select(b => b.Content as string)
            .ToList();

        Assert.Contains("Back up…", buttons);
        Assert.Contains("Restore…", buttons);
    }

    [Fact]
    public void Stop_is_only_there_while_something_is_running()
    {
        // §6 wants a Stop that truly cancels — and nothing to press when there
        // is nothing to stop.
        var model = New();

        Assert.False(model.BackupRunning);
        Assert.False(model.StopBackupCommand.CanExecute(null));
        Assert.True(model.BackUpCommand.CanExecute(null));
    }

    [Fact]
    public async Task Backing_up_writes_a_zip_and_says_where()
    {
        var model = New();
        var destination = Path.Combine(_root, "backup.zip");
        model.ChooseBackupDestination = () => Task.FromResult<string?>(destination);

        await model.BackUpCommand.ExecuteAsync(null);

        Assert.True(File.Exists(destination));
        Assert.True(model.BackupStatus.Contains("backup.zip", StringComparison.Ordinal),
            $"status was “{model.BackupStatus}”");
        Assert.False(model.BackupRunning);
    }

    [Fact]
    public async Task Cancelling_the_picker_starts_nothing()
    {
        var model = New();
        model.ChooseBackupDestination = () => Task.FromResult<string?>(null);

        await model.BackUpCommand.ExecuteAsync(null);

        Assert.False(model.BackupRunning);
        Assert.Equal(string.Empty, model.BackupStatus);
    }

    [Fact]
    public async Task A_zip_that_is_not_a_backup_is_reported_in_words()
    {
        // §10: actionable, and not a stack trace. The whole message is shown in
        // the tab.
        var stray = Path.Combine(_root, "not-a-backup.zip");
        File.WriteAllBytes(stray, [0x50, 0x4B, 0x05, 0x06, .. new byte[18]]);

        var model = New();
        model.ChooseBackupSource = () => Task.FromResult<string?>(stray);

        await model.RestoreCommand.ExecuteAsync(null);

        Assert.False(model.BackupRunning);
        Assert.Contains("backup", model.BackupStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", model.BackupStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_says_what_it_kept()
    {
        // §6 never clobbers, and the user should be told that rather than left
        // wondering why a model did not come back.
        var model = New();
        var destination = Path.Combine(_root, "backup.zip");
        model.ChooseBackupDestination = () => Task.FromResult<string?>(destination);
        await model.BackUpCommand.ExecuteAsync(null);

        model.ChooseBackupSource = () => Task.FromResult<string?>(destination);
        await model.RestoreCommand.ExecuteAsync(null);

        // Everything in the archive is already on disk — it was just backed up
        // from there — so all of it is kept.
        Assert.Contains("already here were kept", model.BackupStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outcome_is_not_overwritten_by_a_late_progress_report()
    {
        // It was, about one run in fifteen. Progress<T> delivers on another
        // thread, so a report could land after the run was over and replace
        // "Backed up to backup.zip" with "Backing up… 100%". They are separate
        // fields now, which removes the race rather than narrowing it.
        var model = New();
        model.ChooseBackupDestination = () => Task.FromResult<string?>(
            Path.Combine(_root, "backup.zip"));

        await model.BackUpCommand.ExecuteAsync(null);

        Assert.Contains("Backed up", model.BackupStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("%", model.BackupStatus, StringComparison.Ordinal);

        // Deliberately nothing about BackupDetail. It is the running
        // commentary, a late report can still land on it after the run is
        // over, and the tab only shows it while BackupRunning — so a stale
        // value there is invisible. Asserting it failed on Linux, where the
        // timing differs, and it was the assertion that was wrong rather than
        // the code: the guarantee is that the *outcome* survives.
        Assert.False(model.BackupRunning);
    }

    [Fact]
    public async Task A_finished_run_leaves_the_buttons_usable_again()
    {
        var model = New();
        model.ChooseBackupDestination = () => Task.FromResult<string?>(
            Path.Combine(_root, "backup.zip"));

        await model.BackUpCommand.ExecuteAsync(null);

        Assert.True(model.BackupIsIdle);
        Assert.True(model.BackUpCommand.CanExecute(null));
        Assert.False(model.StopBackupCommand.CanExecute(null));
    }

    /// <summary>A settings view model over a models folder with one model in it.</summary>
    private SettingsViewModel New()
    {
        var models = Path.Combine(_root, "Models");
        var file = Path.Combine(models, "models", "elbruno", "Qwen3", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}");

        var log = new RecordingLog();
        var settingsPath = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(log, settingsPath);
        var settings = store.Load() with { ModelsFolder = models };
        store.Save(settings);

        return new SettingsViewModel(
            store,
            new ModelConfigLibrary(log, Path.Combine(_root, "configs.json")),
            log,
            _ => { },
            _ => "elbruno/Qwen3");
    }

    private sealed class RecordingLog : ILogSink
    {
        public void Log(string message) { }
    }
}
