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

using System.Text;
using Bunyi.Core.Models;

using Bunyi.Core.Engine;

namespace Bunyi.Core.Diagnostics;

/// <summary>How bad a finding is (spec §11).</summary>
public enum DoctorSeverity
{
    Ok,
    Warning,
    Blocker,
}

/// <summary>One thing Doctor looked at.</summary>
/// <remarks>
/// Every finding names what is wrong <b>and what would resolve it</b> — §11 is
/// explicit that Doctor fixes nothing, so a finding that only reports a state
/// leaves the user with nothing to do.
/// </remarks>
public sealed record DoctorFinding(string Title, string Detail, DoctorSeverity Severity);

/// <summary>Everything Doctor found, for one mode.</summary>
public sealed record DoctorReport(TtsMode Mode, IReadOnlyList<DoctorFinding> Findings)
{
    /// <summary>Whether anything would stop a run.</summary>
    public bool HasBlockers => Findings.Any(f => f.Severity == DoctorSeverity.Blocker);

    /// <summary>Findings that would stop a run.</summary>
    public IEnumerable<DoctorFinding> Blockers =>
        Findings.Where(f => f.Severity == DoctorSeverity.Blocker);

    /// <summary>Findings worth saying but not worth stopping for.</summary>
    public IEnumerable<DoctorFinding> Warnings =>
        Findings.Where(f => f.Severity == DoctorSeverity.Warning);

    /// <summary>
    /// The report as text, for the Logs and the clipboard.
    /// </summary>
    /// <remarks>
    /// It names the mode. §11: the checks are per-mode — three modes, three
    /// models, three sizes, three separately configured sources — so "the
    /// model" is ambiguous unless it is said which. It matters most when
    /// there is no mode on screen at all, which is the case from History.
    /// </remarks>
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"Doctor — {Mode.DisplayName()}");

        foreach (var finding in Findings)
        {
            var mark = finding.Severity switch
            {
                DoctorSeverity.Blocker => "STOP",
                DoctorSeverity.Warning => "note",
                _ => "ok",
            };
            text.AppendLine($"  [{mark}] {finding.Title}: {finding.Detail}");
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// Can this machine finish a generation right now? (spec §11)
/// </summary>
/// <remarks>
/// <para>
/// Not a settings panel, and it fixes nothing. It runs before every generation
/// — <b>before any download begins</b>, because the point is not to discover
/// after 3.4 GB that there was never room for it — and on demand from the
/// window.
/// </para>
/// <para>
/// A report is a value rather than carried state, so it is a static function
/// over what it was given. Nothing here mutates anything.
/// </para>
/// </remarks>
public static class Doctor
{
    /// <summary>
    /// How much larger than its files a model is once resident.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed: the 0.6B preset export is 5.88 GB on disk and
    /// peaked at 8.73 GB resident, which is 1.48x. macOS uses 1.3 for MLX; this
    /// runtime holds the talker's prefill and decode graphs as separate
    /// sessions over the same weights, so it needs more. See RESEARCH-ONNX.md.
    /// </remarks>
    public const double MemoryWorkingFactor = 1.5;

    /// <summary>Headroom wanted beyond the download itself, before warning.</summary>
    public static readonly long DiskWarningHeadroom = 5L * 1000 * 1000 * 1000;

    /// <summary>
    /// Space a generation's own output needs, when nothing is downloading.
    /// </summary>
    /// <remarks>
    /// Clips are megabytes, so this is about not writing onto a disk already at
    /// the edge rather than about the file itself.
    /// </remarks>
    public static readonly long OutputHeadroom = 500L * 1000 * 1000;

    /// <summary>Runs the checks.</summary>
    /// <param name="deep">
    /// Also verify files against the digests the server published. Hashing
    /// gigabytes takes real time, so this is for the on-demand run only — never
    /// the one before a generation.
    /// </param>
    public static async Task<DoctorReport> RunAsync(
        TtsMode mode,
        ModelSource source,
        ModelLayout layout,
        string modelsRoot,
        string outputFolder,
        ISystemProbe probe,
        Func<Uri, CancellationToken, Task<bool>> reachable,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? verifyFiles = null,
        bool deep = false,
        ExecutionProviderChoice? provider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(reachable);

        var findings = new List<DoctorFinding>();
        var folder = ModelDownloader.FolderFor(source, modelsRoot);
        var state = ModelDownloader.Inspect(folder, layout);
        var isPresent = state.IsComplete;

        findings.Add(ModelFinding(mode, isPresent, state));

        // When the model is missing the next checks are about the download,
        // not about files on disk (§11).
        var modelBytes = isPresent ? SizeOf(folder) : layout.ApproxDownloadBytes;

        findings.Add(DiskFinding(modelsRoot, !isPresent, modelBytes, probe));
        findings.Add(MemoryFinding(mode, modelBytes, isPresent, probe));

        if (!isPresent)
        {
            findings.Add(await SourceFinding(source, layout, reachable, ct).ConfigureAwait(false));
        }

        findings.Add(ProviderFinding(provider ?? OnnxRuntimeEnv.Current, probe));
        findings.Add(OutputFinding(outputFolder));

        if (deep && verifyFiles is not null && isPresent)
        {
            findings.Add(await IntegrityFinding(folder, verifyFiles, ct).ConfigureAwait(false));
        }

        return new DoctorReport(mode, findings);
    }

    /// <summary>
    /// Check 1. A missing model is not itself a failure — a generation
    /// downloads it.
    /// </summary>
    private static DoctorFinding ModelFinding(TtsMode mode, bool isPresent, ModelCompleteness state)
    {
        if (isPresent)
        {
            return new DoctorFinding(
                "Model", $"The {mode.DisplayName().ToLowerInvariant()} model is downloaded and ready.",
                DoctorSeverity.Ok);
        }

        var detail = state.Missing.Count == 0 && state.Partial.Count == 0
            ? "It has not been downloaded yet. It will download the first time you generate."
            : $"It is not complete ({state.Describe()}). The missing parts will download "
              + "the first time you generate.";

        return new DoctorFinding("Model", detail, DoctorSeverity.Ok);
    }

    /// <summary>Check 2. Measured on the volume holding the models folder (§3d).</summary>
    private static DoctorFinding DiskFinding(
        string modelsRoot, bool needsDownload, long modelBytes, ISystemProbe probe)
    {
        var free = probe.FreeSpaceBytes(modelsRoot);
        if (free is null)
        {
            return new DoctorFinding(
                "Disk space", "Could not work out how much room is free where models are kept.",
                DoctorSeverity.Warning);
        }

        var freeText = DownloadProgress.Bytes(free.Value);

        if (!needsDownload)
        {
            if (free.Value < OutputHeadroom)
            {
                return new DoctorFinding(
                    "Disk space",
                    $"Only {freeText} free. Generated audio is small, but a disk this full "
                    + "may not accept it. Free up some space.",
                    DoctorSeverity.Blocker);
            }

            return new DoctorFinding("Disk space", $"{freeText} free.", DoctorSeverity.Ok);
        }

        var needText = DownloadProgress.Bytes(modelBytes);

        if (free.Value < modelBytes)
        {
            var shortfall = DownloadProgress.Bytes(modelBytes - free.Value);
            return new DoctorFinding(
                "Disk space",
                $"{freeText} free, and the download needs about {needText}. "
                + $"Free up {shortfall}, or keep models on another drive in Settings.",
                DoctorSeverity.Blocker);
        }

        if (free.Value < modelBytes + DiskWarningHeadroom)
        {
            return new DoctorFinding(
                "Disk space",
                $"{freeText} free and the download needs about {needText}, which fits "
                + "but leaves the disk nearly full.",
                DoctorSeverity.Warning);
        }

        return new DoctorFinding(
            "Disk space", $"{freeText} free, and the download needs about {needText}.",
            DoctorSeverity.Ok);
    }

    /// <summary>
    /// Check 3. Warning only, never a blocker.
    /// </summary>
    /// <remarks>
    /// §11: it is a prediction about a run that has not started, the figure
    /// moves the moment another app quits, and a machine under pressure still
    /// finishes — only slowly. Blocking on it would refuse runs that would have
    /// worked.
    /// </remarks>
    private static DoctorFinding MemoryFinding(
        TtsMode mode, long modelBytes, bool isPresent, ISystemProbe probe)
    {
        var available = probe.AvailableMemoryBytes();
        if (available is null)
        {
            return new DoctorFinding(
                "Memory", "Could not work out how much memory is free.", DoctorSeverity.Warning);
        }

        var needed = (long)(modelBytes * MemoryWorkingFactor);
        var neededText = DownloadProgress.Bytes(needed);
        var availableText = DownloadProgress.Bytes(available.Value);
        var once = isPresent ? string.Empty : " once it has downloaded";

        if (available.Value >= needed)
        {
            return new DoctorFinding(
                "Memory",
                $"{availableText} available, and the {mode.DisplayName().ToLowerInvariant()} "
                + $"model wants about {neededText}.",
                DoctorSeverity.Ok);
        }

        return new DoctorFinding(
            "Memory",
            $"{availableText} available and the {mode.DisplayName().ToLowerInvariant()} model "
            + $"wants about {neededText}{once}. It will still run, but the machine may swap, "
            + "which makes generating slow and playback stutter. Long text needs more again. "
            + "Closing other apps first will help.",
            DoctorSeverity.Warning);
    }

    /// <summary>
    /// Check 4. Only when a download is required.
    /// </summary>
    /// <remarks>
    /// §11: a dead self-hosted server should be reported as a dead server
    /// rather than as a missing model. Asking when the files are already on
    /// disk would report a problem the user does not have — the app is
    /// offline-capable once they are there.
    /// </remarks>
    private static async Task<DoctorFinding> SourceFinding(
        ModelSource source,
        ModelLayout layout,
        Func<Uri, CancellationToken, Task<bool>> reachable,
        CancellationToken ct)
    {
        var probe = ProbeUriFor(source, layout);
        var where = source switch
        {
            ModelSource.Repo repo => repo.Id,
            ModelSource.BaseUrl url => url.Url.AbsoluteUri,
            _ => "the configured source",
        };

        var ok = await reachable(probe, ct).ConfigureAwait(false);

        return ok
            ? new DoctorFinding("Model source", $"{where} is answering.", DoctorSeverity.Ok)
            : new DoctorFinding(
                "Model source",
                $"{where} is not answering, so the model cannot be downloaded. "
                + "Check your connection, and the address in Settings.",
                DoctorSeverity.Blocker);
    }

    /// <summary>
    /// A URL that proves the source is alive.
    /// </summary>
    /// <remarks>
    /// Built from the export's own required-file list rather than assuming
    /// <c>config.json</c> at the top level: the preset-voice export keeps its
    /// config under <c>embeddings/</c>, so a hardcoded probe would report a
    /// perfectly good server as dead.
    /// </remarks>
    internal static Uri ProbeUriFor(ModelSource source, ModelLayout layout)
    {
        var first = layout.RequiredFiles.FirstOrDefault()?.RelativePath
                    ?? layout.Files.FirstOrDefault()?.RelativePath
                    ?? "config.json";

        return source switch
        {
            ModelSource.Repo repo => new Uri($"https://huggingface.co/{repo.Id}/resolve/main/{first}"),
            ModelSource.BaseUrl url => new Uri(
                url.Url.AbsoluteUri.TrimEnd('/') + "/" + first),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    /// <summary>
    /// Check 5a. Which provider the speech model will use, and what is missing
    /// when it is not the fast one.
    /// </summary>
    /// <remarks>
    /// <b>Never a warning</b>, even on a machine that could be 3.7x faster.
    /// Doctor runs before every generation, and §11 is explicit that "a
    /// preflight the user notices on a healthy machine is a bug" — a machine
    /// running correctly on the CPU is healthy. It is a row in the report
    /// someone opens, which is where a speedup they did not know about is
    /// findable without being nagged about it on every run.
    /// </remarks>
    private static DoctorFinding ProviderFinding(
        ExecutionProviderChoice provider,
        ISystemProbe probe)
    {
        if (provider != ExecutionProviderChoice.Cpu)
        {
            return new DoctorFinding(
                "Acceleration",
                $"The speech model runs on {provider.Label()}. The final step, which turns "
                + "the result into sound, always runs on the CPU.",
                DoctorSeverity.Ok);
        }

        // "CPU on a machine with an NVIDIA card" is the case worth explaining.
        // "CPU on a machine without one" is simply the answer, and saying what
        // is missing there would be telling someone to buy hardware.
        return probe.HasNvidiaDriver() == true
            ? new DoctorFinding(
                "Acceleration",
                "The speech model runs on the CPU, though this machine has an NVIDIA "
                + "graphics card. CUDA measured 3.7x faster on long text. It needs the "
                + "CUDA build of Bunyi and NVIDIA's CUDA Toolkit.",
                DoctorSeverity.Ok)
            : new DoctorFinding(
                "Acceleration",
                "The speech model runs on the CPU, which is the supported configuration here.",
                DoctorSeverity.Ok);
    }

    /// <summary>Check 5. Blocker: there is nowhere to put the result.</summary>
    private static DoctorFinding OutputFinding(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            // Actually write, rather than inferring from attributes: a folder
            // can look writable and refuse, on a full disk or a read-only
            // mount.
            var probe = Path.Combine(folder, $".bunyi-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);

            return new DoctorFinding("Output folder", $"Writable: {folder}", DoctorSeverity.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DoctorFinding(
                "Output folder",
                $"Cannot write to {folder}, so there is nowhere to save the audio. {ex.Message}",
                DoctorSeverity.Blocker);
        }
    }

    /// <summary>
    /// Check 6. On demand only.
    /// </summary>
    /// <remarks>
    /// §11: hashing gigabytes before every generation would be a worse problem
    /// than the one it detects. This is the check that catches a truncated or
    /// half-synced model — the failure that otherwise loads and speaks
    /// nonsense.
    /// </remarks>
    private static async Task<DoctorFinding> IntegrityFinding(
        string folder,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> verifyFiles,
        CancellationToken ct)
    {
        try
        {
            var bad = await verifyFiles(folder, ct).ConfigureAwait(false);

            if (bad.Count == 0)
            {
                return new DoctorFinding(
                    "Model files", "Every file matches the checksums your server published.",
                    DoctorSeverity.Ok);
            }

            return new DoctorFinding(
                "Model files",
                $"{bad.Count} file(s) do not match the checksums your server published "
                + $"({string.Join(", ", bad.Take(3))}). Delete the model in Settings and "
                + "download it again.",
                DoctorSeverity.Blocker);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return new DoctorFinding(
                "Model files", $"Could not check the files against their checksums. {ex.Message}",
                DoctorSeverity.Warning);
        }
    }

    private static long SizeOf(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
