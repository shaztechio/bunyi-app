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
using System.Text.RegularExpressions;

namespace Bunyi.Core.Engine;

public sealed record SrtCue(string Id, string Number, string OriginalText, string Text,
    string Start, string End, bool Included = true)
{
    public string SpokenText => SrtDocument.Normalize(Text);
    public string Flags => string.Join(" · ", new[]
    {
        Text.Contains('♪') || Text.Contains('♫') || (Text.IndexOf('[') is var bracket && bracket >= 0 && Text.IndexOf(']', bracket + 1) > bracket)
            ? "Possible non-speech caption — exclude if unwanted" : "",
        Regex.Replace(Text, @"</?(?:i|b|u)>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking).IndexOfAny(['<', '{']) >= 0
            ? "Unknown markup — review spoken text" : ""
    }.Where(s => s.Length > 0));
}

public sealed record SrtDiagnostic(string Id, string Number, string Status, string Message,
    long? SampleCount = null, double? DurationSeconds = null, double OverrunSeconds = 0, double OverlapSeconds = 0);

public static class SrtDocument
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumCues = 10_000;

    public static async Task<IReadOnlyList<SrtCue>> ReadAsync(string path, CancellationToken ct = default)
    {
        // Bound the read itself, including files that grow after opening.
        await using var file = File.OpenRead(path);
        using var data = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await file.ReadAsync(buffer, ct)) > 0)
        {
            if (data.Length + read > MaximumBytes) throw new InvalidDataException("SRT files must be at most 16 MiB.");
            data.Write(buffer, 0, read);
        }
        try { return Parse(new UTF8Encoding(false, true).GetString(data.ToArray())); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Save the subtitle file as UTF-8 and import it again.", ex); }
    }

    public static IReadOnlyList<SrtCue> Parse(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > MaximumBytes) throw new InvalidDataException("SRT files must be at most 16 MiB.");
        var lines = input.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cues = new List<SrtCue>();
        for (var i = 0; i < lines.Length;)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }
            var line = i + 1;
            var number = lines[i++].Trim();
            if (!Regex.IsMatch(number, @"^[0-9]+$") || i >= lines.Length || !lines[i].Contains("-->", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid SRT block at line {line}. Expected a cue number and a timestamp line.");
            var times = lines[i++].Split("-->", StringSplitOptions.TrimEntries);
            if (times.Length != 2) throw new InvalidDataException($"Invalid timestamp separator at line {line + 1}.");
            var text = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (lines[i].Contains("-->", StringComparison.Ordinal))
                    throw new InvalidDataException($"Unexpected timestamp at line {i + 1}. Separate cue blocks with a blank line.");
                text.Add(lines[i++]);
            }
            var original = string.Join('\n', text);
            cues.Add(new SrtCue(Guid.NewGuid().ToString("N"), number, original, original, times[0], times[1]));
            if (cues.Count > MaximumCues) throw new InvalidDataException("SRT files must contain at most 10,000 cues.");
        }
        if (cues.Count == 0) throw new InvalidDataException("The SRT file has no cues.");
        return cues;
    }

    public static string Normalize(string text) => Regex.Replace(
        Regex.Replace(text, @"</?(?:i|b|u)>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        @"\r\n|\r|\n", " ", RegexOptions.NonBacktracking).Trim();

    public static bool TrySamples(string text, out long samples)
    {
        samples = 0;
        var match = Regex.Match(text, @"^([0-9]{2,}):([0-5][0-9]):([0-5][0-9]),([0-9]{3})$");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var hours)) return false;
        try
        {
            samples = checked((hours * 3600000 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60000
                + int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) * 1000
                + int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)) * 24);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    public static IReadOnlyList<SrtDiagnostic> Check(IReadOnlyList<SrtCue> cues, Func<string, long?>? sampleCount = null)
    {
        var results = new List<SrtDiagnostic>();
        var included = cues.Where(c => c.Included).ToArray();
        long previous = -1;
        for (var i = 0; i < included.Length; i++)
        {
            var cue = included[i];
            var count = sampleCount?.Invoke(cue.Id);
            string? error = null;
            var startValid = TrySamples(cue.Start, out var start);
            var endValid = TrySamples(cue.End, out var end);
            if (!startValid || !endValid)
                error = "Use timestamps in HH:MM:SS,mmm format.";
            else if (end <= start) error = "End must be after start.";
            else if (start <= previous) error = "Starts must increase in file order. Edit the time or exclude this cue.";
            else if (cue.SpokenText.Length == 0) error = "Enter text to speak or exclude this cue.";
            previous = start;
            if (error is not null) { results.Add(new(cue.Id, cue.Number, "error", error)); continue; }
            var next = i + 1 < included.Length && TrySamples(included[i + 1].Start, out var nextStart) ? nextStart : (long?)null;
            var overrun = count is { } n ? Math.Max(0, n - (end - start)) / 24000d : 0;
            var overlap = count is { } m && next is { } ns ? Math.Max(0, m - (ns - start)) / 24000d : 0;
            var displayOverlap = next is { } nextSample && end > nextSample;
            var status = overlap > 0 ? "error" : overrun > 0 || displayOverlap ? "warning" : count is null ? "pending" : "pass";
            var message = overlap > 0 ? $"Speech overlaps cue {included[i + 1].Number} by {overlap:0.000} seconds."
                : overrun > 0 ? $"Speech exceeds its end by {overrun:0.000} seconds."
                : displayOverlap ? "Subtitle display interval overlaps the next cue; speech fit is checked separately."
                : count is null ? "Not generated; timing estimate is advisory." : "Fits";
            results.Add(new(cue.Id, cue.Number, status, message, count, count / 24000d, overrun, overlap));
        }
        return results;
    }

    public static void Validate(IReadOnlyList<SrtCue> cues)
    {
        if (cues.Count > MaximumCues || cues.Count == 0 || !cues.Any(c => c.Included))
            throw new InvalidDataException("Include between 1 and 10,000 cues.");
        if (cues.Select(c => c.Id).Distinct().Count() != cues.Count) throw new InvalidDataException("Cue identities must be unique.");
        var errors = Check(cues).Where(d => d.Status == "error").ToArray();
        if (errors.Length > 0) throw new InvalidDataException(string.Join(" ", errors.Select(d => $"Cue {d.Number}: {d.Message}")));
        if (Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(cues)) > MaximumBytes)
            throw new InvalidDataException("The edited timeline exceeds 16 MiB.");
    }
}
