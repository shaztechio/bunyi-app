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

/// <summary>Identify copyable text without putting its label on the clipboard.</summary>
public class ContextualSelectableTextBlock : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);
    protected override AutomationPeer OnCreateAutomationPeer() => new ContextualTextPeer(this);

    private sealed class ContextualTextPeer(TextBlock owner) : TextBlockAutomationPeer(owner)
    {
        // TextBlock's peer ignores AutomationProperties.Name. It exposes the
        // text as its name, so keep the full value and prefix its purpose.
        protected override string? GetNameCore() => AutomationProperties.GetName(Owner) is { Length: > 0 } label
            ? $"{label}: {base.GetNameCore()}"
            : base.GetNameCore();
    }
}
