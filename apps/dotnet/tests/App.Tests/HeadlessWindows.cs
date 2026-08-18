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

namespace Bunyi.App.Tests;

/// <summary>
/// Closes the windows a test opened, when the test ends.
/// </summary>
/// <remarks>
/// <para>
/// The headless tests opened windows and never closed them, so every window
/// ever opened stayed live for the rest of the run — bindings attached, timers
/// running, event handlers subscribed. That is what made the suite
/// intermittently fail in CI with "the calling thread cannot access this object
/// because a different thread owns it", raised during the <b>cleanup</b> of
/// whichever test happened to be running when a leftover fired. It hit
/// different tests on different runs, including one that opens no window at
/// all, which is the signature of shared state rather than a broken test.
/// </para>
/// <para>
/// xunit builds a fresh instance of a test class per test and disposes it
/// afterwards, so a class that derives from this gets its windows closed
/// between tests without every test having to remember.
/// </para>
/// </remarks>
public abstract class HeadlessWindows : IDisposable
{
    private readonly List<Window> _opened = [];

    /// <summary>Shows a window and closes it when the test finishes.</summary>
    protected T Open<T>(T window) where T : Window
    {
        ArgumentNullException.ThrowIfNull(window);

        _opened.Add(window);
        window.Show();
        return window;
    }

    /// <summary>Anything else the test class needs to release.</summary>
    /// <remarks>
    /// A hook rather than a virtual Dispose, so a derived class cannot forget
    /// to close its windows by overriding the wrong thing.
    /// </remarks>
    protected virtual void DisposeCore()
    {
    }

    public void Dispose()
    {
        // Newest first: a dialog shown over a window should go before the
        // window it belongs to.
        for (var i = _opened.Count - 1; i >= 0; i--)
        {
            try
            {
                _opened[i].Close();
            }
            catch (InvalidOperationException)
            {
                // A window already closed by the test itself. Nothing to do,
                // and failing here would turn tidy-up into a test failure.
            }
        }

        _opened.Clear();

        DisposeCore();
        GC.SuppressFinalize(this);
    }
}
