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
using Avalonia.Controls.Selection;
using Avalonia.Threading;

namespace Bunyi.App.Infrastructure;

/// <summary>A picker whose collapsed selection is exposed through Linux AT-SPI.</summary>
public class AccessibleComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override AutomationPeer OnCreateAutomationPeer() => OperatingSystem.IsLinux()
        ? new LinuxComboBoxAutomationPeer(this)
        : base.OnCreateAutomationPeer();
}

internal sealed class LinuxComboBoxAutomationPeer(ComboBox owner) : ComboBoxAutomationPeer(owner)
{
    private IReadOnlyList<AutomationPeer>? _collapsedSelection;
    private object? _selectedItem;
    private string? _selectedName;
    private bool _queued;

    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => Owner.IsDropDownOpen
        ? base.GetChildrenCore()
        : GetSelectionCore();

    protected override IReadOnlyList<AutomationPeer>? GetSelectionCore()
    {
        if (Owner.IsDropDownOpen) return base.GetSelectionCore();
        if (Owner.SelectedItem is not { } item) return null;
        var name = AutomationProperties.GetItemStatus(Owner) ?? item.ToString() ?? string.Empty;
        if (!ReferenceEquals(item, _selectedItem) || name != _selectedName || _collapsedSelection is null)
        {
            _selectedItem = item;
            _selectedName = name;
            _collapsedSelection = [new SelectedPeer(this, name)];
        }
        return _collapsedSelection;
    }

    protected override void OwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        base.OwnerPropertyChanged(sender, e);
        if (e.Property != ComboBox.SelectedItemProperty
            && e.Property != ComboBox.IsDropDownOpenProperty
            && e.Property != AutomationProperties.ItemStatusProperty) return;
        QueueSelectionUpdate();
    }

    protected override void OwnerSelectionChanged(object? sender, SelectionModelSelectionChangedEventArgs e)
    {
        if (Owner.IsDropDownOpen) base.OwnerSelectionChanged(sender, e);
        else QueueSelectionUpdate();
    }

    private void QueueSelectionUpdate()
    {
        if (_queued) return;
        _queued = true;
        // Bindings update ItemStatus with the displayed name after selection.
        // Coalesce that and SelectedItem into one event with the final value.
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            InvalidateChildren();
            if (Owner.IsFocused && !Owner.IsDropDownOpen)
                RaisePropertyChangedEvent(SelectionPatternIdentifiers.SelectionProperty, null, null);
        }, DispatcherPriority.Input);
    }

    // The framework's unrealized selection is absent from GetChildren(), so the
    // AT-SPI selection handler returns a null reference. Serve the same named
    // peer as both child and selection; a new value gets a new node/cache entry.
    private sealed class SelectedPeer(AutomationPeer parent, string name) : UnrealizedElementAutomationPeer
    {
        protected override string? GetAcceleratorKeyCore() => null;
        protected override string? GetAccessKeyCore() => null;
        protected override string? GetAutomationIdCore() => null;
        protected override string GetClassNameCore() => nameof(ComboBoxItem);
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationPeer? GetParentCore() => parent;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetNameCore() => name;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
    }
}