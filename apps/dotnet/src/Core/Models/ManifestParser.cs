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

namespace Bunyi.Core.Models;

/// <summary>One file a manifest names, with its digest when it published one.</summary>
/// <param name="RelativePath">Path under the model folder. Already checked safe.</param>
/// <param name="Sha256">Lowercase hex digest, or null when the manifest gave none.</param>
/// <param name="Required">Whether a 404 fails the whole download (spec §3c).</param>
public sealed record ModelFile(string RelativePath, string? Sha256 = null, bool Required = false);

/// <summary>The outcome of reading a manifest, including what was thrown away.</summary>
/// <param name="Files">Entries that passed the path rules.</param>
/// <param name="Rejected">Entries that did not, verbatim, for the log.</param>
public sealed record ManifestReadResult(
    IReadOnlyList<ModelFile> Files,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Reads <c>manifest.sha256</c> and <c>manifest.txt</c> — one parser, because
/// /spec/DATA-FORMATS.md defines them as the same format with the digest
/// optional.
/// </summary>
public static class ManifestParser
{
    /// <summary>Parses manifest text, dropping unsafe entries.</summary>
    public static ManifestReadResult Parse(string? text)
    {
        var files = new List<ModelFile>();
        var rejected = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return new ManifestReadResult(files, rejected);

        // Case-insensitive, because a manifest offering both Model.onnx and
        // model.onnx would resolve to one destination on Windows and two on
        // Linux — the same manifest producing a different model folder per
        // platform, which is what DATA-FORMATS' interchangeability rests on.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var (digest, path) = SplitLine(line);

            if (!ManifestPath.TryNormalize(path, out var safe))
            {
                rejected.Add(line);
                continue;
            }

            if (!seen.Add(safe))
            {
                rejected.Add(line);
                continue;
            }

            files.Add(new ModelFile(safe, digest));
        }

        return new ManifestReadResult(files, rejected);
    }

    /// <summary>
    /// Splits a line into an optional digest and a path.
    /// </summary>
    /// <remarks>
    /// The separator is <b>any whitespace</b>, not the two spaces
    /// <c>shasum</c> happens to write: tools do emit tabs, and a client
    /// splitting on a literal space would read such a line as a bare path and
    /// skip verification <i>silently</i> — a file fetched unverified while the
    /// manifest looked honoured.
    /// </remarks>
    private static (string? Digest, string Path) SplitLine(string line)
    {
        var separator = line.IndexOfAny([' ', '\t', '\v', '\f']);
        if (separator < 0) return (null, line);

        var first = line[..separator];
        if (!IsSha256Hex(first)) return (null, line);

        var rest = line[(separator + 1)..].TrimStart();

        // A leading '*' is how shasum marks a binary-mode read. Harmless to it,
        // a 404 to us.
        if (rest.StartsWith('*')) rest = rest[1..];

        return (first.ToLowerInvariant(), rest.Trim());
    }

    /// <summary>
    /// Whether a token is exactly 64 hex digits. Anything else means the line
    /// is a bare path, which is what lets one parser read both files and makes
    /// a digest-less line legal.
    /// </summary>
    public static bool IsSha256Hex(string token)
    {
        if (token.Length != 64) return false;
        foreach (var c in token)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }
        return true;
    }
}
