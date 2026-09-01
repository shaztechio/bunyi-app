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

using System.Globalization;
using Avalonia.Media;
using Bunyi.App.ViewModels;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// The History pills wear one colour per mode (parity with macOS Theme.swift).
/// </summary>
public class ModeTintTests
{
    private static Color Ink(string? mode) =>
        ((ISolidColorBrush)ModeTint.Instance.Convert(
            mode, typeof(IBrush), null, CultureInfo.InvariantCulture)).Color;

    private static ISolidColorBrush Fill(string? mode) =>
        (ISolidColorBrush)ModeTint.Instance.Convert(
            mode, typeof(IBrush), "faint", CultureInfo.InvariantCulture);

    [Fact]
    public void The_three_modes_are_three_different_colours()
    {
        // The reported bug: every pill was the same, so the shape carried no
        // information the text was not already carrying.
        var preset = Ink("Preset voice");
        var design = Ink("Voice design");
        var clone = Ink("Voice clone");

        Assert.NotEqual(preset, design);
        Assert.NotEqual(design, clone);
        Assert.NotEqual(preset, clone);
    }

    [Theory]
    [InlineData("Preset voice", 26, 140, 140)]
    [InlineData("Voice design", 161, 61, 227)]
    [InlineData("Voice clone", 184, 115, 13)]
    public void They_are_the_macOS_colours(string mode, byte r, byte g, byte b)
    {
        // Pinned to the values in apps/macos/Theme.swift rather than "three
        // distinct colours": the two apps showing a different teal for the same
        // mode is the kind of drift the parity rule exists to stop.
        Assert.Equal(Color.FromRgb(r, g, b), Ink(mode));
    }

    [Fact]
    public void None_of_them_is_the_accent()
    {
        // The other half of the bug, and the one macOS writes down: in History
        // the accent is the progress ring on the playing row. A pill wearing it
        // competes with the only colour in the view that carries state.
        var accent = Color.Parse("#5C54F5");
        var accentDark = Color.Parse("#7B72F0");

        foreach (var mode in new[] { "Preset voice", "Voice design", "Voice clone" })
        {
            Assert.NotEqual(accent, Ink(mode));
            Assert.NotEqual(accentDark, Ink(mode));
        }
    }

    [Fact]
    public void The_fill_is_the_ink_at_low_opacity()
    {
        // One converter serves both so they cannot drift; the fill must stay
        // the same hue as the text sitting on it.
        var fill = Fill("Voice design");

        Assert.Equal(Ink("Voice design"), fill.Color);
        Assert.Equal(0.14, fill.Opacity, 3);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_unrecognised_is_grey(string? mode)
    {
        // GeneratedOutput.Mode falls back to "Unknown" for a file with no
        // metadata. Inventing a colour for it would be a lie with a hue, and
        // macOS falls back to a neutral for the same reason.
        Assert.Equal(Colors.Gray, Ink(mode));
    }

    [Theory]
    [InlineData("voice DESIGN")]
    [InlineData("Design")]
    public void Matching_does_not_depend_on_exact_wording(string mode)
    {
        // Matched on the distinguishing word, so a clip written by an older
        // build with slightly different metadata still gets its colour rather
        // than falling to grey on a technicality.
        Assert.Equal(Color.FromRgb(161, 61, 227), Ink(mode));
    }
}
