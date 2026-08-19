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
using System.Text;
using Bunyi.Core.Models;

namespace Bunyi.Core.Audio;

/// <summary>One clip in History (spec §2a).</summary>
/// <param name="Path">Where it is on disk.</param>
/// <param name="Created">When it was written.</param>
/// <param name="SizeBytes">How large it is.</param>
/// <param name="Metadata">What produced it, or null if the file carries none.</param>
public sealed record GeneratedOutput(
    string Path,
    DateTimeOffset Created,
    long SizeBytes,
    OutputMetadata? Metadata)
{
    /// <summary>The file's name, used when nothing better is known.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The mode that produced it, for the row's tag.</summary>
    public string Mode => Metadata?.Mode ?? "Unknown";

    /// <summary>The voice, however the mode chose one.</summary>
    public string? Voice => Metadata?.VoiceSummary();

    /// <summary>
    /// The one line a row shows: what was said, and how.
    /// </summary>
    /// <remarks>
    /// A prompt can be paragraphs long, so the row is a summary and the whole
    /// record is on hover. A file with no metadata says so rather than showing
    /// a bare filename, which reads like a fault rather than an absence.
    /// </remarks>
    public string Summary()
    {
        if (Metadata is null) return FileName;

        var title = Metadata.Title();
        return string.IsNullOrWhiteSpace(title) ? FileName : title;
    }

    /// <summary>Size as a person would write it.</summary>
    public string SizeText() => DownloadProgress.Bytes(SizeBytes);

    /// <summary>
    /// Everything known about the clip, for the hover tooltip and for Copy
    /// details (spec §2a).
    /// </summary>
    /// <remarks>
    /// Hover is for looking; a tooltip cannot be pasted into a note, a bug
    /// report, or back into the app to reproduce a result. The same text serves
    /// both so the two can never drift.
    /// </remarks>
    public string Details()
    {
        var text = new StringBuilder();

        if (Metadata is null)
        {
            text.AppendLine(FileName);
            text.AppendLine();
            text.AppendLine("This file does not carry any details about how it was made.");
            text.AppendLine("It may have been produced by another program.");
            text.AppendLine(CultureInfo.InvariantCulture, $"Created: {Local(Created)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Size: {SizeText()}");
            return text.ToString().TrimEnd();
        }

        Add("Text", Metadata.Text);
        Add("Mode", Metadata.Mode);
        Add("Language", Metadata.Language);
        Add("Speaker", Metadata.Speaker);
        Add("Style", Metadata.Style);
        Add("Voice", Metadata.VoiceDescription);
        Add("Reference transcript", Metadata.ReferenceTranscript);
        Add("Model", Metadata.ModelRepo);
        Add("Created", Local(Metadata.Created));
        Add("Size", SizeText());
        Add("File", Path);
        // The platform is the file's own, not this machine's. A clip made on a
        // Mac and opened here still says macOS, which is the only answer that
        // stays true when the file travels.
        Add("Made with", Metadata.Platform is { Length: > 0 } platform
            ? $"Bunyi {Metadata.AppVersion} ({platform})"
            : $"Bunyi {Metadata.AppVersion}");

        return text.ToString().TrimEnd();

        // Empty values are omitted, as they are in the file itself: a blank
        // line for a field the mode never had reads as missing data.
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {value}");
            }
        }
    }

    private static string Local(DateTimeOffset when) =>
        when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads the Outputs folder (spec §2a).
/// </summary>
/// <remarks>
/// <b>The folder is the record</b>, not an in-app database. The list is read
/// each time it is shown, so a file deleted outside the app disappears from
/// History, and the list survives relaunches with no state to migrate. It is
/// deliberately named History and not Library — "library" already means the
/// saved voices (§5).
/// </remarks>
public static class GeneratedOutputs
{
    /// <summary>Everything in the folder, newest first.</summary>
    public static IReadOnlyList<GeneratedOutput> Read(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder)) return [];

        var outputs = new List<GeneratedOutput>();

        foreach (var path in Directory.EnumerateFiles(folder, "*.wav", SearchOption.TopDirectoryOnly))
        {
            GeneratedOutput? output;
            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0) continue;   // an interrupted write is not a clip

                output = new GeneratedOutput(
                    path,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    info.Length,
                    WavMetadata.TryRead(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file being written, or one we cannot read, is skipped rather
                // than failing the whole list.
                continue;
            }

            outputs.Add(output);
        }

        return [.. outputs.OrderByDescending(o => o.Created)];
    }
}
