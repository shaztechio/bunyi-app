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

/// <summary>Expose one placeholder, rather than the text box's template copies.</summary>
public class AccessibleTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);
    protected override AutomationPeer OnCreateAutomationPeer() => OperatingSystem.IsLinux()
        ? new LinuxTextBoxAutomationPeer(this)
        : base.OnCreateAutomationPeer();
}

internal sealed class LinuxTextBoxAutomationPeer(TextBox owner) : TextBoxAutomationPeer(owner)
{
    // The field already exposes its text through IValueProvider/AT-SPI Text.
    // Template labels/presenters are duplicate content, not separate fields.
    protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => Array.Empty<AutomationPeer>();

    protected override string? GetHelpTextCore()
    {
        var help = base.GetHelpTextCore();
        // ControlAutomationPeer falls back to PlaceholderText for Windows.
        // Linux has a dedicated placeholder-text attribute, which we preserve.
        return help == GetPlaceholderTextCore() ? null : help;
    }
}