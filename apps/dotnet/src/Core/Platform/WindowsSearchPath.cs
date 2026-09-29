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

using System.Runtime.InteropServices;

namespace Bunyi.Core.Platform;

/// <summary>
/// Keeps a binary planted in the current directory from being found by name on
/// Windows. Running <c>bunyi</c> from an untrusted folder must not load code from it.
/// </summary>
public static class WindowsSearchPath
{
    /// <summary>
    /// The system's own <c>explorer.exe</c>, by full path, so a copy in the current
    /// directory cannot be picked up by a bare-name lookup. Falls back to the
    /// bare name only when the Windows folder cannot be resolved.
    /// </summary>
    public static string Explorer
    {
        get
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return string.IsNullOrEmpty(windows) ? "explorer.exe" : Path.Combine(windows, "explorer.exe");
        }
    }

    /// <summary>
    /// Removes the current directory from the DLL search order for this process
    /// (native libraries such as the CUDA edition's separately installed cuDNN and
    /// cuBLAS are still found through the application directory and PATH).
    /// Call once, first thing in <c>Main</c>. Does nothing off Windows.
    /// </summary>
    public static void RemoveCurrentDirectory()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { SetDllDirectoryW(string.Empty); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string lpPathName);
}
