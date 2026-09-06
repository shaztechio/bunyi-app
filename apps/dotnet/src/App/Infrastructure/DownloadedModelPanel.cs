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

using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Bunyi.App.Infrastructure;

/// <summary>A model row whose action carries its complete spoken context.</summary>
public sealed class DownloadedModelPanel : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ModelRowPeer(this);

    private sealed class ModelRowPeer(DownloadedModelPanel owner) : ControlAutomationPeer(owner)
    {
        // The button name already carries the action, model and size/origin.
        // Keep the adjacent visual labels out of the accessibility tree so
        // all of this information has a single accessible representation.
        // AT-SPI includes Raw peers, so marking those labels Raw is not enough.
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() =>
            owner.Children.OfType<Button>().Select(CreatePeerForElement).ToArray();
    }
}
