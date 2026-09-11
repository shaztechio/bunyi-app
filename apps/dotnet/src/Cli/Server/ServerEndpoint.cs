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

using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Bunyi.Core.Infrastructure;

namespace Bunyi.Cli.Server;

public sealed class ServerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A per-user local endpoint. The optional scope isolates integration tests.</summary>
public sealed class ServerEndpoint
{
    public string Name { get; }
    public string DirectoryPath { get; }
    public string SocketPath => Path.Combine(DirectoryPath, "server.sock");
    public string LockPath => Path.Combine(DirectoryPath, "server.lock");

    public ServerEndpoint(string? scope = null)
    {
        var identity = OperatingSystem.IsWindows()
            ? System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value
            : GetUserId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        scope ??= Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataRoot));
        var config = Path.GetFullPath(Environment.GetEnvironmentVariable("BUNYI_CONFIG_FILE") ?? AppPaths.SettingsFile);
        if (OperatingSystem.IsWindows()) { scope = scope.ToUpperInvariant(); config = config.ToUpperInvariant(); }
        Name = "bunyi-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + ":" + scope + ":" + config)))[..24].ToLowerInvariant();
        DirectoryPath = Path.Combine(Path.GetTempPath(), Name);
    }

    internal void PrepareDirectory()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
        else
        {
            Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var info = new DirectoryInfo(DirectoryPath);
            if (info.LinkTarget is not null || !IsOwner(DirectoryPath) || (File.GetUnixFileMode(DirectoryPath) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new ServerException("server_unavailable", "The local server directory must be private to the current user.");
        }
        if (new FileInfo(LockPath).LinkTarget is not null)
            throw new ServerException("server_unavailable", "The server lock path cannot be a symbolic link.");
    }

    internal async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(1000, ct); return pipe; }
            catch { pipe.Dispose(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
            VerifyPeer(socket);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static void VerifyPeer(Socket socket)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("The headless ONNX server supports Windows and Linux.");
        // Linux SO_PEERCRED returns pid, uid, gid. Check both accepted peers and servers.
        uint length = 12;
        if (GetPeerCredentials(socket.SafeHandle, 1, 17, out var credentials, ref length) != 0 || length != 12 || credentials.UserId != GetUserId())
            throw new ServerException("server_unavailable", "The local server endpoint belongs to another user.");
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetUserId();

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials { public int ProcessId; public uint UserId; public uint GroupId; }

    // Use native SO_PEERCRED: SocketOptionName is translated by .NET and cannot
    // represent this Linux-only option by casting the native numeric constant.
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetPeerCredentials(SafeSocketHandle socket, int level, int option, out PeerCredentials credentials, ref uint length);

    private static bool IsOwner(string path)
    {
        // Linux statx has a fixed architecture-independent layout. stx_uid is at byte 20.
        var buffer = Marshal.AllocHGlobal(256);
        try { return Statx(-100, path, 0x100, 0x8, buffer) == 0 && unchecked((uint)Marshal.ReadInt32(buffer, 20)) == GetUserId(); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFd, string path, int flags, uint mask, IntPtr buffer);
}
