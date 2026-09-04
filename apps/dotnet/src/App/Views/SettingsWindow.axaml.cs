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
using Avalonia.Input;
using Bunyi.App.Infrastructure;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Bunyi.App.ViewModels;

namespace Bunyi.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Wire();
    }

    private void Wire()
    {
        if (DataContext is not SettingsViewModel model) return;

        model.ChooseModelsFolder = ChooseModelsFolderAsync;
        model.ChooseBackupDestination = ChooseBackupDestinationAsync;
        model.ChooseBackupSource = ChooseBackupSourceAsync;
        model.ConfirmDelete = ConfirmDeleteAsync;
    }

    /// <summary>
    /// The window title names the window and then the selected tab (spec §7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Settings — General", not "General". The tab alone is the macOS
    /// convention and this window used to copy it, which cost a screen-reader
    /// user the one thing that confirms the chord worked: pressing Ctrl+, with
    /// Narrator on announced "General", a word appearing nowhere in what they
    /// asked for (#196).
    /// </para>
    /// <para>
    /// It has to be the title rather than an accessible name set beside it.
    /// Avalonia's <c>WindowAutomationPeer.GetNameCore()</c> is
    /// <c>=> Owner.Title</c> — it overrides the usual lookup and ignores
    /// <c>AutomationProperties.Name</c> entirely, so for a window the title
    /// <i>is</i> the announcement and there is nowhere else to put this.
    /// </para>
    /// </remarks>
    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TabControl { SelectedItem: TabItem { Header: string header } })
        {
            Title = $"Settings — {header}";
        }
    }

    /// <summary>
    /// Puts the version and platform on the clipboard (spec §9a).
    /// </summary>
    /// <remarks>
    /// The tab asks people to quote these in a bug report, so taking them has
    /// to be one click. Reading a version off the screen and typing it back in
    /// is how the wrong one ends up in the report.
    /// </remarks>
    private async void CopyVersion(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Clipboard is null) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText($"{AboutInfo.Name} {AboutInfo.VersionLine}"));
        await Clipboard.SetDataAsync(transfer);

        // §2a's acknowledgement rule, which applies to any copy: one that says
        // nothing is indistinguishable from one that failed.
        if (this.FindControl<Button>("CopyVersionButton") is { } button)
        {
            ToolTip.SetTip(button, "Copied");
        }
    }

    /// <summary>§6: where to write the backup.</summary>
    private async Task<string?> ChooseBackupDestinationAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save a backup of your models",
            SuggestedFileName = $"Bunyi models {DateTime.Now:yyyy-MM-dd}",
            DefaultExtension = "zip",
            FileTypeChoices = [new FilePickerFileType("Backup") { Patterns = ["*.zip"] }],
        });

        return file?.TryGetLocalPath();
    }

    /// <summary>§6: which backup to restore from.</summary>
    private async Task<string?> ChooseBackupSourceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a backup to restore",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Backup") { Patterns = ["*.zip"] }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> ChooseModelsFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should Bunyi keep its models?",
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// §3d: deleting a model moves it to the Trash after confirming. Several
    /// gigabytes are worth a question first.
    /// </summary>
    private Task<bool> ConfirmDeleteAsync(DownloadedModelRow row) =>
        AskAsync(
            "Delete this model?",
            $"{row.Name} ({row.SizeText}) goes to the Trash. Bunyi will download it "
            + "again the next time a mode that uses it runs.",
            confirm: "Delete",
            cancel: "Keep");

    internal async Task<bool> AskAsync(string title, string message, string confirm, string cancel)
    {
        // Keep answers both Return and Escape; Delete answers neither. IsCancel
        // means "Escape presses this" - it had been on Delete, so Escape on
        // "Delete this model?" deleted the model. Spec §12: Escape dismisses
        // without acting.
        var cancelButton = new Button { Content = cancel, IsDefault = true, IsCancel = true, MinWidth = 110 };
        var confirmButton = new Button { Content = confirm, MinWidth = 110 };

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { confirmButton, cancelButton },
                    },
                },
            },
        };

        // Keeping is the safe default, and dismissing the dialog keeps.
        var result = new TaskCompletionSource<bool>();
        cancelButton.Click += (_, _) => { result.TrySetResult(false); dialog.Close(); };
        confirmButton.Click += (_, _) => { result.TrySetResult(true); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(false);

        await dialog.ShowDialog(this);
        return await result.Task;
    }
}
