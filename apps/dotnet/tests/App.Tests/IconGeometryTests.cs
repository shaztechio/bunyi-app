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
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>
/// The play glyph sits centred in its round button.
/// </summary>
/// <remarks>
/// <para>
/// This was wrong twice, and eyeballing it is what let it stay wrong. The trap:
/// a triangle's centre of area is a third of the way from its base, and its
/// bounding-box centre is halfway — the two cannot coincide for any triangle.
/// Anything that centres by bounding box, which is what <c>Stretch</c> does,
/// therefore puts a play glyph visibly off centre inside a circle.
/// </para>
/// <para>
/// So the geometries are drawn at final size with the centre of area already on
/// the button's centre. That is arithmetic, and arithmetic can be checked.
/// </para>
/// </remarks>
public class IconGeometryTests
{
    private static Geometry Resource(string key)
    {
        var app = Application.Current!;
        Assert.True(app.TryFindResource(key, out var value), $"no resource named {key}");
        return Assert.IsAssignableFrom<Geometry>(value);
    }

    /// <summary>
    /// The centre of area of a right-pointing triangle, from its bounds alone.
    /// </summary>
    /// <remarks>
    /// The base is the vertical edge at <c>Left</c> and the tip is at
    /// <c>Right</c>, so the three vertices are (L, Top), (L, Bottom) and
    /// (R, midY). Averaging them gives (2L + R) / 3 across, and the vertical
    /// midpoint down.
    /// </remarks>
    private static Point CentreOfArea(Rect bounds) => new(
        (2 * bounds.Left + bounds.Right) / 3,
        bounds.Top + bounds.Height / 2);

    [AvaloniaTheory]
    [InlineData("IconPlayRound28", 28)]
    [InlineData("IconPlayRound32", 32)]
    public void The_play_triangle_is_centred_by_area_in_its_button(string key, double buttonSize)
    {
        var centre = CentreOfArea(Resource(key).Bounds);

        Assert.Equal(buttonSize / 2, centre.X, 1);
        Assert.Equal(buttonSize / 2, centre.Y, 1);
    }

    [AvaloniaFact]
    public void The_stop_square_is_centred_in_its_button()
    {
        var bounds = Resource("IconStopRound28").Bounds;

        Assert.Equal(14, bounds.Center.X, 1);
        Assert.Equal(14, bounds.Center.Y, 1);
    }

    [AvaloniaFact]
    public void The_round_glyphs_fit_inside_their_button()
    {
        // Drawn at final size, so anything outside the button would be clipped
        // rather than scaled down.
        foreach (var (key, size) in new[]
                 {
                     ("IconPlayRound28", 28.0), ("IconStopRound28", 28.0),
                     ("IconPlayRound32", 32.0),
                 })
        {
            var bounds = Resource(key).Bounds;
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0, $"{key} starts outside the button");
            Assert.True(bounds.Right <= size && bounds.Bottom <= size, $"{key} overflows the button");
        }
    }

    [AvaloniaFact]
    public void Centring_by_bounding_box_would_have_been_wrong()
    {
        // The reason these geometries exist rather than a Stretch. If bounding-
        // box centring were adequate, the two would agree — and for the play
        // triangle they never can.
        var bounds = Resource("IconPlayRound28").Bounds;

        Assert.NotEqual(bounds.Center.X, CentreOfArea(bounds).X, 1);
    }

    [AvaloniaFact]
    public void Every_icon_in_the_set_resolves()
    {
        // A missing geometry draws nothing and reports nothing, which is how
        // the Copy button spent a release invisible.
        foreach (var key in new[]
                 {
                     "IconDoctor", "IconSettings", "IconLogs", "IconHelp", "IconPlay", "IconStop",
                     "IconSave", "IconCopy", "IconTick", "IconFolder", "IconTrash",
                     "IconPlayRound28", "IconStopRound28", "IconPlayRound32",
                 })
        {
            Assert.True(Application.Current!.TryFindResource(key, out var value), $"missing {key}");
            Assert.IsAssignableFrom<Geometry>(value);
        }
    }
}
