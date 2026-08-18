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
using Bunyi.Core.Diagnostics;

namespace Bunyi.Core.Platform;

/// <summary>What to run to show a file in the platform's file manager.</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">Its arguments, already in order.</param>
public sealed record RevealCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Shows a file, or the folder holding it, in the platform's file manager
/// (spec §2 and §2a: "one click away via the in-app reveal-in-file-manager
/// button").
/// </summary>
/// <remarks>
/// Windows can select the file itself. Linux has no guaranteed verb for that —
/// <c>xdg-open</c> opens a folder but cannot highlight something inside it — so
/// per spec the app tries the freedesktop <c>FileManager1</c> interface, which
/// file managers implement and which does select, and falls back to opening the
/// containing folder. Opening the folder is a worse answer than selecting the
/// file, and a much better one than doing nothing.
/// </remarks>
public static class FileReveal
{
    /// <summary>
    /// Builds the command to reveal <paramref name="path"/>, without running it.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="Reveal"/> so the argument construction can be
    /// tested on any machine. Launching a file manager cannot be asserted on in
    /// CI, but getting the arguments wrong is the likely failure, and that can.
    /// </remarks>
    public static RevealCommand CommandFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);

        if (OperatingSystem.IsWindows())
        {
            // No space after the comma: explorer parses "/select,<path>" as one
            // token, and a space makes it open Documents instead — a silent,
            // confusing wrong answer rather than an error.
            return new RevealCommand("explorer.exe", [$"/select,{full}"]);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new RevealCommand("open", ["-R", full]);
        }

        return new RevealCommand("dbus-send",
        [
            "--session",
            "--print-reply",
            "--dest=org.freedesktop.FileManager1",
            "/org/freedesktop/FileManager1",
            "org.freedesktop.FileManager1.ShowItems",
            // new Uri(...).AbsoluteUri gives a correctly escaped file:// URI;
            // hand-escaping is what SYSLIB0013 warns can corrupt the result.
            $"array:string:{new Uri(full).AbsoluteUri}",
            "string:",
        ]);
    }

    /// <summary>
    /// Builds the fallback command: open the containing folder.
    /// </summary>
    public static RevealCommand? FallbackFor(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Only the Linux path has a fallback; the others select the file.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return null;

        var full = System.IO.Path.GetFullPath(path);
        var folder = Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(folder)) return null;

        return new RevealCommand("xdg-open", [folder]);
    }

    /// <summary>
    /// Reveals <paramref name="path"/>, returning whether anything was launched.
    /// </summary>
    /// <remarks>
    /// Never throws. This sits behind a button next to audio the user just
    /// made; a file manager that will not start is a disappointment, not a
    /// reason to take down the window that is playing their result.
    /// </remarks>
    public static bool Reveal(string path, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            log.Log($"Cannot show {path} in the file manager: it is not there.");
            return false;
        }

        if (TryStart(CommandFor(path))) return true;

        var fallback = FallbackFor(path);
        if (fallback is not null && TryStart(fallback))
        {
            log.Log("The file manager could not select the file, so its folder was opened instead.");
            return true;
        }

        log.Log($"Could not open a file manager to show {path}.");
        return false;
    }

    private static bool TryStart(RevealCommand command)
    {
        try
        {
            var info = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
