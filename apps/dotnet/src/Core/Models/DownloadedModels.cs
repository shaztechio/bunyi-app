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

/// <summary>Where a downloaded model came from.</summary>
public enum ModelOrigin
{
    /// <summary>A Hugging Face repository, under <c>models/&lt;org&gt;/&lt;repo&gt;</c>.</summary>
    Hub,

    /// <summary>A server the user gave, under <c>models/self-hosted/&lt;slug&gt;</c>.</summary>
    SelfHosted,
}

/// <summary>One model on disk (spec §3d).</summary>
public sealed record DownloadedModel(string Name, ModelOrigin Origin, string Folder, long SizeBytes)
{
    /// <summary>Its size, as a person would write it.</summary>
    public string SizeText() => DownloadProgress.Bytes(SizeBytes);

    /// <summary>Where it came from, for the row.</summary>
    public string OriginText() => Origin == ModelOrigin.Hub ? "Hugging Face" : "Your server";
}

/// <summary>
/// The models on disk, so they can be listed and reclaimed (spec §3d).
/// </summary>
/// <remarks>
/// Reclaiming several gigabytes must not require knowing where the app keeps
/// its files. The folder is somewhere a user has no reason to have seen, and on
/// macOS it is inside a sandbox container — not somewhere anyone can reasonably
/// be sent.
/// </remarks>
public static class DownloadedModels
{
    /// <summary>Everything under the models root, largest first.</summary>
    public static IReadOnlyList<DownloadedModel> Read(string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);

        var models = new List<DownloadedModel>();
        var root = Path.Combine(modelsRoot, "models");
        if (!Directory.Exists(root)) return models;

        foreach (var orgFolder in SafeDirectories(root))
        {
            var org = Path.GetFileName(orgFolder);

            if (string.Equals(org, "self-hosted", StringComparison.OrdinalIgnoreCase))
            {
                // models/self-hosted/<slug>
                foreach (var slugFolder in SafeDirectories(orgFolder))
                {
                    models.Add(new DownloadedModel(
                        Path.GetFileName(slugFolder), ModelOrigin.SelfHosted,
                        slugFolder, SizeOf(slugFolder)));
                }
                continue;
            }

            // models/<org>/<repo>
            foreach (var repoFolder in SafeDirectories(orgFolder))
            {
                models.Add(new DownloadedModel(
                    $"{org}/{Path.GetFileName(repoFolder)}", ModelOrigin.Hub,
                    repoFolder, SizeOf(repoFolder)));
            }
        }

        return [.. models.OrderByDescending(m => m.SizeBytes)];
    }

    /// <summary>How much is on disk in total.</summary>
    public static long TotalBytes(string modelsRoot) => Read(modelsRoot).Sum(m => m.SizeBytes);

    /// <summary>
    /// Moves a model's folder to the Trash, after the caller has confirmed.
    /// </summary>
    /// <remarks>
    /// <b>Evicting a loaded model from memory first is the caller's job</b>, and
    /// §3d insists on it: otherwise the app keeps generating from a model whose
    /// files are gone and silently re-downloads on the next launch. On Windows
    /// it is not merely tidy — a loaded ONNX session holds its
    /// <c>.onnx.data</c> open, and the delete fails outright.
    /// </remarks>
    public static bool TryDelete(DownloadedModel model, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(log);

        if (!Directory.Exists(model.Folder))
        {
            log.Log($"Cannot delete {model.Name}: it is not there.");
            return false;
        }

        var trashed = Platform.Trash.TryMoveFolderToTrash(model.Folder, log);
        if (trashed) log.Log($"Deleted {model.Name} ({model.SizeText()}).");
        return trashed;
    }

    /// <summary>
    /// The command to fetch a model in advance (spec §3d).
    /// </summary>
    /// <remarks>
    /// Shown with the real folder path filled in, so it can be copied and run.
    /// Returns null for a mode pointed at the user's own server: there is no
    /// repository name to give the tool, and emitting a line with a URL where a
    /// repo id belongs produces a command that cannot work.
    /// </remarks>
    public static string? PreDownloadCommand(ModelSource source, string modelsRoot)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source is not ModelSource.Repo repo) return null;

        var destination = Path.Combine(modelsRoot, "models", repo.Id.Replace('/', Path.DirectorySeparatorChar));
        return $"hf download {repo.Id} --local-dir \"{destination}\"";
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static long SizeOf(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
