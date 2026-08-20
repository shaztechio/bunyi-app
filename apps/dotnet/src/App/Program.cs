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
using Bunyi.Core.Diagnostics;

namespace Bunyi.App;

internal static class Program
{
    /// <summary>
    /// The clock behind the startup line in the log.
    /// </summary>
    /// <remarks>
    /// Started here rather than in <see cref="App"/>, which is otherwise the
    /// one place that builds things: the span this exists to measure — the
    /// runtime coming up, and Avalonia bringing up windowing and rendering — is
    /// already over by the time the composition root runs, and only the entry
    /// point is early enough to see it. Null when the app is hosted without
    /// this entry point, which is how the headless tests run.
    /// </remarks>
    internal static StartupTimeline? Startup { get; private set; }

    // Avalonia desktop entry point. Runs on Windows and Linux.
    [STAThread]
    public static void Main(string[] args)
    {
        // First statement, so nothing this app does lands in the phase that is
        // meant to hold only what happened before it got control.
        Startup = StartupTimeline.FromProcessStart();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
