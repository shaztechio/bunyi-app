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
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bunyi.App.Infrastructure;
using Xunit;

namespace Bunyi.App.Tests;

// These check managed peers/styles. The AtSpiProbe checks the actual Linux
// bridge; neither test layer claims to have heard Orca speak.
public class LinuxAccessibilityPresentationTests : HeadlessWindows
{
    [AvaloniaFact]
    public void LayoutClassFallbacksAreEmptyButExplicitNamesAndChildrenRemain()
    {
        var button = new Button { Content = "Play" };
        var group = new StackPanel { Children = { button } };
        AutomationProperties.SetName(group, "Playback");
        var presenter = new ContentPresenter { Content = group };
        var border = new Border { Child = presenter };
        var window = new Window { Content = border };
        window.Styles.Add(LinuxAccessibilityPresentation.CreateStyles());
        Open(window);
        window.UpdateLayout();
        Assert.Empty(ControlAutomationPeer.CreatePeerForElement(border).GetClassName());
        Assert.Empty(ControlAutomationPeer.CreatePeerForElement(presenter).GetClassName());
        Assert.Empty(ControlAutomationPeer.CreatePeerForElement(group).GetClassName());
        Assert.Equal("Playback", ControlAutomationPeer.CreatePeerForElement(group).GetName());
        Assert.Equal("Play", ControlAutomationPeer.CreatePeerForElement(button).GetName());
        Assert.NotEmpty(ControlAutomationPeer.CreatePeerForElement(group).GetChildren());
    }

    [AvaloniaFact]
    public void CollapsedSelectionIsTheSameNamedPeerExposedAsAChild()
    {
        var combo = new ComboBox { ItemsSource = new[] { "raw-id" }, SelectedIndex = 0 };
        AutomationProperties.SetItemStatus(combo, "Friendly voice name");
        Open(new Window { Content = combo });
        var peer = new LinuxComboBoxAutomationPeer(combo);
        var selected = Assert.Single(peer.GetProvider<ISelectionProvider>()!.GetSelection());
        Assert.Same(selected, Assert.Single(peer.GetChildren()));
        Assert.Same(peer, selected.GetParent());
        Assert.Equal("Friendly voice name", selected.GetName());
    }

    [AvaloniaFact]
    public async Task SelectionAndDisplayBindingProduceOneEventWithTheFinalName()
    {
        var combo = new ComboBox { ItemsSource = new[] { "first-id", "second-id" }, SelectedIndex = 0 };
        AutomationProperties.SetItemStatus(combo, "Ryan");
        Open(new Window { Content = combo });
        var peer = new LinuxComboBoxAutomationPeer(combo);
        Assert.True(combo.Focus());
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var names = new List<string?>();
        peer.PropertyChanged += (_, e) =>
        {
            if (e.Property == SelectionPatternIdentifiers.SelectionProperty)
                names.Add(Assert.Single(peer.GetProvider<ISelectionProvider>()!.GetSelection()).GetName());
        };
        combo.SelectedIndex = 1;
        AutomationProperties.SetItemStatus(combo, "Aiden");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(new[] { "Aiden" }, names);
        Assert.Equal("Aiden", Assert.Single(peer.GetChildren()).GetName());
    }
}