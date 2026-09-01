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
using Bunyi.Core.Engine;
using Bunyi.Core.Models;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Spec §11: can this machine finish a generation right now?
/// </summary>
public sealed class DoctorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private string Outputs => Path.Combine(_root, "Outputs");

    private static ModelLayout Layout { get; } = new(
        "test",
        [
            new ModelFile("embeddings/config.json", Required: true),
            new ModelFile("model.onnx", Required: true),
        ],
        ApproxDownloadBytes: 6_000_000_000);

    private static ModelSource Source { get; } = new ModelSource.Repo("org/repo");

    public DoctorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void InstallModel()
    {
        var folder = ModelDownloader.FolderFor(Source, _root);
        Directory.CreateDirectory(Path.Combine(folder, "embeddings"));
        File.WriteAllBytes(Path.Combine(folder, "embeddings", "config.json"), new byte[64]);
        File.WriteAllBytes(Path.Combine(folder, "model.onnx"), new byte[4096]);
    }

    private Task<DoctorReport> Run(
        long? memory = 32_000_000_000,
        long? disk = 500_000_000_000,
        bool reachable = true,
        bool deep = false,
        ExecutionProviderChoice? provider = ExecutionProviderChoice.Cpu,
        bool? nvidia = null,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? verify = null) =>
        Doctor.RunAsync(
            TtsMode.PresetVoice, Source, Layout, _root, Outputs,
            new FakeProbe(memory, disk, nvidia),
            (_, _) => Task.FromResult(reachable),
            verify,
            deep,
            provider);

    private static DoctorFinding Finding(DoctorReport report, string title) =>
        report.Findings.Single(f => f.Title == title);

    [Fact]
    public async Task A_healthy_machine_reports_nothing_wrong()
    {
        // §11: "a preflight the user notices on a healthy machine is a bug".
        InstallModel();

        var report = await Run();

        Assert.False(report.HasBlockers);
        Assert.Empty(report.Warnings);
        Assert.All(report.Findings, f => Assert.Equal(DoctorSeverity.Ok, f.Severity));
    }

    [Fact]
    public async Task A_missing_model_is_not_itself_a_failure()
    {
        // §11 is explicit: a generation downloads it. It changes what the other
        // checks are about, not whether the run can proceed.
        var report = await Run();

        Assert.Equal(DoctorSeverity.Ok, Finding(report, "Model").Severity);
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public async Task Not_enough_room_for_the_download_stops_the_run()
    {
        var report = await Run(disk: 1_000_000_000);   // 1 GB against a 6 GB model

        var disk = Finding(report, "Disk space");
        Assert.Equal(DoctorSeverity.Blocker, disk.Severity);
        Assert.True(report.HasBlockers);
    }

    [Fact]
    public async Task A_disk_that_only_just_fits_warns_rather_than_stopping()
    {
        var report = await Run(disk: 8_000_000_000);   // 6 GB model, under 5 GB spare

        Assert.Equal(DoctorSeverity.Warning, Finding(report, "Disk space").Severity);
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public async Task A_disk_finding_says_how_much_to_free()
    {
        // §10: findings are actionable, and §11 says sizes are stated.
        var report = await Run(disk: 1_000_000_000);

        var detail = Finding(report, "Disk space").Detail;
        Assert.Contains("Free up", detail);
        Assert.Contains("GB", detail);
    }

    [Fact]
    public async Task With_the_model_present_only_the_output_needs_room()
    {
        // Not the model's size again — it is already on disk.
        InstallModel();

        var report = await Run(disk: 2_000_000_000);   // far less than the 6 GB model

        Assert.Equal(DoctorSeverity.Ok, Finding(report, "Disk space").Severity);
    }

    [Fact]
    public async Task A_disk_at_the_very_edge_blocks_even_with_the_model_present()
    {
        InstallModel();

        var report = await Run(disk: 1_000_000);   // 1 MB

        Assert.Equal(DoctorSeverity.Blocker, Finding(report, "Disk space").Severity);
    }

    [Fact]
    public async Task Low_memory_warns_and_never_blocks()
    {
        // §11: "Warning only, never a blocker" — it is a prediction about a run
        // that has not started, the figure moves the moment another app quits,
        // and a machine under pressure still finishes, only slowly. Blocking
        // would refuse runs that would have worked.
        var report = await Run(memory: 1_000_000_000);

        Assert.Equal(DoctorSeverity.Warning, Finding(report, "Memory").Severity);
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public async Task The_memory_finding_says_what_would_help()
    {
        var report = await Run(memory: 1_000_000_000);

        var detail = Finding(report, "Memory").Detail;
        Assert.Contains("still run", detail);
        Assert.Contains("Closing other apps", detail);
    }

    [Fact]
    public async Task An_unknown_memory_figure_warns_rather_than_guessing()
    {
        var report = await Run(memory: null);

        Assert.Equal(DoctorSeverity.Warning, Finding(report, "Memory").Severity);
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public async Task A_dead_source_is_reported_as_a_dead_source()
    {
        // §11: "so a dead self-hosted server is reported as a dead server
        // rather than as a missing model".
        var report = await Run(reachable: false);

        var source = Finding(report, "Model source");
        Assert.Equal(DoctorSeverity.Blocker, source.Severity);
        Assert.Contains("not answering", source.Detail);
        Assert.Equal(DoctorSeverity.Ok, Finding(report, "Model").Severity);
    }

    [Fact]
    public async Task The_source_is_not_asked_about_when_the_model_is_already_here()
    {
        // The app is offline-capable once the files are there, so reporting an
        // unreachable server would be reporting a problem the user does not
        // have.
        InstallModel();

        var report = await Run(reachable: false);

        Assert.DoesNotContain(report.Findings, f => f.Title == "Model source");
        Assert.False(report.HasBlockers);
    }

    [Fact]
    public async Task An_unwritable_output_folder_stops_the_run()
    {
        var report = await Doctor.RunAsync(
            TtsMode.PresetVoice, Source, Layout, _root,
            // A path under a file cannot be created as a folder.
            Path.Combine(CreateFile(), "Outputs"),
            new FakeProbe(32_000_000_000, 500_000_000_000),
            (_, _) => Task.FromResult(true));

        Assert.Equal(DoctorSeverity.Blocker, Finding(report, "Output folder").Severity);
    }

    private string CreateFile()
    {
        var path = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public async Task Integrity_is_not_checked_before_a_generation()
    {
        // §11: hashing gigabytes before every run would be a worse problem than
        // the one it detects.
        InstallModel();
        var asked = false;

        var report = await Run(deep: false, verify: (_, _) =>
        {
            asked = true;
            return Task.FromResult<IReadOnlyList<string>>([]);
        });

        Assert.False(asked);
        Assert.DoesNotContain(report.Findings, f => f.Title == "Model files");
    }

    [Fact]
    public async Task Integrity_is_checked_on_demand()
    {
        InstallModel();

        var report = await Run(deep: true, verify: (_, _) =>
            Task.FromResult<IReadOnlyList<string>>([]));

        Assert.Equal(DoctorSeverity.Ok, Finding(report, "Model files").Severity);
    }

    [Fact]
    public async Task A_file_that_fails_its_checksum_stops_the_run()
    {
        // The failure that otherwise loads and speaks nonsense.
        InstallModel();

        var report = await Run(deep: true, verify: (_, _) =>
            Task.FromResult<IReadOnlyList<string>>(["model.onnx"]));

        var files = Finding(report, "Model files");
        Assert.Equal(DoctorSeverity.Blocker, files.Severity);
        Assert.Contains("model.onnx", files.Detail);
        Assert.Contains("download it again", files.Detail);
    }

    [Fact]
    public async Task Every_report_names_the_mode_it_is_about()
    {
        // §11: the checks are per-mode — three modes, three models, three
        // sizes, three separately configured sources — so "the model" is
        // ambiguous unless it is named. It matters most from History, which is
        // not a mode at all.
        var report = await Doctor.RunAsync(
            TtsMode.VoiceClone, Source, Layout, _root, Outputs,
            new FakeProbe(32_000_000_000, 500_000_000_000),
            (_, _) => Task.FromResult(true));

        Assert.Equal(TtsMode.VoiceClone, report.Mode);
        Assert.Contains("Voice clone", report.Describe());
    }

    [Fact]
    public async Task The_written_report_marks_what_would_stop_a_run()
    {
        var report = await Run(disk: 1_000_000_000, memory: 1_000_000_000);

        var text = report.Describe();
        Assert.Contains("[STOP]", text);
        Assert.Contains("[note]", text);
        Assert.Contains("[ok]", text);
    }

    [Fact]
    public void The_source_probe_uses_the_exports_own_first_required_file()
    {
        // A hardcoded config.json probe would report the real preset-voice
        // export as dead, because it keeps its config under embeddings/.
        var probe = Doctor.ProbeUriFor(Source, Layout);

        Assert.EndsWith("embeddings/config.json", probe.AbsoluteUri);
        Assert.DoesNotContain("/config.json", probe.AbsoluteUri.Replace("embeddings/config.json", ""));
    }

    [Fact]
    public void The_source_probe_for_a_self_hosted_server_hangs_off_its_base()
    {
        var probe = Doctor.ProbeUriFor(
            new ModelSource.BaseUrl(new Uri("https://models.example.com/customvoice")), Layout);

        Assert.Equal(
            "https://models.example.com/customvoice/embeddings/config.json", probe.AbsoluteUri);
    }

    [Fact]
    public void The_memory_factor_reflects_what_was_measured()
    {
        // 5.88 GB on disk peaked at 8.73 GB resident, which is 1.48x. macOS
        // uses 1.3 for MLX; this runtime holds prefill and decode as separate
        // sessions over the same weights, so it needs more.
        Assert.True(Doctor.MemoryWorkingFactor >= 1.45,
            "a factor below what was measured would under-predict and never warn");
    }

    [Fact]
    public async Task It_names_the_provider_the_talker_will_use()
    {
        // #143: an invisible choice cannot be debugged from a bug report, and a
        // speedup nobody is told about is one nobody uses.
        InstallModel();

        var report = await Run(provider: ExecutionProviderChoice.Cuda);

        var finding = Finding(report, "Acceleration");
        // CUDA, not Cuda: the enum spells it for C#, the finding for a person.
        Assert.Contains("CUDA", finding.Detail, StringComparison.Ordinal);
        // That the last step stays on the CPU is stated wherever the provider
        // is, because "on the GPU" would otherwise read as all of it. Asserted
        // on "sound" rather than "vocoder": the finding is user-facing and
        // deliberately does not use the model's internal vocabulary.
        Assert.Contains("sound", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vocoder", finding.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("talker", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_says_what_is_missing_when_a_card_is_present_and_unused()
    {
        // §11 requires a finding to say what would resolve it. "You are on the
        // CPU" on a machine with a 4090 in it is a state, not an answer.
        InstallModel();

        var report = await Run(provider: ExecutionProviderChoice.Cpu, nvidia: true);

        var finding = Finding(report, "Acceleration");
        Assert.Contains("CUDA Toolkit", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_on_the_CPU_without_a_card_is_not_a_complaint()
    {
        // Telling someone with no NVIDIA card what they are missing is telling
        // them to buy hardware, so that branch says nothing about the toolkit.
        InstallModel();

        var report = await Run(provider: ExecutionProviderChoice.Cpu, nvidia: false);

        var finding = Finding(report, "Acceleration");
        Assert.DoesNotContain("Toolkit", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(DoctorSeverity.Ok, finding.Severity);
    }

    [Fact]
    public async Task The_provider_row_never_warns()
    {
        // Doctor runs before EVERY generation, and §11: "a preflight the user
        // notices on a healthy machine is a bug". A machine correctly using its
        // CPU is healthy, however much faster it could be.
        InstallModel();

        foreach (var (p, card) in new (ExecutionProviderChoice, bool?)[]
                 {
                     (ExecutionProviderChoice.Cpu, true),
                     (ExecutionProviderChoice.Cpu, false),
                     (ExecutionProviderChoice.Cuda, true),
                 })
        {
            var finding = Finding(await Run(provider: p, nvidia: card), "Acceleration");
            Assert.Equal(DoctorSeverity.Ok, finding.Severity);
        }
    }

    private sealed class FakeProbe(long? memory, long? disk, bool? nvidia = null) : ISystemProbe
    {
        public long? AvailableMemoryBytes() => memory;
        public long? FreeSpaceBytes(string path) => disk;
        public bool? HasNvidiaDriver() => nvidia;
    }
}
