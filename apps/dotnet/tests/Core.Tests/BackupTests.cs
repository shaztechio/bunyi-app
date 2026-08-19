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
using Bunyi.Core;
using Bunyi.Core.Diagnostics;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Backing the models folder up and putting it back (spec §6).
/// </summary>
/// <remarks>
/// The archive is a contract — a backup made on one machine is restored on
/// another, and DATA-FORMATS pins its shape — so most of this checks the file
/// rather than the object that wrote it.
/// </remarks>
public sealed class BackupTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();

    private string Models => Path.Combine(_root, "ModelsFolder");

    private string Zip => Path.Combine(_root, "backup.zip");

    public BackupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- The archive ----

    [Fact]
    public async Task A_backup_holds_every_file_in_the_models_folder()
    {
        Model("elbruno/Qwen3", "config.json", 100);
        Model("elbruno/Qwen3", "int4/model.onnx", 5_000);
        Model("wavekat/Base", "config.json", 100);

        await New().BackupAsync(Models, Zip, null, default);

        using var archive = ZipFile.OpenRead(Zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("models/elbruno/Qwen3/config.json", names);
        Assert.Contains("models/elbruno/Qwen3/int4/model.onnx", names);
        Assert.Contains("models/wavekat/Base/config.json", names);
    }

    [Fact]
    public async Task It_is_stored_rather_than_compressed()
    {
        // DATA-FORMATS is explicit, and the reason is not tidiness: weights do
        // not compress, so deflating them costs a great deal of CPU for almost
        // nothing — and it breaks the determinate bar, because a compressed
        // archive's size stops tracking the bytes read.
        Model("elbruno/Qwen3", "weights.bin", 200_000);

        await New().BackupAsync(Models, Zip, null, default);

        using var archive = ZipFile.OpenRead(Zip);
        var entry = archive.Entries.Single(e => e.FullName.EndsWith("weights.bin", StringComparison.Ordinal));

        Assert.Equal(entry.Length, entry.CompressedLength);
    }

    [Fact]
    public async Task Half_finished_downloads_are_left_out()
    {
        // An .incomplete file is a download that was interrupted. Carrying it
        // into a backup would restore a model that reads as partial.
        Model("elbruno/Qwen3", "config.json", 100);
        Model("elbruno/Qwen3", "model.onnx.incomplete", 5_000);

        await New().BackupAsync(Models, Zip, null, default);

        using var archive = ZipFile.OpenRead(Zip);

        Assert.DoesNotContain(archive.Entries, e => e.FullName.Contains(".incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_models_folder_says_so_rather_than_writing_nothing()
    {
        Directory.CreateDirectory(Models);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => New().BackupAsync(Models, Zip, null, default));

        Assert.Contains("no models to back up", error.Message);
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public async Task Progress_reaches_the_end()
    {
        Model("elbruno/Qwen3", "weights.bin", 9_000_000);

        var reports = new List<BackupProgress>();
        await New().BackupAsync(Models, Zip, new Progress(reports.Add), default);

        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.All(reports, r => Assert.InRange(r.Fraction, 0, 1));
    }

    // ---- Cancelling ----

    [Fact]
    public async Task A_cancelled_backup_leaves_nothing_behind()
    {
        // Not even at the temporary name. A truncated archive that looks like a
        // backup is the worst outcome here: it is discovered on the machine
        // that needed it.
        Model("elbruno/Qwen3", "weights.bin", 20_000_000);

        using var cancel = new CancellationTokenSource();
        var reports = new Progress(_ => cancel.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => New().BackupAsync(Models, Zip, reports, cancel.Token));

        Assert.False(File.Exists(Zip));
        Assert.False(File.Exists(Zip + ".partial"));
    }

    // ---- Reading one ----

    [Fact]
    public async Task It_says_what_a_backup_holds_without_writing_anything()
    {
        Model("elbruno/Qwen3", "config.json", 100);
        Model("wavekat/Base", "config.json", 250);

        await New().BackupAsync(Models, Zip, null, default);
        var contents = New().Inspect(Zip);

        Assert.Equal(["elbruno/Qwen3", "wavekat/Base"], contents.Repos);
        Assert.Equal(350, contents.Bytes);
    }

    [Fact]
    public void A_zip_that_is_not_a_backup_is_refused_before_anything_is_touched()
    {
        var stray = Path.Combine(_root, "holiday-photos.zip");
        using (var archive = ZipFile.Open(stray, ZipArchiveMode.Create))
        {
            archive.CreateEntry("beach.jpg");
        }

        var error = Assert.Throws<InvalidDataException>(() => New().Inspect(stray));

        Assert.Contains("does not look like a Bunyi backup", error.Message);
    }

    [Fact]
    public async Task A_backup_wrapped_in_a_folder_is_still_read()
    {
        // A zip made by a file manager often wraps everything in a folder named
        // after the archive. Refusing those would reject backups that are fine.
        Model("elbruno/Qwen3", "config.json", 100);
        await New().BackupAsync(Models, Zip, null, default);

        var wrapped = Path.Combine(_root, "wrapped.zip");
        Rewrap(Zip, wrapped, "Bunyi backup/");

        Assert.Equal(["elbruno/Qwen3"], New().Inspect(wrapped).Repos);
    }

    // ---- Restoring ----

    [Fact]
    public async Task A_restore_puts_the_files_back()
    {
        Model("elbruno/Qwen3", "config.json", 100);
        Model("elbruno/Qwen3", "int4/model.onnx", 4_000);
        await New().BackupAsync(Models, Zip, null, default);

        var fresh = Path.Combine(_root, "Fresh");
        await New().RestoreAsync(Zip, fresh, null, default);

        Assert.True(File.Exists(Path.Combine(fresh, "models", "elbruno", "Qwen3", "config.json")));

        var restored = Path.Combine(fresh, "models", "elbruno", "Qwen3", "int4", "model.onnx");
        Assert.Equal(4_000, new FileInfo(restored).Length);
    }

    [Fact]
    public async Task A_model_already_here_is_never_clobbered()
    {
        // §6. The copy on disk has been verified complete; the one in the
        // archive has not been verified since it was made.
        Model("elbruno/Qwen3", "config.json", 100);
        Model("wavekat/Base", "config.json", 100);
        await New().BackupAsync(Models, Zip, null, default);

        var target = Path.Combine(_root, "Target");
        var mine = Path.Combine(target, "models", "elbruno", "Qwen3", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "mine, and newer");

        var skipped = await New().RestoreAsync(Zip, target, null, default);

        Assert.Equal(["elbruno/Qwen3"], skipped);
        Assert.Equal("mine, and newer", File.ReadAllText(mine));

        // The one that was not here did arrive.
        Assert.True(File.Exists(Path.Combine(target, "models", "wavekat", "Base", "config.json")));
    }

    [Fact]
    public async Task A_repo_is_skipped_whole_rather_than_merged_file_by_file()
    {
        // A half-replaced model is worse than either keeping or replacing it.
        Model("elbruno/Qwen3", "config.json", 100);
        Model("elbruno/Qwen3", "extra.bin", 500);
        await New().BackupAsync(Models, Zip, null, default);

        var target = Path.Combine(_root, "Target");
        var existing = Path.Combine(target, "models", "elbruno", "Qwen3", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "mine");

        await New().RestoreAsync(Zip, target, null, default);

        // extra.bin was in the archive and is not written, because its repo was
        // already here.
        Assert.False(File.Exists(Path.Combine(target, "models", "elbruno", "Qwen3", "extra.bin")));
    }

    [Fact]
    public async Task An_empty_folder_left_behind_does_not_count_as_present()
    {
        // Deleting a model can leave the directory. Treating that as "already
        // here" would silently refuse to restore it.
        Model("elbruno/Qwen3", "config.json", 100);
        await New().BackupAsync(Models, Zip, null, default);

        var target = Path.Combine(_root, "Target");
        Directory.CreateDirectory(Path.Combine(target, "models", "elbruno", "Qwen3"));

        var skipped = await New().RestoreAsync(Zip, target, null, default);

        Assert.Empty(skipped);
        Assert.True(File.Exists(Path.Combine(target, "models", "elbruno", "Qwen3", "config.json")));
    }

    [Fact]
    public async Task A_backup_survives_the_round_trip_byte_for_byte()
    {
        Model("elbruno/Qwen3", "weights.bin", 250_000);
        var original = File.ReadAllBytes(Path.Combine(Models, "models", "elbruno", "Qwen3", "weights.bin"));

        await New().BackupAsync(Models, Zip, null, default);

        var fresh = Path.Combine(_root, "Fresh");
        await New().RestoreAsync(Zip, fresh, null, default);

        Assert.Equal(
            original,
            File.ReadAllBytes(Path.Combine(fresh, "models", "elbruno", "Qwen3", "weights.bin")));
    }

    [Fact]
    public void An_entry_that_climbs_out_of_the_folder_is_refused()
    {
        // The archive's paths decide where files land, so they are checked the
        // same way a download manifest's are.
        var nasty = Path.Combine(_root, "nasty.zip");
        using (var archive = ZipFile.Open(nasty, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(
                archive.CreateEntry("models/org/repo/ok.txt").Open());
            writer.Write("fine");
        }

        Assert.Equal(["org/repo"], New().Inspect(nasty).Repos);
        Assert.False(ManifestPathAccepts("../../escaped.txt"));
    }

    private static bool ManifestPathAccepts(string entry) =>
        Bunyi.Core.Models.ManifestPath.TryNormalize(entry, out _);

    // ---- Fixtures ----

    private BackupManager New() => new(_log);

    /// <summary>A file inside a repository, of a given size.</summary>
    private void Model(string repo, string relative, int bytes)
    {
        var path = Path.Combine(
            Models, "models",
            repo.Replace('/', Path.DirectorySeparatorChar),
            relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var content = new byte[bytes];
        for (var i = 0; i < bytes; i++) content[i] = (byte)(i % 251);

        File.WriteAllBytes(path, content);
    }

    /// <summary>Rewrites an archive with every entry under a prefix.</summary>
    private static void Rewrap(string source, string destination, string prefix)
    {
        using var from = ZipFile.OpenRead(source);
        using var to = ZipFile.Open(destination, ZipArchiveMode.Create);

        foreach (var entry in from.Entries)
        {
            var copy = to.CreateEntry(prefix + entry.FullName, CompressionLevel.NoCompression);

            using var input = entry.Open();
            using var output = copy.Open();
            input.CopyTo(output);
        }
    }

    private sealed class Progress(Action<BackupProgress> report) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => report(value);
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Lines { get; } = [];

        public void Log(string message) => Lines.Add(message);
    }
}
