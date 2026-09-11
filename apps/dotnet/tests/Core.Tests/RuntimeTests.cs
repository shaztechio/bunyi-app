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
using Bunyi.Core.Models;
using Bunyi.Core.Runtime;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bunyi-runtime-tests", Guid.NewGuid().ToString("N"));
    private readonly SilentLog _log = new();
    public RuntimeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task All_download_includes_every_tts_mode_and_whisper_without_loading()
    {
        var store = new SettingsStore(_log, Path.Combine(_root, "settings.json"));
        store.SaveStrict(new AppSettings { ModelsFolder = _root });
        await using var runtime = new BunyiRuntime(_log, store, strictSettings: true);
        foreach (var mode in Enum.GetValues<TtsMode>())
            Complete(runtime.SourceFor(mode), ModelLayout.For(mode));
        Complete(new ModelSource.Repo(ModelLayout.WhisperSource), ModelLayout.Whisper);
        var assets = await runtime.DownloadModelsAsync(null, null, default);
        Assert.Equal(new[] { "preset", "design", "clone", "whisper" }, assets.Select(a => a.Id));
        Assert.All(assets, asset => Assert.True(asset.IsComplete));
        Assert.Null(runtime.Engine.LoadedMode);
        using var available = ModelOperationLease.Acquire(_root);
    }

    [Fact]
    public async Task Storage_operation_blocks_inference_and_other_runtime_instances()
    {
        var store = new SettingsStore(_log, Path.Combine(_root, "settings.json"));
        store.SaveStrict(new AppSettings { ModelsFolder = _root });
        await using var runtime = new BunyiRuntime(_log, store, strictSettings: true);
        using (runtime.AcquireOperation("backup.restore"))
        {
            Assert.Throws<BunyiBusyException>(() => ModelOperationLease.Acquire(_root));
            await Assert.ThrowsAsync<BunyiBusyException>(() => runtime.Engine.PreloadAsync(TtsMode.PresetVoice, null, default));
        }
        using var available = ModelOperationLease.Acquire(_root);
    }

    [Fact]
    public async Task Strict_runtime_does_not_fall_back_from_missing_configured_folder()
    {
        var store = new SettingsStore(_log, Path.Combine(_root, "settings.json"));
        store.SaveStrict(new AppSettings { ModelsFolder = Path.Combine(_root, "missing") });
        await using var runtime = new BunyiRuntime(_log, store, strictSettings: true);
        Assert.Throws<DirectoryNotFoundException>(() => runtime.ModelsRoot);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("org/../../escape")]
    [InlineData("/absolute")]
    [InlineData("org/..")]
    public void Repository_identifiers_cannot_escape_the_models_folder(string id) =>
        Assert.Throws<ArgumentException>(() => ModelDownloader.FolderFor(new ModelSource.Repo(id), _root));

    [Fact]
    public async Task Mirror_recovery_is_explicit_mode_scoped_and_preserves_separate_caches()
    {
        var store = new SettingsStore(_log, Path.Combine(_root, "settings.json"));
        var mirror = ModelConfigLibrary.BunyiMirror.For(TtsMode.VoiceClone);
        store.SaveStrict(new AppSettings { ModelsFolder = _root }.WithSourceFor(TtsMode.VoiceClone, mirror)
            .WithSourceFor(TtsMode.VoiceDesign, "custom/design"));
        await using var runtime = new BunyiRuntime(_log, store, strictSettings: true);
        var oldSource = runtime.SourceFor(TtsMode.VoiceClone);
        var partial = Path.Combine(ModelDownloader.FolderFor(oldSource, _root), "vocoder.onnx.data.incomplete");
        Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        await File.WriteAllBytesAsync(partial, [1, 2]);
        var failure = new DownloadServiceException("download_service_unavailable", "Paused", new(mirror + "/manifest.sha256"));
        Assert.True(runtime.CanUseHuggingFace(TtsMode.VoiceClone, failure));
        Assert.Equal(mirror, store.Load().SourceFor(TtsMode.VoiceClone));
        Assert.False(runtime.CanUseHuggingFace(TtsMode.PresetVoice, failure));
        Assert.False(runtime.CanUseHuggingFace(TtsMode.VoiceClone,
            new("download_service_unavailable", "Down", new("https://huggingface.co/whisper/model"))));
        runtime.UseHuggingFace(TtsMode.VoiceClone, failure);
        Assert.Equal(BunyiRuntime.DefaultSourceFor(TtsMode.VoiceClone), store.Load().SourceFor(TtsMode.VoiceClone));
        Assert.Equal("custom/design", store.Load().SourceFor(TtsMode.VoiceDesign));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partial));
        Assert.NotEqual(ModelDownloader.FolderFor(oldSource, _root), ModelDownloader.FolderFor(runtime.SourceFor(TtsMode.VoiceClone), _root));
        Assert.False(runtime.CanUseHuggingFace(TtsMode.VoiceClone, failure));
        Assert.Throws<InvalidOperationException>(() => runtime.UseHuggingFace(TtsMode.VoiceClone, failure));
    }

    [Theory]
    [InlineData("https://models.bunyi.app/onnx/voiceclone?custom=yes")]
    [InlineData("https://models.bunyi.app/onnx/voiceclone/custom")]
    [InlineData("https://another.example/onnx/voiceclone")]
    [InlineData("http://models.bunyi.app/onnx/voiceclone")]
    public async Task Custom_servers_never_offer_the_builtin_recovery(string source)
    {
        var store = new SettingsStore(_log, Path.Combine(_root, "settings.json"));
        store.SaveStrict(new AppSettings { ModelsFolder = _root }.WithSourceFor(TtsMode.VoiceClone, source));
        await using var runtime = new BunyiRuntime(_log, store);
        Assert.False(runtime.CanUseHuggingFace(TtsMode.VoiceClone,
            new("download_service_unavailable", "Down", new(source.TrimEnd('/') + "/manifest.sha256"))));
    }

    private void Complete(ModelSource source, ModelLayout layout)
    {
        var folder = ModelDownloader.FolderFor(source, _root);
        foreach (var file in layout.Files)
        {
            var path = Path.Combine(folder, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);
        }
    }

    private sealed class SilentLog : ILogSink { public void Log(string message) { } }
}
