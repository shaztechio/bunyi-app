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
/// The History segment, which sits beside the three modes but is not one.
/// </summary>
/// <remarks>
/// A type of its own rather than a fourth value in <c>TtsMode</c>, because that
/// enum is a data format: it keys the settings, names every output file and is
/// written into each clip's metadata. History is none of those things.
/// </remarks>
public sealed class HistorySegment
{
    public static HistorySegment Instance { get; } = new();
    private HistorySegment() { }
    public override string ToString() => "History";
}

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

/// <summary>
/// Shows a model's identifier the way a person would write it.
/// </summary>
/// <remarks>
/// Display only. The picker still holds — and still sends — whatever the model
/// published, so "Uncle Fu" on screen is <c>uncle_fu</c> on the wire. Converting
/// the value instead would mean guessing our way back to the identifier, and
/// getting it wrong for any name we had not thought of.
/// </remarks>
public sealed class IdentifierName : IValueConverter
{
    public static IdentifierName Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string identifier ? DisplayName.For(identifier) : value?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Whether the picker can be used right now (spec §2).
/// </summary>
/// <remarks>
/// <para>
/// While work runs the row is locked, <b>unless History is already showing</b>,
/// in which case it unlocks so the run can be got back to. That is macOS's rule
/// — <c>.disabled(engine.status.isBusy &amp;&amp; tab != .history)</c> — and it
/// is the whole rule.
/// </para>
/// <para>
/// This started out the other way round: the modes went dead and History stayed
/// live, on the reasoning that History only reads a folder so there is no harm
/// in reaching it. The harm is that it is a door with no handle on the far side.
/// Going to History mid-run left every way back disabled, and the only exit was
/// to stop the work or close the window. Reported from using the app.
/// </para>
/// <para>
/// The modes are still inputs that must not change mid-run, so locking them is
/// right; what was wrong was offering the one destination that could not be
/// left.
/// </para>
/// </remarks>
public sealed class SegmentEnabled : IMultiValueConverter
{
    public static SegmentEnabled Instance { get; } = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2) return true;

        var isBusy = values[0] is true;
        var showingHistory = values.Count > 1 && values[1] is true;

        // Everything or nothing, depending on where you already are. While work
        // runs, a mode tab locks the row completely and History unlocks it.
        return !isBusy || showingHistory;
    }
}

/// <summary>
/// One colour per generation mode, for the History pills.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>apps/macos/Theme.swift</c>, <c>TTSMode.pillColor</c>, which is
/// the reference implementation. Every pill here wore
/// <c>BunyiAccent</c> — one colour for all three modes, and the wrong one:
/// <b>in History the accent already means "this row is playing"</b>, because it
/// is the progress ring drawn around the play button. A pill wearing it
/// competed with the only colour in that view carrying state.
/// </para>
/// <para>
/// Violet is the brand's own second stop; teal and amber are not, and are the
/// only invented colours in the app. They earn it by making a long list
/// scannable without reading every row. The mode name stays spelled out inside
/// the pill, so the colour is redundant rather than load-bearing — nothing is
/// lost to anyone who cannot separate these hues.
/// </para>
/// <para>
/// Fixed sRGB rather than theme brushes, for the reason macOS gives: these must
/// stay recognisably the same three in light and dark.
/// </para>
/// </remarks>
public sealed class ModeTint : IValueConverter
{
    public static ModeTint Instance { get; } = new();

    // The macOS values, converted from sRGB fractions: 0.10/0.55/0.55,
    // 0.63/0.24/0.89, 0.72/0.45/0.05.
    private static readonly Color Preset = Color.FromRgb(26, 140, 140);
    private static readonly Color Design = Color.FromRgb(161, 61, 227);
    private static readonly Color Clone = Color.FromRgb(184, 115, 13);

    /// <summary>The pill's background opacity, as macOS draws it.</summary>
    private const double Faint = 0.14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tint = Tint(value as string);

        // "faint" asks for the pill's fill, anything else for its ink. One
        // converter rather than two, because the two must not drift apart.
        return string.Equals(parameter as string, "faint", StringComparison.Ordinal)
            ? new SolidColorBrush(tint, Faint)
            : new SolidColorBrush(tint);
    }

    /// <summary>
    /// The mode's colour, or a neutral one for anything unrecognised.
    /// </summary>
    /// <remarks>
    /// Matched on the distinguishing word rather than the whole string.
    /// <c>GeneratedOutput.Mode</c> is whatever the file's metadata recorded and
    /// falls back to "Unknown" when there is none, so an exact match would put
    /// a clip written by an older build into the default branch on a
    /// technicality. Unknown gets grey, which is macOS's fallback too — an
    /// invented colour for "we do not know" would be a lie with a hue.
    /// </remarks>
    private static Color Tint(string? mode)
    {
        if (mode is null) return Colors.Gray;

        if (mode.Contains("design", StringComparison.OrdinalIgnoreCase)) return Design;
        if (mode.Contains("clone", StringComparison.OrdinalIgnoreCase)) return Clone;
        if (mode.Contains("preset", StringComparison.OrdinalIgnoreCase)) return Preset;

        return Colors.Gray;
    }

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
/// The play button's tooltip, which says what pressing it will do.
/// </summary>
public sealed class PlayTip : IValueConverter
{
    public static PlayTip Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Stop" : "Play the result again";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A 0-to-1 fraction as a width in pixels.
/// </summary>
/// <remarks>
/// The filled half of the playback bar, drawn as a plain Border rather than a
/// ProgressBar — see MainWindow.axaml for why. Clamped, because a fraction
/// slightly over 1 on the last tick would draw past the end of the track.
/// </remarks>
public sealed class BarWidth : IValueConverter
{
    public static BarWidth Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? d : 0;
        var full = parameter is string s && double.TryParse(s, NumberStyles.Float, culture, out var w)
            ? w
            : 0;

        return Math.Clamp(fraction, 0, 1) * full;
    }

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
