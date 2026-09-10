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

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Bunyi.Cli.Server;

internal static class BackgroundProcess
{
    internal static Process Start(ProcessStartInfo info)
    {
        if (!OperatingSystem.IsWindows()) return Process.Start(info) ?? throw new IOException("Could not start the server.");
        // ProcessStartInfo redirects the three standard handles, but Windows may
        // still inherit other inheritable pipes belonging to the calling agent.
        // Inherit NO handles; the child opens its own NUL standard handles.
        var commandLine = new StringBuilder(string.Join(" ", new[] { info.FileName }.Concat(info.ArgumentList).Select(Quote)));
        var environment = string.Join('\0', info.Environment.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        var environmentPointer = Marshal.StringToHGlobalUni(environment);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        try
        {
            if (!CreateProcess(info.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x08000400,
                environmentPointer, info.WorkingDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the background server.");
            try { return Process.GetProcessById(unchecked((int)process.ProcessId)); }
            finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
        }
        finally { Marshal.FreeHGlobal(environmentPointer); }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2);
        return result.Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharacterWidth, CharacterHeight, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
