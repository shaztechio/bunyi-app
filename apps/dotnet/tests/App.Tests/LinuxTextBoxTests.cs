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
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bunyi.App.Infrastructure;
using Xunit;

namespace Bunyi.App.Tests;

// Managed peer checks; the Linux probe verifies AT-SPI, and Orca speech is manual.
public class LinuxTextBoxTests : HeadlessWindows
{
    [AvaloniaFact]
    public void PlaceholderAppearsOnceAndTheFieldRemainsNamedAndEditable()
    {
        var box = new TextBox { PlaceholderText = "Type something", Text = "" };
        AutomationProperties.SetName(box, "Script");
        Open(new Window { Content = box });
        var peer = new LinuxTextBoxAutomationPeer(box);
        Assert.Equal("Script", peer.GetName());
        Assert.Equal("Type something", peer.GetPlaceholderText());
        Assert.True(string.IsNullOrEmpty(peer.GetHelpText()));
        Assert.Empty(peer.GetChildren());
        var value = peer.GetProvider<IValueProvider>()!;
        Assert.Equal("", value.Value);
        value.SetValue("Actual text");
        Assert.Equal("Actual text", box.Text);
        Assert.Equal("Actual text", value.Value);
    }

    [AvaloniaFact]
    public void DistinctHelpAndValidationMessagesRemainAvailable()
    {
        var box = new TextBox { PlaceholderText = "Type something" };
        Open(new Window { Content = box });
        var peer = new LinuxTextBoxAutomationPeer(box);
        AutomationProperties.SetHelpText(box, "Separate help");
        Assert.Equal("Separate help", peer.GetHelpText());
        box.ClearValue(AutomationProperties.HelpTextProperty);
        DataValidationErrors.SetErrors(box, new[] { "Text is required" });
        Assert.Equal("Text is required", peer.GetHelpText());
    }
}