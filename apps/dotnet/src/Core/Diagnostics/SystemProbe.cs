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

using System.Globalization;
using System.Runtime.InteropServices;

namespace Bunyi.Core.Diagnostics;

/// <summary>What the machine can currently offer (spec §11).</summary>
/// <remarks>
/// An interface because Doctor's whole job is reporting on the machine, and a
/// test cannot arrange for one to be short of memory. The real implementation
/// is thin enough that mocking it loses nothing worth keeping.
/// </remarks>
public interface ISystemProbe
{
    /// <summary>
    /// Memory that could be given to a new allocation, or null if unknown.
    /// </summary>
    long? AvailableMemoryBytes();

    /// <summary>Free space on the volume holding a path, or null if unknown.</summary>
    long? FreeSpaceBytes(string path);

    /// <summary>
    /// Whether an NVIDIA driver is installed, or null if it cannot be told.
    /// </summary>
    /// <remarks>
    /// Only ever asked to explain a CPU answer: "this machine has the card and
    /// is still not using it" is a different report from "this machine has no
    /// card", and §11 requires a finding to say what would resolve it. Default
    /// implemented as "cannot tell" so a test probe need not answer it.
    /// </remarks>
    bool? HasNvidiaDriver() => null;
}

/// <summary>Reads the real machine.</summary>
public sealed class SystemProbe : ISystemProbe
{
    /// <inheritdoc />
    /// <remarks>
    /// The driver's own CUDA library, not the toolkit: <c>nvcuda</c> ships with
    /// the display driver, so finding it means "there is an NVIDIA card here"
    /// rather than "CUDA will work". Whether CUDA works is a separate and
    /// harder question, answered by building session options — see
    /// <see cref="Engine.OnnxRuntimeEnv.CudaLoads"/>.
    /// </remarks>
    public bool? HasNvidiaDriver()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return null;

        var name = OperatingSystem.IsWindows() ? "nvcuda.dll" : "libcuda.so.1";

        try
        {
            if (!NativeLibrary.TryLoad(name, out var handle)) return false;
            NativeLibrary.Free(handle);
            return true;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public long? AvailableMemoryBytes()
    {
        try
        {
            return OperatingSystem.IsWindows() ? WindowsAvailable() : UnixAvailable();
        }
        catch (Exception ex) when (ex is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public long? FreeSpaceBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            // The volume holding the folder, which §3d allows the user to
            // choose — so it is not necessarily the one the app is installed
            // on. Walks up because the folder may not exist yet.
            var existing = NearestExistingFolder(path);
            if (existing is null) return null;

            return new DriveInfo(Path.GetPathRoot(existing) ?? existing).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NearestExistingFolder(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) return null;
            current = parent;
        }
        return current;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long? WindowsAvailable()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.ullAvailPhys : null;
    }

    /// <summary>
    /// Reads <c>MemAvailable</c> from <c>/proc/meminfo</c>.
    /// </summary>
    /// <remarks>
    /// <c>MemAvailable</c> rather than <c>MemFree</c>: the kernel's own estimate
    /// of what a new allocation could have, which counts reclaimable cache.
    /// <c>MemFree</c> on a warm machine is close to nothing and would make every
    /// run look impossible.
    /// </remarks>
    private static long? UnixAvailable()
    {
        const string path = "/proc/meminfo";
        if (!File.Exists(path)) return null;

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;

            var digits = line.AsSpan("MemAvailable:".Length).Trim();
            var end = digits.IndexOf(' ');
            if (end > 0) digits = digits[..end];

            if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
            {
                return kb * 1024;
            }
        }

        return null;
    }
}
