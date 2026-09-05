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
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;

namespace Bunyi.App.Infrastructure;

internal static class LinuxAccessibilityPresentation
{
    // AT-SPI falls back from an empty accessible name to GetClassName(). Orca
    // consequently treats an unnamed Panel as a named group. An empty class
    // fallback lets Orca recognize layout-only containers; their children,
    // explicit names, labels and roles remain intact. Windows keeps its UIA tree.
    internal static Styles CreateStyles() =>
    [
        QuietLayout(s => s.Is<Panel>()),
        QuietLayout(s => s.Is<Decorator>()),
        QuietLayout(s => s.Is<ContentPresenter>()),
    ];

    private static Style QuietLayout(Func<Selector?, Selector> selector) => new(selector)
    {
        Setters = { new Setter(AutomationProperties.ClassNameOverrideProperty, string.Empty) },
    };
}