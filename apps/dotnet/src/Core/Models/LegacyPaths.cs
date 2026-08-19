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

using Bunyi.Core.Diagnostics;

namespace Bunyi.Core.Models;

/// <summary>
/// Tidies up where earlier builds put things.
/// </summary>
/// <remarks>
/// Small and, with luck, short-lived. It exists because a models folder outlives
/// the version of the app that filled it, and leaving files where nothing looks
/// for them is worse than moving them.
/// </remarks>
public static class LegacyPaths
{
    /// <summary>The corner an earlier build fetched the Whisper model into.</summary>
    internal const string OldWhisperFolder = "whisper";

    /// <summary>
    /// Moves the Whisper model in beside the others, once.
    /// </summary>
    /// <remarks>
    /// It was fetched into <c>&lt;root&gt;/whisper/models/…</c>, which is
    /// invisible to Settings ▸ Storage — that lists
    /// <c>models/&lt;org&gt;/&lt;repo&gt;</c> — so 141 MB could be neither seen
    /// nor deleted. It also put a second <c>models/</c> tree inside every
    /// backup, where a restore expects exactly one.
    /// </remarks>
    /// <returns>Whether anything was moved.</returns>
    public static bool MoveMisplacedWhisper(string modelsRoot, ILogSink? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);

        var old = Path.Combine(modelsRoot, OldWhisperFolder, "models");
        if (!Directory.Exists(old)) return false;

        var moved = false;

        try
        {
            foreach (var org in Directory.GetDirectories(old))
            {
                foreach (var repo in Directory.GetDirectories(org))
                {
                    var destination = Path.Combine(
                        modelsRoot, "models", Path.GetFileName(org), Path.GetFileName(repo));

                    if (Directory.Exists(destination))
                    {
                        // Already there, so the copy in the corner is the spare.
                        Directory.Delete(repo, recursive: true);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    Directory.Move(repo, destination);
                    moved = true;

                    log?.Log($"Moved {Path.GetFileName(repo)} in with the other models.");
                }
            }

            // Files, not entries: moving a repository leaves its now-empty org
            // folder behind, and that is not a reason to keep 141 MB worth of
            // directory structure nothing looks in. Anything that is still a
            // file in there was not put there by this app, and stays.
            if (!Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(Path.Combine(modelsRoot, OldWhisperFolder), recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth failing a launch over. The worst case is the model is
            // fetched again, which is a 141 MB annoyance rather than a fault.
            log?.Log($"Could not tidy up the old Whisper folder. {ex.Message}");
        }

        return moved;
    }
}
