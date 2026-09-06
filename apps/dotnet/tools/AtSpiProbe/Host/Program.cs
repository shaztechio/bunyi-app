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

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bunyi.App.ViewModels;

// Separate diagnostic executable: use the real app/view/binding, but feed known
// status announcements without downloading models or running generation. The
// Python probe supplies temporary settings and data directories.
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var clock = Stopwatch.StartNew();
        var next = 0;
        string[] messages = ["Generating", "Generating… 24 frames · 2.0s of speech so far", "Ready"];
        AppBuilder.Configure<Bunyi.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .AfterSetup(_ => DispatcherTimer.Run(() =>
            {
                if (clock.Elapsed.TotalSeconds < 2 + next * 12) return true;
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                    || desktop.MainWindow?.DataContext is not MainViewModel model) return true;
                model.Status = messages[next];
                model.Announcement = messages[next++];
                return next < messages.Length;
            }, TimeSpan.FromMilliseconds(250)))
            .StartWithClassicDesktopLifetime(args);
    }
}