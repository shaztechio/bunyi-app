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

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bunyi.App.ViewModels;

namespace Bunyi.App.Views;

public partial class SrtCueTable : UserControl
{
    private MainViewModel? _model;
    public SrtCueTable()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.SrtErrorFocusRequested -= FocusError;
            _model = DataContext as MainViewModel;
            if (_model is not null) _model.SrtErrorFocusRequested += FocusError;
        };
    }
    private void FocusError(object? sender, SrtCueRow row)
    {
        var list = this.FindControl<ListBox>("CueList")!;
        list.ScrollIntoView(row);
        Dispatcher.UIThread.Post(() => list.ContainerFromItem(row)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus());
    }
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || list.SelectedItem is null) return;
        list.ScrollIntoView(list.SelectedItem);
        list.ContainerFromItem(list.SelectedItem)?.BringIntoView();
    }
}
