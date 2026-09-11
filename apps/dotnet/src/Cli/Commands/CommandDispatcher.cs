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

using System.Reflection;
using Bunyi.Cli.Protocol;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Infrastructure;
using Bunyi.Core.Models;
using Bunyi.Core.Platform;
using Bunyi.Core.Runtime;

namespace Bunyi.Cli.Commands;

/// <summary>The one command implementation used by disposable clients and the resident server.</summary>
public sealed class CommandDispatcher(ILogSink log)
{
    public async Task<Dictionary<string, object?>> ExecuteAsync(CommandRequest request, BunyiRuntime runtime,
        Action<Dictionary<string, object?>> emit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        emit = new ProgressEmitter(emit).Report;
        var operation = request.Operation;
        var mode = request.Has("mode") ? CommandParser.Mode(request.Get("mode")) : TtsMode.PresetVoice;
        var downloadProgress = new InlineProgress<AggregateDownloadProgress>(p => emit(CliProtocol.Event(request, p.Phase switch
        {
            DownloadPhase.Manifest => "resolving", DownloadPhase.Done => "verifying", _ => p.Phase.ToString().ToLowerInvariant()
        }, ("item", new { id = p.ItemId, index = p.ItemIndex, count = p.ItemCount }),
            ("bytesCompleted", p.BytesCompleted), ("bytesTotal", p.BytesTotal), ("rateBytesPerSecond", p.RateBytesPerSecond),
            ("etaSeconds", p.EtaSeconds), ("currentFile", p.CurrentFile),
            ("itemBytesCompleted", p.ItemBytesCompleted), ("itemBytesTotal", p.ItemBytesTotal),
            ("retryAt", p.Download?.ServiceWait?.RetryAt), ("retryAfterSeconds", p.Download?.ServiceWait?.SecondsRemaining),
            ("retryAttempt", p.Download?.ServiceWait?.Attempt), ("host", p.Download?.ServiceWait?.Host),
            ("detail", p.Download?.ServiceWait is null ? null : p.Download.Human()))));
        var engineProgress = new InlineProgress<EngineStatus>(status =>
        {
            if (status.State is EngineState.Idle or EngineState.Error) return;
            if (status.Download is { ServiceWait: { } wait } download)
            {
                emit(CliProtocol.Event(request, "waiting", ("retryAt", wait.RetryAt),
                    ("retryAfterSeconds", wait.SecondsRemaining), ("retryAttempt", wait.Attempt), ("host", wait.Host),
                    ("bytesCompleted", download.BytesReceived + download.BytesReused), ("bytesTotal", download.BytesTotal),
                    ("currentFile", download.CurrentFile), ("detail", download.Human())));
                return;
            }
            emit(CliProtocol.Event(request, status.State.ToString().ToLowerInvariant(),
                ("frames", status.Frames), ("audioSeconds", status.Frames / 12.5), ("detail", status.Detail)));
        });
        if (operation.StartsWith("generate.", StringComparison.Ordinal))
        {
            mode = CommandParser.Mode(operation.Split('.')[1]);
            var reference = request.Get("reference");
            var transcript = request.Get("transcript");
            if (request.Has("saved-voice"))
            {
                var library = Voices();
                var voice = FindVoice(library, request.Get("saved-voice")!);
                reference = CommandParser.ReadableFile(library.ClipPath(voice));
                transcript = voice.Transcript;
            }
            if (request.Has("auto-transcribe"))
            {
                emit(CliProtocol.Event(request, "transcribing"));
                transcript = await runtime.TranscribeAsync(reference!, request.Get("language") ?? "auto", downloadProgress, ct);
            }
            var generation = new GenerateRequest(mode, request.Get("text")!, request.Get("language") ?? Languages.Default,
                request.Get("speaker") ?? FallbackSpeakers.Default, request.Get("style") ?? request.Get("voice"), reference, transcript);
            if (GenerationReadiness.Missing(generation) is { } missing) throw CommandParser.Missing(missing.Reason);
            var result = await runtime.Engine.GenerateAsync(generation, engineProgress, ct);
            return CliProtocol.Result(request, ("outputPath", Path.GetFullPath(result.OutputPath)),
                ("mode", CommandParser.ModeName(mode)), ("durationSeconds", result.Duration.TotalSeconds), ("frames", result.Frames),
                ("elapsedSeconds", result.Elapsed.TotalSeconds), ("modelSource", SourceText(runtime.SourceFor(mode))),
                ("metadata", WavMetadata.TryRead(result.OutputPath)));
        }

        switch (operation)
        {
            case "help": return CliProtocol.Result(request, ("help", CommandParser.Help));
            case "version": return CliProtocol.Result(request, ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString()), ("runtime", "onnx"));
            case "transcribe":
                emit(CliProtocol.Event(request, "transcribing"));
                return CliProtocol.Result(request, ("transcript", await runtime.TranscribeAsync(request.Get("target")!, request.Get("language") ?? "auto", downloadProgress, ct, trimReference: false)));
            case "speakers":
                await runtime.Engine.PreloadAsync(TtsMode.PresetVoice, engineProgress, ct);
                return CliProtocol.Result(request, ("speakers", runtime.Engine.Speakers));
            case "server.preload":
                await runtime.Engine.PreloadAsync(mode, engineProgress, ct);
                return CliProtocol.Result(request, ("loadedMode", CommandParser.ModeName(mode)));
            case "server.unload":
                await runtime.Engine.UnloadAsync();
                return CliProtocol.Result(request, ("loadedMode", null));
            case "models.list": return CliProtocol.Result(request, ("models", DownloadedModels.Read(runtime.ModelsRoot)));
            case "models.status":
                var modes = request.Has("mode") ? new[] { mode } : Enum.GetValues<TtsMode>();
                return CliProtocol.Result(request, ("models", modes.Select(m =>
                {
                    var source = runtime.SourceFor(m);
                    var folder = ModelDownloader.FolderFor(source, runtime.ModelsRoot);
                    return new { mode = CommandParser.ModeName(m), source = SourceText(source), folder,
                        completeness = ModelDownloader.Inspect(folder, Layout(m)), approximateBytes = Layout(m).ApproxDownloadBytes,
                        loaded = runtime.Engine.LoadedMode == m };
                }).ToArray()));
            case "models.download":
                var downloaded = await runtime.DownloadModelsAsync(request.Has("all") ? null : mode, downloadProgress, ct);
                return CliProtocol.Result(request, ("assets", downloaded.Select(asset => asset with { Source = CliLog.Redact(asset.Source) }).ToArray()));
            case "models.verify":
                using (runtime.AcquireOperation(operation))
                {
                    var source = runtime.SourceFor(mode);
                    var folder = ModelDownloader.FolderFor(source, runtime.ModelsRoot);
                    var completeness = ModelDownloader.Inspect(folder, Layout(mode));
                    if (!completeness.IsComplete)
                    {
                        var incomplete = CliProtocol.Error(request, "model_incomplete", "The model is missing or incomplete. Download it before verification.", 3);
                        incomplete["completeness"] = completeness;
                        return incomplete;
                    }
                    emit(CliProtocol.Event(request, "verifying"));
                    var bad = await runtime.Downloader.VerifyAsync(source, Layout(mode), folder, ct);
                    if (bad.Count > 0)
                    {
                        var mismatch = CliProtocol.Error(request, "checksum_mismatch", "Model files failed verification. Remove and download the model again.");
                        mismatch["files"] = bad;
                        return mismatch;
                    }
                    return CliProtocol.Result(request, ("mode", CommandParser.ModeName(mode)), ("folder", folder), ("isComplete", true), ("mismatches", bad));
                }
            case "models.remove":
                using (runtime.AcquireOperation(operation))
                {
                    var folder = ModelDownloader.FolderFor(runtime.SourceFor(mode), runtime.ModelsRoot);
                    if (runtime.Engine.LoadedFolder is { } loadedFolder && PathsEqual(loadedFolder, folder)) throw new CliException("model_loaded", "Unload this model with bunyi server unload before removing it.", 4);
                    var model = DownloadedModels.Read(runtime.ModelsRoot).SingleOrDefault(m => PathsEqual(m.Folder, folder))
                        ?? throw CommandParser.Missing("The configured model is not downloaded.");
                    if (!DownloadedModels.TryDelete(model, log)) throw new IOException("Could not move the model to Trash.");
                    return CliProtocol.Result(request, ("removedFolder", Path.GetFullPath(model.Folder)), ("recoverable", true));
                }
            case "doctor":
                var reports = new List<DoctorReport>();
                foreach (var m in request.Has("mode") ? new[] { mode } : Enum.GetValues<TtsMode>())
                {
                    var report = await runtime.DoctorAsync(m, request.Has("deep"), ct);
                    reports.Add(report with { Findings = report.Findings.Select(f => f with { Detail = CliLog.Redact(f.Detail) }).ToArray() });
                }
                var reportResult = CliProtocol.Result(request, ("reports", reports));
                if (reports.Any(r => r.HasBlockers))
                {
                    reportResult = CliProtocol.Error(request, "preflight_blocked", "Doctor found blockers. See reports for findings and suggested actions.", 3);
                    reportResult["reports"] = reports;
                }
                return reportResult;
            case "voices.list": return CliProtocol.Result(request, ("voices", Voices().Voices));
            case "voices.add":
                {
                    var transcript = request.Get("transcript");
                    if (request.Has("auto-transcribe"))
                    {
                        emit(CliProtocol.Event(request, "transcribing"));
                        transcript = await runtime.TranscribeAsync(request.Get("reference")!, "auto", downloadProgress, ct);
                    }
                    using var voiceLease = runtime.AcquireOperation(operation);
                    var voice = Voices().Save(request.Get("name")!, request.Get("reference")!, transcript!);
                    return CliProtocol.Result(request, ("voice", voice), ("clipPath", Path.Combine(AppPaths.Voices, voice.FileName)));
                }
            case "voices.remove":
                using (runtime.AcquireOperation(operation))
                {
                    var library = Voices(); var voice = FindVoice(library, request.Get("target")!);
                    library.Delete(voice);
                    return CliProtocol.Result(request, ("removedId", voice.Id), ("recoverable", false));
                }
            case "history.list": return CliProtocol.Result(request, ("outputs", GeneratedOutputs.Read(AppPaths.Outputs)));
            case "history.show": return CliProtocol.Result(request, ("output", FindOutput(request.Get("target")!)));
            case "history.remove":
                var output = FindOutput(request.Get("target")!);
                if (!Trash.TryMoveToTrash(output.Path, log)) throw new IOException("Could not move the output to Trash.");
                return CliProtocol.Result(request, ("removedPath", output.Path), ("recoverable", true));
            case "backup.create":
            case "backup.restore":
                using (runtime.AcquireOperation(operation))
                {
                    var manager = new BackupManager(log);
                    var progress = new InlineProgress<BackupProgress>(p => emit(CliProtocol.Event(request, "verifying", ("fraction", p.Fraction), ("detail", p.Detail))));
                    if (operation == "backup.create")
                    {
                        if (File.Exists(request.Get("target"))) throw CommandParser.Invalid("Backup destination already exists; choose a new path.");
                        await manager.BackupAsync(runtime.ModelsRoot, request.Get("target")!, progress, ct);
                        return CliProtocol.Result(request, ("outputPath", request.Get("target")));
                    }
                    if (runtime.Engine.LoadedMode is not null) throw new CliException("model_loaded", "Unload the model before restoring a backup.", 4);
                    var skipped = await manager.RestoreAsync(request.Get("target")!, runtime.ModelsRoot, progress, ct);
                    return CliProtocol.Result(request, ("modelsFolder", runtime.ModelsRoot), ("skipped", skipped));
                }
            case "config.list": return CliProtocol.Result(request, ("settings", Configuration(runtime)));
            case "config.get":
                var settings = Configuration(runtime);
                if (!settings.TryGetValue(request.Get("target")!, out var value)) throw CommandParser.Invalid("Unknown configuration key.");
                return CliProtocol.Result(request, ("key", request.Get("target")), ("value", value));
            case "config.set":
                // A disappeared custom drive must not make the command that repairs
                // its setting unusable. Desktop falls back to the default root.
                var configuredRoot = runtime.CurrentSettings.ModelsFolder;
                var configLeaseRoot = !string.IsNullOrWhiteSpace(configuredRoot) && Directory.Exists(configuredRoot)
                    ? configuredRoot : AppPaths.DefaultModelsFolder;
                using (ModelOperationLease.Acquire(configLeaseRoot)) return SetConfig(request, runtime);
            case "logs.path": return CliProtocol.Result(request, ("path", CliLog.Path));
            case "logs.tail":
                var lines = 100;
                if (request.Has("lines") && (!int.TryParse(request.Get("lines"), out lines) || lines < 1 || lines > 10000)) throw CommandParser.Invalid("--lines must be between 1 and 10000.");
                return CliProtocol.Result(request, ("path", CliLog.Path), ("lines", File.Exists(CliLog.Path) ? File.ReadLines(CliLog.Path).TakeLast(lines).ToArray() : []));
            case "logs.clear":
                if (File.Exists(CliLog.Path)) File.WriteAllText(CliLog.Path, "");
                return CliProtocol.Result(request, ("path", CliLog.Path));
            default: throw CommandParser.Invalid("Unknown command.");
        }
    }

    private VoiceLibrary Voices() { var library = new VoiceLibrary(log); library.Load(); return library; }
    private static SavedVoice FindVoice(VoiceLibrary library, string id) => Guid.TryParse(id, out var guid)
        ? library.Voices.SingleOrDefault(v => v.Id == guid) ?? throw CommandParser.Missing("No saved voice has that ID.")
        : throw CommandParser.Invalid("Use the exact voice ID from voices list.");
    private static GeneratedOutput FindOutput(string path) => GeneratedOutputs.Read(AppPaths.Outputs).SingleOrDefault(o => PathsEqual(o.Path, path))
        ?? throw CommandParser.Missing("That exact path is not a Bunyi history output.");
    private static bool PathsEqual(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static ModelLayout Layout(TtsMode mode) => mode switch { TtsMode.PresetVoice => ModelLayout.PresetVoice, TtsMode.VoiceDesign => ModelLayout.VoiceDesign, _ => ModelLayout.VoiceClone };
    public static string SourceText(ModelSource source) => source switch { ModelSource.Repo repo => repo.Id, ModelSource.BaseUrl url => CliLog.Redact(url.Url.AbsoluteUri), _ => "" };

    private static Dictionary<string, object?> Configuration(BunyiRuntime runtime) => new()
    {
        ["modelsFolder"] = runtime.CurrentSettings.ModelsFolder ?? AppPaths.DefaultModelsFolder,
        ["unloadOnModeSwitch"] = runtime.CurrentSettings.UnloadOnModeSwitch,
        ["modelSource.preset"] = SourceText(runtime.SourceFor(TtsMode.PresetVoice)),
        ["modelSource.design"] = SourceText(runtime.SourceFor(TtsMode.VoiceDesign)),
        ["modelSource.clone"] = SourceText(runtime.SourceFor(TtsMode.VoiceClone))
    };

    private static Dictionary<string, object?> SetConfig(CommandRequest request, BunyiRuntime runtime)
    {
        var key = request.Get("target")!; var value = request.Get("value")!;
        var settings = runtime.CurrentSettings;
        if (key == "modelsFolder")
        {
            var folder = Path.GetFullPath(value);
            if (!Directory.Exists(folder)) throw CommandParser.Missing("The models folder must already exist.");
            settings = settings with { ModelsFolder = folder };
        }
        else if (key == "unloadOnModeSwitch")
        {
            if (!bool.TryParse(value, out var boolean)) throw CommandParser.Invalid("unloadOnModeSwitch accepts true or false.");
            settings = settings with { UnloadOnModeSwitch = boolean };
        }
        else if (key.StartsWith("modelSource.", StringComparison.Ordinal))
        {
            var mode = CommandParser.Mode(key[12..]);
            var source = ModelSource.Parse(value, SourceText(runtime.SourceFor(mode)));
            _ = ModelDownloader.FolderFor(source, runtime.ModelsRoot);
            settings = settings.WithSourceFor(mode, value);
        }
        else throw CommandParser.Invalid("Unknown configuration key.");
        runtime.Settings.SaveStrict(settings);
        return CliProtocol.Result(request, ("key", key), ("value", key == "modelsFolder" ? settings.ModelsFolder : value));
    }
}
