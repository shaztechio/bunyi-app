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

using System.Windows.Automation;

namespace Bunyi.UiaProbe;

/// <summary>
/// Walking the control view of a live UI Automation tree.
/// </summary>
/// <remarks>
/// <para>
/// The <b>control view</b> deliberately, not the raw view. It is the view a
/// screen reader navigates, so it is the one whose shape a claim about Narrator
/// is a claim about. An element the app pushed out with
/// <c>AutomationProperties.AccessibilityView="Raw"</c> is absent here, which is
/// exactly what the app intends and what these walks are checking.
/// </para>
/// </remarks>
internal static class Tree
{
    private static readonly TreeWalker Walker = TreeWalker.ControlViewWalker;

    /// <summary>
    /// Whether a reader will actually read this, rather than merely reach it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The control view is not the view that gets spoken.</b> This tool
    /// walked the control view and passed a Doctor report and a History list
    /// that Narrator read as complete silence, because
    /// <c>IsControlElementOverride</c> puts an element in the control view
    /// <i>alone</i> and Narrator reads the <b>content</b> view. Named correctly,
    /// typed correctly, in the tree, and skipped.
    /// </para>
    /// <para>
    /// So the checks ask for both. Anything that must be announced is checked
    /// with this, not merely with a walk that found it.
    /// </para>
    /// </remarks>
    internal static bool WillBeRead(AutomationElement element) =>
        element.Current.IsContentElement && element.Current.IsControlElement;

    /// <summary>Every top-level window belonging to <paramref name="processId"/>.</summary>
    internal static List<AutomationElement> WindowsOf(int processId)
    {
        var found = AutomationElement.RootElement.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId));

        return found.Cast<AutomationElement>().ToList();
    }

    /// <summary>The direct children of <paramref name="element"/> in the control view.</summary>
    internal static IEnumerable<AutomationElement> Children(AutomationElement element)
    {
        for (var child = Walker.GetFirstChild(element);
             child is not null;
             child = Walker.GetNextSibling(child))
        {
            yield return child;
        }
    }

    /// <summary>Every descendant of <paramref name="element"/>, depth first.</summary>
    internal static IEnumerable<AutomationElement> Descendants(AutomationElement element)
    {
        foreach (var child in Children(element))
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    /// <summary>One element as a screen reader would meet it: type, name, and what it adds.</summary>
    internal static string Describe(AutomationElement element)
    {
        var info = element.Current;
        var line = $"{info.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal)} : "
            + $"'{Shorten(info.Name)}'";

        if (!string.IsNullOrEmpty(info.AcceleratorKey)) line += $" (accel={info.AcceleratorKey})";
        if (!string.IsNullOrEmpty(info.HelpText)) line += $" (help='{Shorten(info.HelpText)}')";

        // Marked, not hidden: an element in the control view but out of the
        // content view is the shape that reads as silence, and it is invisible
        // in a plain tree dump. See WillBeRead.
        if (!info.IsContentElement) line += "  [control view only — not spoken]";

        return line;
    }

    /// <summary>Prints the control view under <paramref name="element"/>, indented by depth.</summary>
    internal static void Print(AutomationElement element, int depth = 0)
    {
        Console.WriteLine(new string(' ', depth * 2) + Describe(element));
        foreach (var child in Children(element)) Print(child, depth + 1);
    }

    private static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= 70 ? flat : string.Concat(flat.AsSpan(0, 69), "…");
    }
}
