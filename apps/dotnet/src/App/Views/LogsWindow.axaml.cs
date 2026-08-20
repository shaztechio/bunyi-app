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

using Avalonia;
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

        if (this.FindControl<SelectableTextBlock>("LogText") is { } block)
        {
            block.PropertyChanged += OnSelectionChanged;
        }

        DataContextChanged += (_, _) => Attach();
        Attach();
    }

    private LogsViewModel? _attached;

    /// <summary>Whether the block is showing text older than the view model's.</summary>
    private bool _textIsStale;

    /// <summary>Guards the re-entry caused by collapsing the selection below.</summary>
    private bool _applying;

    private void Attach()
    {
        if (_attached is not null)
        {
            _attached.LinesAppended -= OnLinesAppended;
            _attached.PropertyChanged -= OnModelChanged;
        }

        _attached = DataContext as LogsViewModel;

        if (_attached is not null)
        {
            _attached.LinesAppended += OnLinesAppended;
            _attached.PropertyChanged += OnModelChanged;
        }

        ShowText();
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.Document)) ShowText();
    }

    /// <summary>
    /// Puts the view model's text on screen, unless something is selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replacing the text drops any selection in it. A live log rewrites itself
    /// several times a second during a generation, so binding it directly would
    /// clear a selection the moment someone made one — which is exactly when
    /// they are trying to copy a line out to report it. The update waits for
    /// them to let go, and <see cref="OnSelectionChanged" /> applies it then.
    /// </para>
    /// <para>
    /// Clear is the exception, and has to be: §8 offers it as the way to get
    /// rid of what is there, so text that outlived it would be the window
    /// showing lines the store no longer has — and holding it back would leave
    /// no way to clear a log at all while anything was selected.
    /// </para>
    /// </remarks>
    private void ShowText()
    {
        var block = this.FindControl<SelectableTextBlock>("LogText");
        if (block is null || _attached is null || _applying) return;

        var cleared = _attached.Document.Length == 0;

        if (!cleared && block.SelectionStart != block.SelectionEnd)
        {
            _textIsStale = true;
            return;
        }

        _applying = true;
        try
        {
            // The selection is dropped with the text it pointed into, rather
            // than left addressing offsets that no longer exist.
            if (cleared)
            {
                block.SelectionStart = 0;
                block.SelectionEnd = 0;
            }

            block.Text = _attached.Document;
            _textIsStale = false;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Catches up the text once a selection is released.</summary>
    private void OnSelectionChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != SelectableTextBlock.SelectionStartProperty
            && e.Property != SelectableTextBlock.SelectionEndProperty)
        {
            return;
        }

        if (_textIsStale && sender is SelectableTextBlock { } block
            && block.SelectionStart == block.SelectionEnd)
        {
            ShowText();
        }
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
