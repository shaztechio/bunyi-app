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
using System.Security.Cryptography;
using Bunyi.Core;
using Bunyi.Core.Infrastructure;
using Bunyi.Core.Models;

namespace Bunyi.MirrorManifest;

/// <summary>
/// Prepares a Bunyi mirror from ONNX exports already on this machine (#100).
/// </summary>
/// <remarks>
/// <para>
/// <c>SELF-HOSTING.md</c> builds each manifest with <c>find</c>, over a folder
/// fetched fresh from Hugging Face. That works, and it carries a failure the
/// runbook names in as many words: <i>"a folder that is only mostly right
/// produces a manifest that is confidently wrong."</i> A `find` cannot tell the
/// difference between a file the app needs, a file it will never ask for, and a
/// file that should have been there and is not.
/// </para>
/// <para>
/// This asks <see cref="ModelLayout"/> instead — the app's own statement of
/// which files each mode fetches. A required file that is missing stops the run
/// with its name; a file on disk that no mode wants is reported and left out.
/// The manifest that comes out is what the client will ask for, by
/// construction, which is the check #100 wanted running on a schedule.
/// </para>
/// <para>
/// It copies nothing. Hashing 14 GB is unavoidable; duplicating it is not, so
/// the output is a manifest plus an <c>rclone --files-from</c> list that
/// uploads out of the models folder where the app already put them.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>Where each mode is published, settled in #100's comments.</summary>
    private static readonly (TtsMode Mode, string Prefix, string Repo)[] Mirror =
    [
        (TtsMode.PresetVoice, "onnx/customvoice", "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX"),
        (TtsMode.VoiceDesign, "onnx/voicedesign", "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX"),
        (TtsMode.VoiceClone, "onnx/voiceclone", "wavekat/Qwen3-TTS-0.6B-Base-ONNX"),
    ];

    private static int Main(string[] args)
    {
        var models = Argument(args, "--models") ?? Path.Combine(AppPaths.DefaultModelsFolder, "models");
        var output = Argument(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "mirror");

        if (!Directory.Exists(models))
        {
            Console.Error.WriteLine(
                $"No models folder at {models}.\n"
                + "Pass --models <folder> — the one holding <org>/<repo> subfolders. "
                + "Settings → Storage names it if it has been moved.");
            return 2;
        }

        Console.WriteLine($"Reading   {models}");
        Console.WriteLine($"Writing   {output}\n");

        var failed = false;
        var grandTotal = 0L;

        foreach (var (mode, prefix, repo) in Mirror)
        {
            var source = Path.Combine(models, repo.Replace('/', Path.DirectorySeparatorChar));
            Console.WriteLine($"{mode.DisplayName()}  ->  {prefix}");

            if (!Directory.Exists(source))
            {
                Console.WriteLine($"  SKIP  not downloaded here ({repo})");
                Console.WriteLine("        Generate once in this mode, or leave it off the mirror —");
                Console.WriteLine("        a blank Settings field keeps using Hugging Face.\n");
                continue;
            }

            if (!Publish(mode, source, Path.Combine(output, prefix), out var bytes)) failed = true;
            grandTotal += bytes;
            Console.WriteLine();
        }

        if (failed)
        {
            Console.Error.WriteLine("Nothing was written for at least one mode. Fix the above and run again.");
            return 1;
        }

        Console.WriteLine($"Total to upload: {Size(grandTotal)}\n");
        Upload(models, output, Argument(args, "--bucket") ?? "r2:bunyi-models");
        return 0;
    }

    /// <summary>Writes one prefix's manifest and upload list.</summary>
    private static bool Publish(TtsMode mode, string source, string destination, out long total)
    {
        total = 0;
        var layout = ModelLayout.For(mode);

        // Required first, and separately: a mirror missing one of these looks
        // finished and fails at load, which is the failure the whole
        // completeness rule exists to prevent.
        var missing = layout.RequiredFiles
            .Where(f => !File.Exists(Path.Combine(source, Native(f.RelativePath))))
            .Select(f => f.RelativePath)
            .ToList();

        if (missing.Count > 0)
        {
            Console.WriteLine($"  FAIL  {missing.Count} required file(s) are not on disk:");
            foreach (var path in missing.Take(8)) Console.WriteLine($"          {path}");
            if (missing.Count > 8) Console.WriteLine($"          … and {missing.Count - 8} more");
            return false;
        }

        var present = layout.Files
            .Select(f => f.RelativePath)
            .Where(p => File.Exists(Path.Combine(source, Native(p))))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Directory.CreateDirectory(destination);

        var manifest = new List<string>();
        var clock = Stopwatch.StartNew();

        foreach (var (path, index) in present.Select((p, i) => (p, i)))
        {
            var file = Path.Combine(source, Native(path));
            total += new FileInfo(file).Length;
            manifest.Add($"{Sha256(file)}  {path}");

            // Only when someone is watching. Redirected, a carriage return is
            // not a redraw but another line of the same message, and the log
            // fills with one per file.
            if (!Console.IsOutputRedirected)
                Console.Write($"\r  hashing  {index + 1}/{present.Count}   ");
        }

        if (!Console.IsOutputRedirected) Console.Write("\r                                        \r");

        // The format DATA-FORMATS.md pins and ManifestParser reads: a 64-char
        // digest, whitespace, the path. Same file shape as `shasum -a 256`.
        File.WriteAllLines(Path.Combine(destination, "manifest.sha256"), manifest);

        // What rclone should upload, so the files nobody asked for stay behind
        // without anyone having to remember an --exclude. On this machine that
        // is the clone export's validation/ samples and its generator script.
        File.WriteAllLines(Path.Combine(destination, "files.txt"), present);

        var onDisk = Directory
            .EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(source, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(p => !p.StartsWith('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extra = onDisk.Except(present, StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine($"  OK    {present.Count} files, {Size(total)}, hashed in {clock.Elapsed.TotalSeconds:0}s");
        if (extra.Count > 0)
        {
            Console.WriteLine($"        {extra.Count} file(s) on disk that no mode asks for, left out:");
            foreach (var path in extra.Take(5)) Console.WriteLine($"          {path}");
            if (extra.Count > 5) Console.WriteLine($"          … and {extra.Count - 5} more");
        }

        return true;
    }

    /// <summary>
    /// The two commands per prefix, with the real paths in them.
    /// </summary>
    /// <remarks>
    /// Written to be copied and run, so the paths are this machine's and the
    /// separators are forward slashes throughout — rclone takes them on Windows,
    /// and a line mixing both is a line somebody has to repair before it works.
    /// </remarks>
    private static void Upload(string models, string output, string bucket)
    {
        Console.WriteLine("Then, once `rclone config` has a remote for the bucket (SELF-HOSTING.md step 4).");
        Console.WriteLine($"Change {bucket} with --bucket if your remote or bucket is named differently:\n");

        foreach (var (_, prefix, repo) in Mirror)
        {
            var source = Path.Combine(models, repo.Replace('/', Path.DirectorySeparatorChar));
            Console.WriteLine($"  rclone copy \"{Slash(source)}\" {bucket}/{prefix} \\");
            Console.WriteLine($"      --files-from \"{Slash(Path.Combine(output, prefix, "files.txt"))}\" --progress");
            Console.WriteLine($"  rclone copy \"{Slash(Path.Combine(output, prefix, "manifest.sha256"))}\" {bucket}/{prefix}");
            Console.WriteLine();
        }

        Console.WriteLine("Then SELF-HOSTING.md steps 9 and 11: make the bucket readable over HTTPS,");
        Console.WriteLine("and verify by pointing Settings at each URL before anyone else does.");
    }

    // ---- Plumbing ----

    private static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>One separator, so a printed command can be pasted as it stands.</summary>
    private static string Slash(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string Native(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0.0} MB";

    private static string? Argument(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
