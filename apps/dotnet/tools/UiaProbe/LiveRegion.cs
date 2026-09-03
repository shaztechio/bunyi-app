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

using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Bunyi.UiaProbe;

/// <summary>
/// The live-region half of the probe, on the COM UI Automation client (#192).
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="Tree"/> because it uses a different client, and it
/// uses a different client because the managed one cannot answer either
/// question. Measured, not assumed:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>AutomationElement.GetCurrentPropertyValue(AutomationElementIdentifiers.LiveSettingProperty)</c>
/// returns an <c>ArgumentException</c> <i>object</i> whose message is
/// "Unsupported Property" — the identifier exists in <c>UIAutomationTypes</c>,
/// and the client will not fetch it.
/// </description></item>
/// <item><description>
/// <c>Automation.AddAutomationEventHandler(…LiveRegionChangedEvent…)</c> is
/// accepted without complaint and then delivers nothing, on a window whose
/// status text demonstrably changed.
/// </description></item>
/// </list>
/// <para>
/// That is what left #192's original probe inconclusive, and it is why the
/// answer needs <c>IUIAutomation</c>. Both questions are about the same one
/// mechanism, and only the second one matters on its own: a live region that
/// carries the property and never raises the event is silent.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.1")]
internal static class LiveRegion
{
    private static IUIAutomation Client() => (IUIAutomation)new CUIAutomation();

    /// <summary>What one element reports for <c>UIA_LiveSettingPropertyId</c> (30135).</summary>
    /// <returns>
    /// "Off", "Polite" or "Assertive" when the provider serves the property;
    /// null when it does not serve it at all, which is the failure worth
    /// telling apart from a deliberate Off.
    /// </returns>
    private static string? Setting(IUIAutomationElement element)
    {
        // Ex with ignoreDefaultValue, so a provider that does not implement the
        // property comes back as the reserved "not supported" object rather
        // than as a zero indistinguishable from a real Off.
        var value = element.GetCurrentPropertyValueEx(UIA_PROPERTY_ID.UIA_LiveSettingPropertyId, true);

        return (value as int?) switch
        {
            0 => "Off",
            1 => "Polite",
            2 => "Assertive",
            _ => null,
        };
    }

    /// <summary>Every element under <paramref name="window"/> that serves a live setting other than Off.</summary>
    internal static List<(string Name, string Setting)> Read(nint window)
    {
        var uia = Client();
        var walker = uia.ControlViewWalker;
        var found = new List<(string, string)>();

        void Walk(IUIAutomationElement element)
        {
            if (Setting(element) is { } setting and not "Off")
                found.Add((element.CurrentName.ToString(), setting));

            for (var child = walker.GetFirstChildElement(element);
                 child is not null;
                 child = walker.GetNextSiblingElement(child))
            {
                Walk(child);
            }
        }

        Walk(uia.ElementFromHandle(new HWND(window)));
        return found;
    }

    /// <summary>
    /// Listens for <c>UIA_LiveRegionChangedEventId</c> (20024) for a while, and
    /// reports what it heard.
    /// </summary>
    /// <remarks>
    /// The one check nothing else in this repository can make. A property value
    /// says the app asked to be announced; only the event says the toolkit went
    /// and announced it.
    /// </remarks>
    internal static List<string> Watch(nint window, TimeSpan how_long, Action<string> onEach)
    {
        var uia = Client();
        var handler = new Handler(onEach);

        uia.AddAutomationEventHandler(
            UIA_EVENT_ID.UIA_LiveRegionChangedEventId,
            uia.ElementFromHandle(new HWND(window)),
            TreeScope.TreeScope_Subtree,
            cacheRequest: null!,
            handler);

        try
        {
            Thread.Sleep(how_long);
        }
        finally
        {
            uia.RemoveAllEventHandlers();
        }

        return handler.Heard;
    }

    /// <summary>The callback UIA hands each event to, on its own thread.</summary>
    private sealed class Handler(Action<string> onEach) : IUIAutomationEventHandler
    {
        private readonly Lock _gate = new();

        internal List<string> Heard { get; } = [];

        public void HandleAutomationEvent(IUIAutomationElement sender, UIA_EVENT_ID eventId)
        {
            // Read the name here rather than later: by the time this returns,
            // the status may already say something else.
            var name = sender.CurrentName.ToString();

            lock (_gate) Heard.Add(name);
            onEach(name);
        }
    }
}
