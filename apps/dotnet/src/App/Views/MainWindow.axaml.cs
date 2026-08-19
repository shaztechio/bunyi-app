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
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Bunyi.App.ViewModels;
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;

namespace Bunyi.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _waitingToClose;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => WireHistory();
    }

    /// <summary>
    /// Gives History the things only a window has: a clipboard, a save picker
    /// and somewhere to ask a question.
    /// </summary>
    private void WireHistory()
    {
        if (DataContext is not MainViewModel model) return;

        model.ShowReport = report => ShowReportAsync(report, "Cannot generate yet");
        model.History.Clipboard = Clipboard;
        model.History.ConfirmTrash = ConfirmTrashAsync;
        model.History.ChooseSaveLocation = ChooseSaveLocationAsync;
        model.ChooseReference = ChooseReferenceAsync;
        model.FocusRequested += (_, input) => FocusTheProblem(input);
    }

    /// <summary>§2a: Trash after confirming, because the audio may be the only copy.</summary>
    private Task<bool> ConfirmTrashAsync(ViewModels.HistoryRow row) =>
        AskAsync(
            "Move this clip to the Trash?",
            $"“{row.Summary}” goes to the Trash, where you can still get it back.",
            confirm: "Move to Trash",
            cancel: "Keep");

    /// <summary>
    /// Puts the cursor in the field the run is waiting on (spec §1).
    /// </summary>
    /// <remarks>
    /// The view model knows which input is missing; only the window knows which
    /// control holds it. Focus is the half of this that works without sight of
    /// the red outline, so it is not decoration.
    /// </remarks>
    private void FocusTheProblem(RequiredInput input)
    {
        var name = input switch
        {
            RequiredInput.Text => "ScriptBox",
            RequiredInput.Instruction => "InstructBox",
            RequiredInput.Reference => "ChooseRecordingButton",
            RequiredInput.Transcript => "TranscriptBox",
            _ => null,
        };

        if (name is null) return;

        this.FindControl<Control>(name)?.Focus();
    }

    /// <summary>
    /// §4: picks the recording a clone is taken from.
    /// </summary>
    /// <remarks>
    /// The three formats the decoder actually handles are offered, plus an "any
    /// file" escape for the person whose recording has an unusual extension. A
    /// file it cannot read fails with a message naming the formats, which is
    /// better than a filter that hides the file and explains nothing.
    /// </remarks>
    private async Task<string?> ChooseReferenceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a recording of the voice",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.mp3", "*.flac"] },
                new FilePickerFileType("Any file") { Patterns = ["*"] },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// §2a: Download opens a save panel so the user chooses the destination.
    /// </summary>
    private async Task<string?> ChooseSaveLocationAsync(ViewModels.HistoryRow row)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save a copy",
            SuggestedFileName = System.IO.Path.GetFileName(row.Path),
            DefaultExtension = "wav",
            FileTypeChoices = [new FilePickerFileType("Audio") { Patterns = ["*.wav"] }],
        });

        return file?.TryGetLocalPath();
    }

    private ITtsEngine? Engine => (DataContext as MainViewModel)?.Engine;

    /// <summary>
    /// Runs Doctor on demand and shows everything it found (spec §11).
    /// </summary>
    /// <remarks>
    /// Every check, passes included — "everything is fine" is the most common
    /// useful answer, and a dialog that appears only when something is wrong
    /// cannot give it. The same findings go to the log so they can be copied
    /// into a bug report.
    /// </remarks>
    private async void OnDoctorClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;

        var report = await model.RunDoctorAsync();
        if (report is null) return;

        await ShowReportAsync(report, "Doctor");
    }

    /// <summary>
    /// The findings as rows, blockers first.
    /// </summary>
    /// <remarks>
    /// Separated from the dialog so the ordering can be tested without showing
    /// a modal window, which a headless test cannot dismiss.
    /// </remarks>
    internal static StackPanel BuildFindings(DoctorReport report)
    {
        var lines = new StackPanel { Spacing = 10 };

        // Blockers first: what stops the run is what the reader needs.
        foreach (var finding in report.Findings
                     .OrderByDescending(f => f.Severity == DoctorSeverity.Blocker)
                     .ThenByDescending(f => f.Severity == DoctorSeverity.Warning))
        {
            var mark = finding.Severity switch
            {
                DoctorSeverity.Blocker => "✕",
                DoctorSeverity.Warning => "!",
                _ => "✓",
            };

            lines.Children.Add(new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = mark, Width = 14, FontWeight = Avalonia.Media.FontWeight.Bold },
                    new StackPanel
                    {
                        Children =
                        {
                            new TextBlock
                            {
                                Text = finding.Title,
                                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                            },
                            new SelectableTextBlock
                            {
                                Text = finding.Detail,
                                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                Opacity = 0.85,
                                MaxWidth = 420,
                            },
                        },
                    },
                },
            });
        }


        return lines;
    }

    /// <summary>Shows a report, blockers first.</summary>
    internal async Task ShowReportAsync(DoctorReport report, string title)
    {
        var lines = BuildFindings(report);

        var copy = new Button { Content = "Copy", MinWidth = 90 };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90 };

        var dialog = new Window
        {
            Title = $"{title} — {report.Mode.DisplayName()}",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    lines,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { copy, close },
                    },
                },
            },
        };

        copy.Click += async (_, _) =>
        {
            if (Clipboard is null) return;
            var transfer = new Avalonia.Input.DataTransfer();
            transfer.Add(Avalonia.Input.DataTransferItem.CreateText(report.Describe()));
            await Clipboard.SetDataAsync(transfer);
            copy.Content = "Copied";
        };
        close.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Opens the Logs, or brings the open one forward (spec §8).
    /// </summary>
    /// <remarks>
    /// One window, reused. A second Logs window would be a second subscription
    /// to the same store and two lists drifting apart, and there is nothing to
    /// compare between them anyway.
    /// </remarks>
    private LogsWindow? _logs;

    private void OnLogsClicked(object? sender, RoutedEventArgs e)
    {
        if (_logs is not null)
        {
            _logs.Activate();
            return;
        }

        if (DataContext is not MainViewModel { Logs: not null } model) return;

        _logs = new LogsWindow { DataContext = model.Logs };
        _logs.Closed += (_, _) => _logs = null;
        _logs.Show(this);

        // Opened to read the newest thing that happened, so start there.
        _logs.ScrollToEnd();
    }

    /// <summary>Opens Help, or brings the open one forward (spec §10).</summary>
    private HelpWindow? _help;

    private void OnHelpClicked(object? sender, RoutedEventArgs e)
    {
        if (_help is not null)
        {
            _help.Activate();
            return;
        }

        _help = new HelpWindow();
        _help.Closed += (_, _) => _help = null;
        _help.Show(this);
    }

    /// <summary>Opens Settings, or brings the open one forward.</summary>
    private SettingsWindow? _settings;

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        if (DataContext is not MainViewModel { Settings: not null } model) return;

        _settings = new SettingsWindow { DataContext = model.Settings };
        _settings.Closed += (_, _) =>
        {
            _settings = null;

            // A source or folder may have changed under the engine.
            model.Settings!.Reload();
        };
        _settings.Show(this);
    }

    /// <summary>
    /// Busy-close confirmation (spec §9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confirming stops the work first and closes once it has actually
    /// stopped — not both at once. Cancellation is cooperative, so a window
    /// that closed on confirmation would disappear while the engine was still
    /// generating and still holding the model, leaving the app with no window
    /// and visible work.
    /// </para>
    /// <para>
    /// A timeout closes anyway rather than trapping the user in a window that
    /// will not shut, and the prompt says so — one promising to close "once it
    /// has stopped" would be a lie in exactly the case the timeout exists for.
    /// Pressing close again during the wait does nothing: it must not ask twice
    /// or start a second stop.
    /// </para>
    /// </remarks>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        var engine = Engine;

        if (_closeConfirmed || engine is null || !engine.Status.IsBusy)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        // Already waiting: do nothing at all, rather than ask twice.
        if (_waitingToClose) return;

        var keepWorking = await ConfirmAsync();
        if (keepWorking) return;

        _waitingToClose = true;
        engine.RequestStop();

        await engine.WaitForIdleAsync(TimeSpan.FromSeconds(15));

        _closeConfirmed = true;
        Close();
    }

    /// <summary>Returns whether the user chose to keep working.</summary>
    private Task<bool> ConfirmAsync() =>
        AskAsync(
            "Stop the current operation?",
            "Bunyi is still working. Stopping will discard what it is doing. "
            + "The window closes once it has stopped, or after 15 seconds.",
            confirm: "Stop and Close",
            cancel: "Keep Working",
            invert: true);

    /// <summary>
    /// A two-button question.
    /// </summary>
    /// <param name="invert">
    /// True when the returned value should mean "cancelled" rather than
    /// "confirmed" — §9 wants Keep Working as the safe default and the
    /// destructive choice as the other one.
    /// </param>
    private async Task<bool> AskAsync(
        string title, string message, string confirm, string cancel, bool invert = false)
    {
        var cancelButton = new Button { Content = cancel, IsDefault = true, MinWidth = 120 };
        var confirmButton = new Button { Content = confirm, IsCancel = true, MinWidth = 120 };

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

        // The safe choice is the default and the destructive one is not, and
        // dismissing the dialog counts as the safe choice (spec §9).
        var result = new TaskCompletionSource<bool>();
        cancelButton.Click += (_, _) => { result.TrySetResult(invert); dialog.Close(); };
        confirmButton.Click += (_, _) => { result.TrySetResult(!invert); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(invert);

        await dialog.ShowDialog(this);
        return await result.Task;
    }
}
