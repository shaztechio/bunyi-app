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
/// Watches UIA property changes on a live window, on the COM client.
/// </summary>
/// <remarks>
/// <para>
/// A control that updates silently tells a screen-reader user nothing, and a
/// property read back at rest cannot tell a silent control from a talkative
/// one — only an event can. This is the tool for "I changed it and nothing was
/// spoken".
/// </para>
/// <para>
/// On the COM client for the same reason <see cref="LiveRegion"/> is: the
/// managed client's event delivery cannot be trusted here. It accepted a
/// subscription to <c>LiveRegionChanged</c> and delivered nothing while the
/// event was demonstrably being raised, and it does the same for these. A
/// silent client and a silent app look identical from the outside, which is
/// how a real defect and a phantom one get confused — twice, in #192's history.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows6.1")]
internal static class PropertyWatch
{
    /// <summary>The properties a screen reader acts on when a control's value moves.</summary>
    internal static readonly UIA_PROPERTY_ID[] Interesting =
    [
        UIA_PROPERTY_ID.UIA_ItemStatusPropertyId,
        UIA_PROPERTY_ID.UIA_NamePropertyId,
        UIA_PROPERTY_ID.UIA_ValueValuePropertyId,
        UIA_PROPERTY_ID.UIA_SelectionItemIsSelectedPropertyId,
        UIA_PROPERTY_ID.UIA_ExpandCollapseExpandCollapseStatePropertyId,
    ];

    /// <summary>Reports every change to <paramref name="properties"/> under the window.</summary>
    internal static unsafe List<string> Watch(
        nint window,
        UIA_PROPERTY_ID[] properties,
        TimeSpan howLong,
        Action<string> onEach)
    {
        var uia = (IUIAutomation)new CUIAutomation();
        var handler = new Handler(onEach);

        fixed (UIA_PROPERTY_ID* ids = properties)
        {
            uia.AddPropertyChangedEventHandlerNativeArray(
                uia.ElementFromHandle(new HWND(window)),
                TreeScope.TreeScope_Subtree,
                cacheRequest: null!,
                handler,
                ids,
                properties.Length);
        }

        try
        {
            Thread.Sleep(howLong);
        }
        finally
        {
            uia.RemoveAllEventHandlers();
        }

        return handler.Heard;
    }

    /// <summary>The short name of a property id, for reading rather than for lookups.</summary>
    private static string Name(UIA_PROPERTY_ID id) =>
        id.ToString().Replace("UIA_", "", StringComparison.Ordinal)
            .Replace("PropertyId", "", StringComparison.Ordinal);

    private sealed class Handler(Action<string> onEach) : IUIAutomationPropertyChangedEventHandler
    {
        private readonly Lock _gate = new();

        internal List<string> Heard { get; } = [];

        public void HandlePropertyChangedEvent(
            IUIAutomationElement sender,
            UIA_PROPERTY_ID propertyId,
            object newValue)
        {
            var line = $"{Name(propertyId),-28} -> '{newValue}'   on '{sender.CurrentName}'";

            lock (_gate) Heard.Add(line);
            onEach(line);
        }
    }
}
