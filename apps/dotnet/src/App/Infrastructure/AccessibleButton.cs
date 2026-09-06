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

using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Bunyi.App.Infrastructure;

/// <summary>A button that respects an explicitly empty accessible description.</summary>
public class AccessibleButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    protected override AutomationPeer OnCreateAutomationPeer() => new ExplicitHelpButtonPeer(this);

    private sealed class ExplicitHelpButtonPeer(Button owner) : ButtonAutomationPeer(owner)
    {
        // Avalonia treats empty HelpText as missing and falls back to ToolTip.
        // Honor the explicit value so hover explanations need not be spoken.
        protected override string? GetHelpTextCore() =>
            AutomationProperties.GetHelpText(Owner) ?? base.GetHelpTextCore();
    }
}
