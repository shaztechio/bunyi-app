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
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Bunyi.App.Infrastructure;

/// <summary>
/// Says something out loud, on a control that is a live region (spec §12).
/// </summary>
/// <remarks>
/// <para>
/// Binding a string to <see cref="TextProperty"/> sets the control's accessible
/// name and raises the change, which is what makes a live region speak. Two
/// pieces of Avalonia behaviour make this necessary rather than ornamental,
/// both measured:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>A TextBlock's peer reports its own text and ignores a name set on it.</b>
/// So a live region cannot be the visible label whenever what is shown and what
/// is announced need to differ — and here they must, because the status ticks
/// per frame and speech does not.
/// </description></item>
/// <item><description>
/// <b><c>ControlAutomationPeer</c> does not watch <c>AutomationProperties.Name</c>.</b>
/// It watches bounds, visibility, visual parent, <c>ItemStatus</c> and
/// <c>AutomationId</c> — and not the name. So setting the name changes what a
/// reader would find on inspection and tells it nothing has happened. The event
/// has to be raised by hand, and raising it is the whole point: Avalonia's Win32
/// bridge turns a Name change on a peer whose <c>LiveSetting</c> is not Off into
/// <c>UIA_LiveRegionChangedEventId</c>.
/// </description></item>
/// </list>
/// <para>
/// Put this on a container, give the container <c>LiveSetting</c>, a
/// <c>ControlTypeOverride</c> of Text and <c>AccessibilityView="Content"</c>,
/// and make the visible text inside it Raw so it is not read twice.
/// </para>
/// </remarks>
public static class Announce
{
    /// <summary>What the control should say. Setting it says it.</summary>
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Text", typeof(Announce));

    static Announce() => TextProperty.Changed.AddClassHandler<Control>(OnTextChanged);

    public static string? GetText(Control control) => control.GetValue(TextProperty);

    public static void SetText(Control control, string? value) => control.SetValue(TextProperty, value);

    private static void OnTextChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        var spoken = args.GetNewValue<string?>();
        if (string.IsNullOrEmpty(spoken)) return;

        var previous = AutomationProperties.GetName(control);
        if (previous == spoken) return;

        AutomationProperties.SetName(control, spoken);

        // CreatePeerForElement is get-or-create, so this is the same peer the
        // bridge is holding. Before a reader has walked here there is no node
        // listening and the call is a no-op, which is the correct outcome.
        ControlAutomationPeer.CreatePeerForElement(control).RaisePropertyChangedEvent(
            AutomationElementIdentifiers.NameProperty,
            previous,
            spoken);
    }
}
