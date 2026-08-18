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

namespace Bunyi.Core.Models;

/// <summary>What the downloader is doing.</summary>
public enum DownloadPhase
{
    /// <summary>Deciding whether anything needs downloading at all.</summary>
    Resolving,
    /// <summary>Fetching manifest.sha256, then manifest.txt.</summary>
    Manifest,
    /// <summary>Asking the server how large each file is, to build a real total.</summary>
    Sizing,
    /// <summary>Bytes are moving.</summary>
    Downloading,
    /// <summary>Hashing what arrived.</summary>
    Verifying,
    Done,
}

/// <summary>
/// Byte-level progress for a whole download (spec §3b).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bytes, not files.</b> §3b requires progress to follow bytes received from
/// the network rather than completed files, because a model is one enormous
/// file and a dozen small ones: per-file progress sits still for minutes on the
/// big one, which is what made a healthy download look dead. That was a real
/// bug on the macOS self-hosted path, and the shape of this record is what
/// prevents it here — there is nowhere to report "file 3 of 13" as the fraction.
/// </para>
/// <para>
/// <see cref="BytesReused"/> counts toward the total. A resumed download that
/// skipped 4 GB should not show 0% while it fetches the last 200 MB.
/// </para>
/// </remarks>
public sealed record DownloadProgress(
    DownloadPhase Phase,
    long BytesReceived = 0,
    long BytesReused = 0,
    long BytesTotal = 0,
    double BytesPerSecond = 0,
    TimeSpan? Eta = null,
    string? CurrentFile = null,
    int FilesDone = 0,
    int FilesTotal = 0)
{
    /// <summary>0 to 1, or 0 when the total is not known yet.</summary>
    public double Fraction => BytesTotal > 0
        ? Math.Clamp((double)(BytesReceived + BytesReused) / BytesTotal, 0, 1)
        : 0;

    /// <summary>
    /// The human line beside the bar — "42% — about 3.1 MB/s, ~6 min left".
    /// </summary>
    /// <remarks>
    /// Worded to match the macOS app, so the two read identically and a
    /// screenshot from either means the same thing.
    /// </remarks>
    public string Human()
    {
        var percent = (int)Math.Round(Fraction * 100);

        return Phase switch
        {
            DownloadPhase.Manifest => "Looking for a file list…",
            DownloadPhase.Sizing => "Working out the download size…",
            DownloadPhase.Verifying => CurrentFile is null
                ? "Checking the files…"
                : $"Checking {CurrentFile}…",
            DownloadPhase.Done => "Done",
            DownloadPhase.Resolving => "Checking what is already downloaded…",
            _ when BytesTotal <= 0 => "Downloading…",
            _ when BytesPerSecond <= 0 => $"{percent}%",
            _ => $"{percent}% — about {Rate(BytesPerSecond)}/s, {EtaText(Eta)}",
        };
    }

    /// <summary>Bytes as a person would write them.</summary>
    public static string Rate(double bytesPerSecond) => Bytes((long)Math.Round(bytesPerSecond));

    /// <summary>A size in the units the user's file manager would use.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        // Whole bytes; one decimal once the number is small enough for it to
        // mean something.
        var text = unit == 0
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
        return $"{text} {units[unit]}";
    }

    /// <summary>
    /// Time remaining, phrased as an estimate because it is one.
    /// </summary>
    /// <remarks>
    /// Under a minute is not given in seconds: the number would be wrong as
    /// often as right at that resolution, and "about 12 seconds left" that
    /// takes 40 reads as a broken app rather than a rough guess.
    /// </remarks>
    public static string EtaText(TimeSpan? eta)
    {
        if (eta is not { } remaining || remaining < TimeSpan.Zero) return "time left unknown";
        if (remaining.TotalSeconds < 90) return "under a minute left";
        if (remaining.TotalMinutes < 90) return $"~{(int)Math.Round(remaining.TotalMinutes)} min left";
        return $"~{remaining.TotalHours:0.#} hours left";
    }
}
