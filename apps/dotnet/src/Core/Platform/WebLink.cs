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

/// <summary>What to run to open a link, and with what arguments.</summary>
public sealed record LinkCommand(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Opens a web link in whatever browser the user already uses.
/// </summary>
/// <remarks>
/// <para>
/// Built the same way as <see cref="FileReveal" />, and for the same reason:
/// launching a browser cannot be asserted on in CI, but getting the command
/// wrong can be, so choosing it and running it are separate.
/// </para>
/// <para>
/// <b>Only https.</b> The links this app opens are constants compiled into it,
/// so nothing hostile should ever reach here — but "should" is doing work in
/// that sentence, and handing an arbitrary string to a shell handler is how
/// that assumption turns into someone else's program starting. The guard costs
/// one comparison.
/// </para>
/// </remarks>
public static class WebLink
{
    /// <summary>Whether this is a link worth handing to a browser.</summary>
    public static bool IsSafe(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// The command that opens a link, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Each platform's own handler, so the link opens in the browser the user
    /// has chosen rather than one this app picks for them.
    /// </remarks>
    public static LinkCommand? CommandFor(string? url)
    {
        if (!IsSafe(url)) return null;

        // Re-serialised through Uri rather than passed through as typed: this is
        // the string that reaches a shell handler, and it should be one the
        // parser produced, not one a caller assembled.
        var safe = new Uri(url!).AbsoluteUri;

        if (OperatingSystem.IsWindows()) return new LinkCommand("explorer.exe", [safe]);
        if (OperatingSystem.IsMacOS()) return new LinkCommand("open", [safe]);

        return new LinkCommand("xdg-open", [safe]);
    }

    /// <summary>
    /// Opens a link, best effort.
    /// </summary>
    /// <remarks>
    /// A link that will not open is a disappointment, not a failure of the
    /// thing the user was doing — so it is logged rather than thrown.
    /// </remarks>
    public static void Open(string? url, ILogSink? log = null)
    {
        var command = CommandFor(url);

        if (command is null)
        {
            log?.Log($"Refused to open a link that is not https: {url}");
            return;
        }

        try
        {
            var start = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
            foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
        }
        catch (Exception ex)
        {
            log?.Log($"Could not open {url}: {ex.Message}");
        }
    }
}
