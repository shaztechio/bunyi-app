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

using System.IO.Compression;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Models;

namespace Bunyi.Core;

/// <summary>How far a backup or restore has got (spec §6).</summary>
/// <param name="Fraction">0 to 1, or 0 when nothing is known yet.</param>
/// <param name="Detail">A line for a person, or null.</param>
public sealed record BackupProgress(double Fraction, string? Detail = null)
{
    /// <summary>The bytes-based fraction, as a percentage for a bar.</summary>
    public static BackupProgress Of(long done, long total, string? detail = null) =>
        new(total <= 0 ? 0 : Math.Clamp(done / (double)total, 0, 1), detail);
}

/// <summary>What a restore found in an archive, before touching anything.</summary>
/// <param name="Repos">The repositories it holds, as <c>org/name</c>.</param>
/// <param name="Bytes">How much will be written if all of them are.</param>
public sealed record BackupContents(IReadOnlyList<string> Repos, long Bytes);

/// <summary>
/// Backing the models folder up, and putting it back (spec §6).
/// </summary>
/// <remarks>
/// <para>
/// The point of this is not tidiness: a models folder is several gigabytes
/// fetched over a slow link, and the alternative to a backup is downloading it
/// again on the next machine.
/// </para>
/// <para>
/// <b>Stored, never compressed.</b> Weights are already incompressible, so
/// deflating them spends a great deal of CPU to save almost nothing — and it
/// costs the determinate progress bar, because a compressed archive's size no
/// longer tracks the bytes read.
/// </para>
/// </remarks>
public sealed class BackupManager(ILogSink log)
{
    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <summary>The folder inside an archive that holds the repositories.</summary>
    internal const string ModelsEntry = "models";

    /// <summary>
    /// Archives the models folder to one zip (spec §6).
    /// </summary>
    /// <remarks>
    /// Built beside its destination under a temporary name and moved into place
    /// at the end. The move is a rename within one directory, so it cannot half
    /// happen — and a run that is cancelled or fails leaves nothing at the path
    /// the user chose, rather than a truncated archive that looks like a backup.
    /// </remarks>
    public async Task BackupAsync(
        string modelsFolder,
        string destinationZip,
        IProgress<BackupProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZip);

        if (!Directory.Exists(modelsFolder))
        {
            throw new DirectoryNotFoundException(
                $"There is no models folder at {modelsFolder} to back up.");
        }

        var files = Directory
            .EnumerateFiles(modelsFolder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "There are no models to back up yet. Generate something first, or "
                + "download a model in Settings.");
        }

        var total = files.Sum(f => new FileInfo(f).Length);
        _log.Log($"Backing up {files.Count} files ({Bytes(total)}) to {destinationZip}.");

        var folder = Path.GetDirectoryName(Path.GetFullPath(destinationZip));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        var temporary = destinationZip + ".partial";

        try
        {
            var done = 0L;

            await using (var stream = File.Create(temporary))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(modelsFolder, file)
                        .Replace(Path.DirectorySeparatorChar, '/');

                    // NoCompression: see the class remarks. This is the whole
                    // reason a backup of 4 GB takes a minute rather than ten.
                    var entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);

                    await using var source = File.OpenRead(file);
                    await using var target = entry.Open();
                    done = await CopyAsync(source, target, done, total, relative, progress, ct)
                        .ConfigureAwait(false);
                }
            }

            File.Move(temporary, destinationZip, overwrite: true);
            progress?.Report(new BackupProgress(1, "Finished."));

            _log.Log($"Backed up to {destinationZip} ({Bytes(new FileInfo(destinationZip).Length)}).");
        }
        catch
        {
            // Nothing half-written survives, at the temporary name or the real
            // one. A cancelled backup should leave the folder as it found it.
            Delete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Reads what an archive holds, without writing anything (spec §6).
    /// </summary>
    /// <remarks>
    /// Separate from the restore so the window can say what is about to happen
    /// — and so an archive that is not a Bunyi backup is refused before any
    /// file is touched, rather than half way through.
    /// </remarks>
    public BackupContents Inspect(string sourceZip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceZip);

        if (!File.Exists(sourceZip))
        {
            throw new FileNotFoundException($"There is no backup at {sourceZip}.", sourceZip);
        }

        using var archive = ZipFile.OpenRead(sourceZip);

        var prefix = FindModels(archive)
            ?? throw new InvalidDataException(
                "That zip does not look like a Bunyi backup — it has no models folder "
                + "inside it. Choose the .zip a backup produced.");

        var repos = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var bytes = 0L;

        foreach (var entry in archive.Entries)
        {
            if (RepoOf(entry.FullName, prefix) is not { } repo) continue;

            repos.Add(repo);
            bytes += entry.Length;
        }

        if (repos.Count == 0)
        {
            throw new InvalidDataException(
                "That backup has a models folder but no models in it.");
        }

        return new BackupContents([.. repos], bytes);
    }

    /// <summary>
    /// Merges an archive into the models folder (spec §6).
    /// </summary>
    /// <remarks>
    /// <b>Never clobbers.</b> A repository already on disk is skipped whole
    /// rather than merged file by file: a half-replaced model is the one
    /// outcome worse than either keeping or replacing it, and the folder on
    /// disk is the one that has already been verified complete.
    /// </remarks>
    public async Task<IReadOnlyList<string>> RestoreAsync(
        string sourceZip,
        string modelsFolder,
        IProgress<BackupProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsFolder);

        var contents = Inspect(sourceZip);

        using var archive = ZipFile.OpenRead(sourceZip);
        var prefix = FindModels(archive)!;

        var skipped = new List<string>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repo in contents.Repos)
        {
            var already = Path.Combine(
                modelsFolder, ModelsEntry, repo.Replace('/', Path.DirectorySeparatorChar));

            if (Directory.Exists(already) && Directory.EnumerateFileSystemEntries(already).Any())
            {
                skipped.Add(repo);
                _log.Log($"Keeping the copy of {repo} already here; the backup's is skipped.");
            }
            else
            {
                wanted.Add(repo);
            }
        }

        var total = archive.Entries
            .Where(e => RepoOf(e.FullName, prefix) is { } r && wanted.Contains(r))
            .Sum(e => e.Length);

        _log.Log(
            $"Restoring {wanted.Count} of {contents.Repos.Count} model(s) ({Bytes(total)}); "
            + $"{skipped.Count} already here.");

        var done = 0L;
        var restored = new List<string>();

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            if (RepoOf(entry.FullName, prefix) is not { } repo || !wanted.Contains(repo)) continue;

            // The archive's own paths decide where files land, so they are
            // checked the same way a download manifest's are: an entry that
            // climbs out of the models folder is refused, not written.
            var relative = entry.FullName[prefix.Length..].TrimStart('/');
            if (!ManifestPath.TryNormalize(relative, out var safe))
            {
                _log.Log($"Skipped an unsafe path in the backup: {entry.FullName}");
                continue;
            }

            var destination = Path.Combine(modelsFolder, ModelsEntry, safe);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using (var source = entry.Open())
            await using (var target = File.Create(destination))
            {
                done = await CopyAsync(source, target, done, total, repo, progress, ct)
                    .ConfigureAwait(false);
            }

            if (!restored.Contains(repo)) restored.Add(repo);
        }

        progress?.Report(new BackupProgress(1, "Finished."));
        _log.Log($"Restored {restored.Count} model(s).");

        return skipped;
    }

    /// <summary>
    /// The <c>models/</c> prefix inside an archive, or null if there is none.
    /// </summary>
    /// <remarks>
    /// "At or near the root": a zip made by a file manager often wraps
    /// everything in a folder named after the archive, and refusing those would
    /// reject backups that are perfectly good.
    /// </remarks>
    internal static string? FindModels(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split('/');

            for (var i = 0; i < Math.Min(parts.Length, 2); i++)
            {
                if (string.Equals(parts[i], ModelsEntry, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join('/', parts[..(i + 1)]) + "/";
                }
            }
        }

        return null;
    }

    /// <summary>The <c>org/name</c> an entry belongs to, or null.</summary>
    internal static string? RepoOf(string entryPath, string prefix)
    {
        if (!entryPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var rest = entryPath[prefix.Length..].Split('/');

        // org/name/file at the least. A file sitting directly in models/
        // belongs to no repository and is not restored.
        return rest.Length >= 3 ? $"{rest[0]}/{rest[1]}" : null;
    }

    /// <returns>The running total, since an async method cannot take it by ref.</returns>
    private static async Task<long> CopyAsync(
        Stream source, Stream target,
        long done, long total, string what,
        IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[81_920];
        var sinceReport = 0L;
        var running = done;

        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

            running += read;
            sinceReport += read;

            // Every few megabytes rather than every buffer: a 4 GB backup is
            // fifty thousand buffers, and reporting each one costs more than the
            // copying does.
            if (sinceReport >= 4_000_000)
            {
                sinceReport = 0;
                progress?.Report(BackupProgress.Of(running, total, what));
            }
        }

        return running;
    }

    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Log($"Could not clean up {path}. {ex.Message}");
        }
    }

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.0} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0} MB",
        _ => $"{bytes / 1_000.0:0} KB",
    };
}
