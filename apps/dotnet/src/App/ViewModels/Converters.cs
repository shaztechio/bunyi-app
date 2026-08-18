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
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Bunyi.Core;

namespace Bunyi.App.ViewModels;

/// <summary>
/// A mode's name as the user should read it.
/// </summary>
/// <remarks>
/// Without this the picker renders the enum — "PresetVoice", "VoiceDesign" —
/// because that is what ToString gives. The names are already defined once, in
/// <see cref="TtsModeExtensions.DisplayName"/>, because they are also settings
/// keys and part of every output filename.
/// </remarks>
public sealed class ModeName : IValueConverter
{
    public static ModeName Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TtsMode mode ? mode.DisplayName() : value?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Play or stop, per row. There is deliberately no pause (spec §2a).</summary>
public sealed class PlayGlyph : IValueConverter
{
    public static PlayGlyph Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "■" : "▶";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The copy button's label, which acknowledges the copy.
/// </summary>
/// <remarks>
/// §2a: "The button acknowledges the copy, because one that appears to do
/// nothing gets pressed again."
/// </remarks>
public sealed class CopyLabel : IValueConverter
{
    public static CopyLabel Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Copied" : "Copy details";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns a 0–1 fraction into an arc, for the ring around the play button.
/// </summary>
/// <remarks>
/// <para>
/// §2a asks for progress "drawn as a ring around the button itself rather than
/// a separate bar — the control and its progress are the same object, which is
/// what the row has space for". Avalonia has no ring primitive, so the arc is
/// built here.
/// </para>
/// <para>
/// It starts at twelve o'clock and runs clockwise, which is what a viewer
/// expects of something measuring elapsed time.
/// </para>
/// </remarks>
public sealed class ProgressRing : IValueConverter
{
    public static ProgressRing Instance { get; } = new();

    private const double Size = 34;
    private const double Thickness = 2.5;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value switch
        {
            double d => Math.Clamp(d, 0, 1),
            float f => Math.Clamp(f, 0, 1),
            _ => 0,
        };

        if (fraction <= 0) return null;

        var radius = (Size - Thickness) / 2;
        var centre = new Point(Size / 2, Size / 2);
        var start = new Point(centre.X, centre.Y - radius);

        // A full circle cannot be drawn as one arc — the start and end points
        // coincide and the renderer draws nothing. Stop just short.
        if (fraction >= 0.999) fraction = 0.999;

        var angle = fraction * 2 * Math.PI;
        var end = new Point(
            centre.X + radius * Math.Sin(angle),
            centre.Y - radius * Math.Cos(angle));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: fraction > 0.5,
                SweepDirection.Clockwise);
            context.EndFigure(false);
        }

        return geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
