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

using System.Diagnostics.CodeAnalysis;

namespace Bunyi.Core.Models;

/// <summary>
/// The path rules from /spec/DATA-FORMATS.md, applied to every entry a
/// self-hosted manifest names.
/// </summary>
/// <remarks>
/// <para>
/// This is a security boundary. A manifest names its own files and those names
/// become write destinations, and <c>&lt;base&gt;</c> is whatever the user
/// typed into Settings — not necessarily a server anyone audited.
/// </para>
/// <para>
/// <b>The rules are the same on every platform</b>, including the two that only
/// bite on Windows. A backslash and a colon are legal in a POSIX filename and
/// escape nothing on macOS or Linux, but on Windows <c>\</c> is a separator,
/// <c>C:/Windows</c> is drive-rooted and <c>C:foo</c> is drive-<i>relative</i>,
/// landing wherever that drive's working directory happens to point. An entry
/// that looks inert on one implementation and traverses on another is exactly
/// the failure this prevents, so both apps apply the same test rather than each
/// guarding its own platform.
/// </para>
/// </remarks>
public static class ManifestPath
{
    /// <summary>
    /// Accepts a relative path that cannot escape the model folder.
    /// </summary>
    /// <param name="entry">The path exactly as the manifest gave it.</param>
    /// <param name="relativePath">The accepted path, unchanged.</param>
    /// <returns>Whether the entry is safe to use as a write destination.</returns>
    public static bool TryNormalize(
        string? entry,
        [NotNullWhen(true)] out string? relativePath)
    {
        relativePath = null;

        if (string.IsNullOrEmpty(entry)) return false;
        if (entry[0] is '/' or '~') return false;

        // Rejected everywhere, not only where they are dangerous — see the
        // class remarks.
        if (entry.Contains('\\') || entry.Contains(':')) return false;

        // Splitting without removing empties is what catches "a//b" and a
        // trailing slash: neither names a file, and both suggest a manifest
        // built by hand.
        foreach (var component in entry.Split('/'))
        {
            if (component.Length == 0) return false;
            if (component is "." or "..") return false;
        }

        relativePath = entry;
        return true;
    }

    /// <summary>
    /// Resolves an accepted entry against the model folder, and proves the
    /// result is still inside it.
    /// </summary>
    /// <remarks>
    /// A second check after <see cref="TryNormalize"/>, against the resolved
    /// path rather than the text. The rules above are the contract and should
    /// be sufficient; this is the belt to their braces, because the cost of
    /// being wrong is writing an arbitrary file and the cost of the check is a
    /// string comparison.
    /// </remarks>
    public static bool TryResolve(
        string modelFolder,
        string? entry,
        [NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFolder);

        if (!TryNormalize(entry, out var relative)) return false;

        var root = Path.GetFullPath(modelFolder);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!combined.StartsWith(rootWithSeparator, StringComparison.Ordinal)) return false;

        fullPath = combined;
        return true;
    }
}
