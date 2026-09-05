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

using Bunyi.App.Infrastructure;
using Xunit;

namespace Bunyi.App.Tests;

public class LinuxAccessibilityFocusTests
{
    // This checks the private API compatibility that the compiler cannot check.
    // It does not initialize X11, inspect AT-SPI events, or prove Orca speech.
    // The real Linux regression is documented in tools/AtSpiProbe/README.md.
    [Fact]
    public void PinnedAvaloniaExposesTheRequiredAtSpiBindings()
    {
        Assert.NotNull(new LinuxAccessibilityFocus.Bridge());
    }
}