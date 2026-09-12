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

using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Infrastructure;
using Bunyi.Core.Models;
using Bunyi.Core.Qwen;
using Bunyi.Core.Settings;
using Bunyi.Core.Transcription;

namespace Bunyi.Core.Runtime;

/// <summary>The UI-free composition shared by desktop, disposable CLI, and resident server.</summary>
public sealed class BunyiRuntime : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly bool _strictSettings;
    private readonly ILogSink _log;
    private readonly PackagedModelDefaults _defaults;
    private readonly object _leaseGate = new();
    private bool _nonModelOperation;
    private readonly Dictionary<string, (ModelOperationLease Lease, int References)> _leases =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public BunyiRuntime(ILogSink? log = null, SettingsStore? settings = null,
        HttpClient? http = null, bool strictSettings = false, PackagedModelDefaults? defaults = null)
    {
        _log = log ?? LogStore.Shared;
        _defaults = defaults ?? PackagedModelDefaults.Load(_log);
        Settings = settings ?? new SettingsStore(_log);
        _strictSettings = strictSettings;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        Downloader = new ModelDownloader(_http, _log);
        var preset = new PresetSpeechSynthesizer(_log);
        var design = new DesignSpeechSynthesizer(_log);
        var clone = new CloneSpeechSynthesizer(_log);
        Engine = new OnnxTtsEngine(mode => mode switch
        {
            TtsMode.VoiceDesign => design,
            TtsMode.VoiceClone => clone,
            _ => (ISpeechSynthesizer)preset,
        }, Downloader, _log, SourceFor, ModelLayout.For, () => ModelsRoot,
            () => AppPaths.Outputs,
            typeof(BunyiRuntime).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            doctor: DoctorAsync, acquireLease: () => AcquireOperation("model"),
            validateOperation: () =>
            {
                lock (_leaseGate) { if (_nonModelOperation) throw new BunyiBusyException(ModelsRoot); }
            });
    }

    public SettingsStore Settings { get; }
    public AppSettings CurrentSettings => Settings.Load();
    public OnnxTtsEngine Engine { get; }
    public ModelDownloader Downloader { get; }
    public string ModelsRoot
    {
        get
        {
            var current = CurrentSettings;
            if (_strictSettings && !string.IsNullOrWhiteSpace(current.ModelsFolder)
                && !Directory.Exists(current.ModelsFolder))
                throw new DirectoryNotFoundException("The configured models folder is unavailable: " + current.ModelsFolder);
            return Path.GetFullPath(Settings.ResolveModelsFolder(current));
        }
    }

    public static string HuggingFaceSourceFor(TtsMode mode) => mode switch
    {
        TtsMode.PresetVoice => "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        TtsMode.VoiceDesign => "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX",
        TtsMode.VoiceClone => "wavekat/Qwen3-TTS-0.6B-Base-ONNX",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public string DefaultSourceFor(TtsMode mode) => _defaults.UseMirror
        ? ModelConfigLibrary.BunyiMirror.For(mode)!
        : HuggingFaceSourceFor(mode);

    public ModelSource SourceFor(TtsMode mode) =>
        ModelSource.Parse(CurrentSettings.SourceFor(mode), DefaultSourceFor(mode));

    /// <summary>Only the selected built-in mirror can offer a source switch.</summary>
    public bool CanUseHuggingFace(TtsMode mode, DownloadServiceException failure)
    {
        if (failure.Code != "download_service_unavailable" || SourceFor(mode) is not ModelSource.BaseUrl source)
            return false;
        var mirror = new Uri(ModelConfigLibrary.BunyiMirror.For(mode)!);
        return source.Url.AbsoluteUri.TrimEnd('/') == mirror.AbsoluteUri.TrimEnd('/')
            && failure.SourceUri.GetLeftPart(UriPartial.Authority) == mirror.GetLeftPart(UriPartial.Authority)
            && failure.SourceUri.AbsolutePath.StartsWith(mirror.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    public void UseHuggingFace(TtsMode mode, DownloadServiceException failure)
    {
        // Check again at invocation time: settings may have changed since the failure.
        if (!CanUseHuggingFace(mode, failure)) throw new InvalidOperationException("The model source has changed. Press Generate to try your current source.");
        using var lease = AcquireOperation("config.set");
        Settings.SaveStrict(CurrentSettings.WithSourceFor(mode, HuggingFaceSourceFor(mode)));
    }

    public Task<DoctorReport> DoctorAsync(TtsMode mode, bool deep, CancellationToken ct) =>
        Doctor.RunAsync(mode, SourceFor(mode), ModelLayout.For(mode), ModelsRoot, AppPaths.Outputs,
            new SystemProbe(), Reachable,
            (folder, token) => Downloader.VerifyAsync(SourceFor(mode), ModelLayout.For(mode), folder, token),
            deep, provider: null, ct: ct);

    private static async Task<bool> Reachable(Uri uri, CancellationToken ct)
    {
        try
        {
            using var quick = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await quick.SendAsync(request, ct).ConfigureAwait(false);
            return response.StatusCode != System.Net.HttpStatusCode.RequestTimeout;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    // Nested calls in a serialized runtime reuse its resident-model lease.
    // This does not serialize callers: the engine and server own scheduling.
    public IDisposable AcquireOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var root = ModelsRoot;
        lock (_leaseGate)
        {
            if (_nonModelOperation || (operation != "model" && Engine.Status.IsBusy))
                throw new BunyiBusyException(root);
            if (_leases.TryGetValue(root, out var current))
                _leases[root] = (current.Lease, current.References + 1);
            else _leases[root] = (ModelOperationLease.Acquire(root), 1);
            if (operation != "model") _nonModelOperation = true;
        }
        return new ActionLease(() =>
        {
            lock (_leaseGate)
            {
                if (operation != "model") _nonModelOperation = false;
                var current = _leases[root];
                if (current.References > 1) _leases[root] = (current.Lease, current.References - 1);
                else { current.Lease.Dispose(); _leases.Remove(root); }
            }
        });
    }

    public async Task<string> TranscribeAsync(string path, CancellationToken ct)
        => await TranscribeAsync(path, "english", null, ct).ConfigureAwait(false);

    public async Task<string> TranscribeAsync(string path, string language,
        IProgress<AggregateDownloadProgress>? progress, CancellationToken ct, bool trimReference = true)
    {
        // Never hold Whisper and a TTS model together.
        await Engine.UnloadAsync().ConfigureAwait(false);
        using var lease = AcquireOperation("transcribe");
        // Whisper is disposed after each transcription, including cancellation.
        using var transcriber = new WhisperTranscriber(async token =>
        {
            var assets = await Downloader.DownloadAssetsAsync(
                [new("whisper", new ModelSource.Repo(ModelLayout.WhisperSource), ModelLayout.Whisper)],
                ModelsRoot, progress, token).ConfigureAwait(false);
            return Path.Combine(assets[0].Folder, "ggml-base.bin");
        }, _log);
        var trimmed = trimReference ? ReferenceAudio.WriteTrimmedCopy(path, TimeSpan.FromSeconds(10), _log) : null;
        try { return await transcriber.TranscribeAsync(trimmed ?? path, language, ct).ConfigureAwait(false); }
        finally { if (trimmed is not null && File.Exists(trimmed)) File.Delete(trimmed); }
    }

    public async Task<IReadOnlyList<DownloadedAsset>> DownloadModelsAsync(TtsMode? mode,
        IProgress<AggregateDownloadProgress>? progress, CancellationToken ct)
    {
        await Engine.UnloadAsync().ConfigureAwait(false);
        using var lease = AcquireOperation("models.download");
        var modes = mode.HasValue ? new[] { mode.Value } : Enum.GetValues<TtsMode>();
        var assets = modes.Select(m => new DownloadAsset(m switch
        {
            TtsMode.PresetVoice => "preset", TtsMode.VoiceDesign => "design", _ => "clone",
        }, SourceFor(m), ModelLayout.For(m))).ToList();
        if (!mode.HasValue) assets.Add(new("whisper", new ModelSource.Repo(ModelLayout.WhisperSource), ModelLayout.Whisper));
        return await Downloader.DownloadAssetsAsync(assets, ModelsRoot, progress, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync().ConfigureAwait(false);
        if (_ownsHttp) _http.Dispose();
    }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
