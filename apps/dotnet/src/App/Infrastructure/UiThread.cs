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

using Avalonia.Threading;

namespace Bunyi.App.Infrastructure;

/// <summary>
/// The one place that knows what a UI thread is.
/// </summary>
/// <remarks>
/// Core raises its events on whichever thread did the work, by design — it has
/// no business knowing about a dispatcher. Everything that turns those into
/// bound state comes through here, because Avalonia will throw, or corrupt what
/// it is drawing, if a binding is updated from a background thread.
/// </remarks>
internal static class UiThread
{
    /// <summary>Runs an action on the UI thread, immediately if already there.</summary>
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
