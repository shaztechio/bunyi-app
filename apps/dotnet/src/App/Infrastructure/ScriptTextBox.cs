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

namespace Bunyi.App.Infrastructure;

/// <summary>A scrolling script editor that can also live in a scrolling form.</summary>
public sealed class ScriptTextBox : AccessibleTextBox
{
    // The form's ScrollViewer measures with infinite height. Measure the editor
    // at its minimum in that pass so a long script does not grow the whole form.
    // The star row still stretches it to use spare room during arrangement.
    protected override Size MeasureOverride(Size availableSize) =>
        base.MeasureOverride(double.IsPositiveInfinity(availableSize.Height)
            ? availableSize.WithHeight(MinHeight)
            : availableSize);
}