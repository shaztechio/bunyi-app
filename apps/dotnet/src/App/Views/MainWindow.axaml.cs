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
using Bunyi.App.ViewModels;
using Bunyi.Core.Engine;

namespace Bunyi.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private bool _waitingToClose;

    public MainWindow() => AvaloniaXamlLoader.Load(this);

    private ITtsEngine? Engine => (DataContext as MainViewModel)?.Engine;

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
    private async Task<bool> ConfirmAsync()
    {
        var keepWorking = new Button { Content = "Keep Working", IsDefault = true, MinWidth = 120 };
        var stopAndClose = new Button { Content = "Stop and Close", IsCancel = true, MinWidth = 120 };

        var dialog = new Window
        {
            Title = "Stop the current operation?",
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
                        Text = "Bunyi is still working. Stopping will discard what it is "
                             + "doing. The window closes once it has stopped, or after "
                             + "15 seconds.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { stopAndClose, keepWorking },
                    },
                },
            },
        };

        // Keep Working is the safe default; Stop and Close is the destructive
        // one (spec §9).
        var result = new TaskCompletionSource<bool>();
        keepWorking.Click += (_, _) => { result.TrySetResult(true); dialog.Close(); };
        stopAndClose.Click += (_, _) => { result.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => result.TrySetResult(true);

        await dialog.ShowDialog(this);
        return await result.Task;
    }
}
