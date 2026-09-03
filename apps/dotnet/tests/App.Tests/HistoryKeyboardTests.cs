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
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// History can be reached and scrolled from the keyboard (spec §12).
/// </summary>
/// <remarks>
/// <para>
/// #157 measured on macOS that nothing in History moved from the keyboard at
/// all, and inferred the same for this app because both are a scroll container
/// with no selection. Measured here instead of inferred: with focus on a row
/// button, Page Down already paged the list and Tab brought each row into view
/// — Avalonia's ScrollViewer handles those from a descendant — but Home, End
/// and the arrows did nothing. <see cref="HistoryView"/> now supplies those.
/// </para>
/// <para>
/// Thirty rows against a 680-pixel window overflow the list by nearly four
/// screens, so an offset that does not move is a key that did nothing rather
/// than a list that was already short enough.
/// </para>
/// <para>
/// <b>Which screen-reader mode these hold in.</b> Scan mode off. Narrator's
/// scan cursor (Caps Lock + Space) takes the arrow keys, Home and End for
/// itself, so with it on none of them reach the app — which is Narrator working
/// as designed, not History failing. Recorded because #192 found keyboard
/// claims being made without saying so.
/// </para>
/// </remarks>
public sealed class HistoryKeyboardTests : HeadlessWindows
{
    private const int Rows = 30;

    private readonly string _outputs =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    public HistoryKeyboardTests()
    {
        Directory.CreateDirectory(_outputs);

        for (var i = 0; i < Rows; i++)
        {
            var path = Path.Combine(_outputs, $"Preset-voice-20260101T{i:000000}.wav");
            WavWriter.Write(path, new short[2_400]);
            WavMetadata.TryWrite(path, new OutputMetadata
            {
                Mode = TtsMode.PresetVoice.DisplayName(),
                Text = $"Clip {i}",
                Language = "english",
                Speaker = "ryan",
                ModelRepo = "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
                AppVersion = "0.1.0",
                Created = DateTimeOffset.UtcNow,
            });
        }
    }

    protected override void DisposeCore()
    {
        if (Directory.Exists(_outputs)) Directory.Delete(_outputs, recursive: true);
    }

    private (MainWindow Window, ScrollViewer List) Open()
    {
        var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog(), () => _outputs);
        var window = Open(new MainWindow { DataContext = model });
        model.ShowingHistory = true;
        window.UpdateLayout();

        var list = window.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(s => s.Name == "List");

        Assert.Equal(Rows, model.History.Rows.Count);
        Assert.True(list.Extent.Height > list.Viewport.Height * 3,
            $"the list must overflow for these tests to mean anything: extent {list.Extent.Height}, viewport {list.Viewport.Height}");

        return (window, list);
    }

    private static void Press(MainWindow window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.UpdateLayout();
    }

    /// <summary>Tabs from the window's start until focus is inside the list.</summary>
    private static Control TabIntoTheList(MainWindow window, ScrollViewer list)
    {
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;

        for (var presses = 0; presses < 20; presses++)
        {
            Press(window, PhysicalKey.Tab);
            if (focus.GetFocusedElement() is Control c && c.GetVisualAncestors().Contains(list)) return c;
        }

        throw new Xunit.Sdk.XunitException("twenty Tabs never reached the History list");
    }

    [AvaloniaFact]
    public void Tab_reaches_the_list_in_a_handful_of_presses()
    {
        // The header buttons, the mode picker, then the first row. A list
        // that took thirty Tabs to reach would be reachable in the way a
        // locked door is.
        var (window, list) = Open();
        var focus = TopLevel.GetTopLevel(window)!.FocusManager!;

        var presses = 0;
        do
        {
            Press(window, PhysicalKey.Tab);
            presses++;
        }
        while (!(focus.GetFocusedElement() is Control c && c.GetVisualAncestors().Contains(list)) && presses < 20);

        Assert.InRange(presses, 1, 8);
    }

    [AvaloniaFact]
    public void Page_Down_pages_the_list()
    {
        // Avalonia already does this from a focused descendant; pinned so a
        // future template change cannot quietly take it away.
        var (window, list) = Open();
        TabIntoTheList(window, list);

        Press(window, PhysicalKey.PageDown);

        Assert.Equal(list.Viewport.Height, list.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void End_reaches_the_oldest_clip_and_Home_comes_back()
    {
        // §12: reaching the older ones must not require a trackpad. End is the
        // one press that reaches the oldest; before this it did nothing.
        var (window, list) = Open();
        TabIntoTheList(window, list);

        Press(window, PhysicalKey.End);
        Assert.Equal(list.Extent.Height - list.Viewport.Height, list.Offset.Y, 1);

        Press(window, PhysicalKey.Home);
        Assert.Equal(0, list.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void The_arrows_move_one_row_at_a_time()
    {
        var (window, list) = Open();
        TabIntoTheList(window, list);

        Press(window, PhysicalKey.ArrowDown);
        var oneRow = list.Offset.Y;
        Assert.InRange(oneRow, 40, 80);

        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal(oneRow * 2, list.Offset.Y, 1);

        Press(window, PhysicalKey.ArrowUp);
        Assert.Equal(oneRow, list.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void Tabbing_through_the_rows_brings_each_into_view()
    {
        // The route that needs no scrolling key at all: focus moving down the
        // rows carries the list with it. Five buttons a row, so fifty presses
        // is ten rows — past the first screen of eight.
        var (window, list) = Open();
        TabIntoTheList(window, list);

        for (var i = 0; i < 50; i++) Press(window, PhysicalKey.Tab);

        Assert.True(list.Offset.Y > 0, "focus walked ten rows down and the list never moved");
    }
}
