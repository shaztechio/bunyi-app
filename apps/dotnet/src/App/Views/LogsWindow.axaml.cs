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
using Avalonia.Interactivity;
using Bunyi.App.ViewModels;

namespace Bunyi.App.Views;

/// <summary>
/// The Logs window (spec §8).
/// </summary>
public partial class LogsWindow : Window
{
    /// <summary>
    /// How close to the bottom still counts as "at the bottom", in pixels.
    /// </summary>
    /// <remarks>
    /// Not an exact comparison: a partly-visible last line, or a scroll
    /// position a fraction of a pixel short, would otherwise read as "the user
    /// scrolled up" and stop the autoscroll for good.
    /// </remarks>
    internal const double AtBottomSlack = 24;

    public LogsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Attach();
    }

    private LogsViewModel? _attached;

    private void Attach()
    {
        if (_attached is not null) _attached.LinesAppended -= OnLinesAppended;

        _attached = DataContext as LogsViewModel;
        if (_attached is not null) _attached.LinesAppended += OnLinesAppended;
    }

    private void OnLinesAppended(object? sender, EventArgs e)
    {
        // §8 asks for autoscroll — but only while the view is already at the
        // bottom. Yanking the view back down while someone is reading further
        // up is the behaviour that makes a live log useless exactly when it
        // matters, which is during the long run they are trying to read.
        if (IsAtBottom()) ScrollToEnd();
    }

    /// <summary>Whether the view is close enough to the bottom to follow it.</summary>
    internal bool IsAtBottom()
    {
        var scroller = this.FindControl<ScrollViewer>("Scroller");
        if (scroller is null) return true;

        var room = scroller.Extent.Height - scroller.Viewport.Height;

        // Nothing to scroll yet: a short log is always "at the bottom".
        if (room <= 0) return true;

        return scroller.Offset.Y >= room - AtBottomSlack;
    }

    /// <summary>Scrolls to the newest line.</summary>
    internal void ScrollToEnd() => this.FindControl<ScrollViewer>("Scroller")?.ScrollToEnd();

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel model || Clipboard is null) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(model.Text()));
        await Clipboard.SetDataAsync(transfer);

        // §2a's acknowledgement rule: a copy that says nothing is
        // indistinguishable from a copy that failed.
        if (this.FindControl<Button>("CopyButton") is { } button) button.Content = "Copied";
    }
}
