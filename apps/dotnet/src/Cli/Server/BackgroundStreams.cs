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
using Microsoft.Win32.SafeHandles;

namespace Bunyi.Cli.Server;

/// <summary>Detach native as well as managed streams from the short-lived startup client.</summary>
public static class BackgroundStreams
{
    private static readonly List<SafeFileHandle> handles = [];
    public static void Detach()
    {
        Console.SetIn(TextReader.Null);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        if (OperatingSystem.IsWindows())
        {
            for (var index = 0; index < 3; index++)
            {
                var handle = File.OpenHandle("NUL", FileMode.Open, index == 0 ? FileAccess.Read : FileAccess.Write, FileShare.ReadWrite);
                var previous = GetStdHandle(-10 - index);
                if (!SetStdHandle(-10 - index, handle)) { handle.Dispose(); throw new IOException("Could not detach server standard streams."); }
                if (previous != IntPtr.Zero && previous != new IntPtr(-1)) CloseHandle(previous);
                handles.Add(handle); // Keep native standard handles alive until process exit.
            }
        }
        else
        {
            var descriptor = Open("/dev/null", 2);
            if (descriptor < 0) throw new IOException("Could not open /dev/null for the background server.");
            try { for (var index = 0; index < 3; index++) if (Duplicate(descriptor, index) < 0) throw new IOException("Could not detach server standard streams."); }
            finally { if (descriptor > 2) Close(descriptor); }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int identifier, SafeFileHandle handle);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int identifier);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int Duplicate(int oldDescriptor, int newDescriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
