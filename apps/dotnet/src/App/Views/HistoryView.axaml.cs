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
using Avalonia.Markup.Xaml;

namespace Bunyi.App.Views;

public partial class HistoryView : UserControl
{
    /// <summary>
    /// How far one arrow press moves, when no row is realised to measure.
    /// </summary>
    /// <remarks>
    /// A row is about 57 pixels — 36 of button and the padding around it — so
    /// this is within a few pixels of one row either way. It only applies to a
    /// list whose first row has not been laid out yet, which is not a list
    /// anyone is scrolling.
    /// </remarks>
    private const double FallbackRowHeight = 56;

    public HistoryView()
    {
        AvaloniaXamlLoader.Load(this);

        // Bubbling, so it hears a key pressed on any of the row buttons — those
        // are what Tab reaches, and there is no other focusable thing inside.
        // Registered here rather than in the ScrollViewer's own KeyDown so a
        // handled key never reaches the window's shortcuts.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>
    /// The keys Avalonia's ScrollViewer does not give a focused descendant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured headlessly with thirty rows and focus on a row button: Page
    /// Down pages the list (the ScrollViewer handles it from a descendant),
    /// and Tab through the rows brings each into view. Home, End and the
    /// arrows did nothing at all. Spec §12: History holds every clip a person
    /// has made, and reaching the older ones must not require a trackpad —
    /// End is the one that reaches the oldest in a single press.
    /// </para>
    /// <para>
    /// Only when the key is otherwise unhandled, so a control that wants the
    /// arrows for itself keeps them. Nothing in a row does today.
    /// </para>
    /// </remarks>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        if (this.FindControl<ScrollViewer>("List") is not { } list) return;

        var bottom = Math.Max(0, list.Extent.Height - list.Viewport.Height);
        var row = RowHeight(list);

        var target = e.Key switch
        {
            Key.Home => 0,
            Key.End => bottom,
            Key.Down => Math.Min(bottom, list.Offset.Y + row),
            Key.Up => Math.Max(0, list.Offset.Y - row),
            _ => double.NaN,
        };

        if (double.IsNaN(target)) return;

        list.Offset = list.Offset.WithY(target);
        e.Handled = true;
    }

    /// <summary>One row, measured from the first one on screen.</summary>
    private static double RowHeight(ScrollViewer list) =>
        list.Content is ItemsControl items
        && items.ContainerFromIndex(0) is { Bounds.Height: > 0 } first
            ? first.Bounds.Height
            : FallbackRowHeight;
}
