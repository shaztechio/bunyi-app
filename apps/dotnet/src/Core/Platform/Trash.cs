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
using System.Text;
using Bunyi.Core.Diagnostics;

namespace Bunyi.Core.Platform;

/// <summary>
/// Moves a file somewhere the user can get it back from (spec §2a, §3d).
/// </summary>
/// <remarks>
/// <para>
/// The spec asks for the system Trash rather than an unrecoverable delete, and
/// means it: the row label is truncated so the wrong icon is easy to hit, and
/// the audio may be the only copy. <c>File.Delete</c> is not an implementation
/// of this on any platform.
/// </para>
/// <para>
/// Windows uses the shell's file operation with undo allowed, which is what
/// puts an entry in the Recycle Bin — a plain move into <c>$Recycle.Bin</c>
/// would not, because the Bin needs the metadata the shell writes. Linux uses
/// the freedesktop trash specification: the file moves to
/// <c>$XDG_DATA_HOME/Trash/files</c> and a <c>.trashinfo</c> beside it records
/// where it came from, which is what lets a file manager offer "restore".
/// </para>
/// </remarks>
public static class Trash
{
    /// <summary>Moves a file to the platform's trash.</summary>
    /// <returns>Whether it was trashed.</returns>
    public static bool TryMoveToTrash(string path, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            log.Log($"Cannot move {path} to the Trash: it is not there.");
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var ok = OperatingSystem.IsWindows()
                ? MoveToRecycleBin(full)
                : MoveToFreedesktopTrash(full);

            log.Log(ok
                ? $"Moved {Path.GetFileName(full)} to the Trash."
                : $"Could not move {Path.GetFileName(full)} to the Trash.");
            return ok;
        }
        catch (Exception ex)
        {
            // Never throws: this sits behind a confirmed button next to the
            // user's audio, and a failure to delete must not take the window
            // down with it.
            log.Log($"Could not move {Path.GetFileName(path)} to the Trash: {ex.Message}");
            return false;
        }
    }

    /// <summary>Moves a whole folder to the platform's trash.</summary>
    /// <remarks>
    /// §3d deletes a model, which is a folder of gigabytes. Same guarantee as a
    /// file: recoverable, because someone who deletes the wrong one should not
    /// have to download it again.
    /// </remarks>
    public static bool TryMoveFolderToTrash(string folder, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            log.Log($"Cannot move {folder} to the Trash: it is not there.");
            return false;
        }

        try
        {
            var full = Path.GetFullPath(folder);
            return OperatingSystem.IsWindows()
                ? MoveToRecycleBin(full)
                : MoveFolderToFreedesktopTrash(full);
        }
        catch (Exception ex)
        {
            log.Log($"Could not move {Path.GetFileName(folder)} to the Trash: {ex.Message}");
            return false;
        }
    }

    // ---- Windows -----------------------------------------------------------

    private const int FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;    // the flag that means "Recycle Bin"
    private const ushort FOF_NOCONFIRMATION = 0x0010;  // the app already confirmed
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_SILENT = 0x0004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private static bool MoveToRecycleBin(string path)
    {
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // Double null terminated: the API takes a list, and a single
            // terminator makes it read past the end of the string.
            pFrom = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };

        return SHFileOperation(ref operation) == 0 && operation.fAnyOperationsAborted == 0;
    }

    // ---- Linux and anything else -------------------------------------------

    private static bool MoveToFreedesktopTrash(string path)
    {
        var (files, info) = TrashFolders();

        // A name that is free in both places. The spec requires the pair to
        // stay together, so a name taken in either is taken in both.
        var name = UniqueName(files, info, Path.GetFileName(path));

        // The .trashinfo goes first: a file in files/ with no info/ entry is
        // unrestorable, which is exactly what this exists to avoid.
        File.WriteAllText(Path.Combine(info, name + ".trashinfo"), TrashInfoFor(path));

        try
        {
            File.Move(path, Path.Combine(files, name));
        }
        catch
        {
            // Do not leave an info entry pointing at nothing.
            TryDelete(Path.Combine(info, name + ".trashinfo"));
            throw;
        }

        return true;
    }

    private static bool MoveFolderToFreedesktopTrash(string folder)
    {
        var (files, info) = TrashFolders();
        var name = UniqueName(files, info, Path.GetFileName(folder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

        File.WriteAllText(Path.Combine(info, name + ".trashinfo"), TrashInfoFor(folder));

        try
        {
            Directory.Move(folder, Path.Combine(files, name));
        }
        catch
        {
            TryDelete(Path.Combine(info, name + ".trashinfo"));
            throw;
        }

        return true;
    }

    private static (string Files, string Info) TrashFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !Path.IsPathRooted(dataHome))
        {
            dataHome = Path.Combine(home, ".local", "share");
        }

        var trash = Path.Combine(dataHome, "Trash");
        return (Directory.CreateDirectory(Path.Combine(trash, "files")).FullName,
                Directory.CreateDirectory(Path.Combine(trash, "info")).FullName);
    }

    private static string TrashInfoFor(string original)
    {
        var deletionDate = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return new StringBuilder()
            .AppendLine("[Trash Info]")
            .AppendLine(CultureInfo.InvariantCulture, $"Path={EncodePath(original)}")
            .AppendLine(CultureInfo.InvariantCulture, $"DeletionDate={deletionDate}")
            .ToString();
    }

    private static string UniqueName(string files, string info, string name)
    {
        var candidate = name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var counter = 1;

        while (File.Exists(Path.Combine(files, candidate))
               || File.Exists(Path.Combine(info, candidate + ".trashinfo")))
        {
            candidate = $"{stem}.{counter++}{extension}";
        }

        return candidate;
    }

    /// <summary>
    /// Percent-encodes the original path, as the trash specification requires.
    /// </summary>
    /// <remarks>
    /// The separators stay literal — the value is a path, not a URI — so only
    /// the characters that would be ambiguous are escaped. Without this a file
    /// whose name contains a space or a percent sign restores to the wrong
    /// place, or not at all.
    /// </remarks>
    private static string EncodePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
