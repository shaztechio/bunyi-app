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
