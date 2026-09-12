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

public sealed class PackagedDefaultsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "bunyi-defaults", Guid.NewGuid().ToString("N"));
    private readonly TestLog _log = new();
    public PackagedDefaultsTests() => Directory.CreateDirectory(_folder);
    public void Dispose() => Directory.Delete(_folder, true);

    [Theory]
    [InlineData("mirror", true)]
    [InlineData("huggingFace", false)]
    public void Reads_packaged_choice_without_modifying_it(string source, bool mirror)
    {
        var path = Path.Combine(_folder, PackagedModelDefaults.FileName);
        var json = "{\"schemaVersion\":1,\"modelDownloadSource\":\"" + source + "\",\"futureField\":true}";
        File.WriteAllText(path, json);
        Assert.Equal(mirror, PackagedModelDefaults.Load(_log, _folder).UseMirror);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":2,\"modelDownloadSource\":\"mirror\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"modelDownloadSource\":\"mirror\"}")]
    [InlineData("{\"schemaVersion\":1,\"modelDownloadSource\":\"unknown\"}")]
    [InlineData("{\"schemaVersion\":1,\"modelDownloadSource\":true}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void Invalid_or_missing_defaults_fall_back_with_a_diagnostic(string? json)
    {
        if (json is not null) File.WriteAllText(Path.Combine(_folder, PackagedModelDefaults.FileName), json);
        Assert.False(PackagedModelDefaults.Load(_log, _folder).UseMirror);
        Assert.Contains(_log.Messages, message => message.Contains("using Hugging Face"));
    }

    [Fact]
    public async Task Explicit_sources_survive_restart_and_a_changed_package_default()
    {
        var store = new SettingsStore(_log, Path.Combine(_folder, "settings.json"));
        store.SaveStrict(new AppSettings { ModelsFolder = _folder });
        await using (var first = new BunyiRuntime(_log, store, defaults: new(true)))
        {
            Assert.All(Enum.GetValues<TtsMode>(), mode => Assert.IsType<ModelSource.BaseUrl>(first.SourceFor(mode)));
            var settings = first.CurrentSettings;
            foreach (var mode in Enum.GetValues<TtsMode>()) settings = settings.WithSourceFor(mode, BunyiRuntime.HuggingFaceSourceFor(mode));
            store.SaveStrict(settings.WithSourceFor(TtsMode.VoiceDesign, "custom/designed-voice"));
        }
        foreach (var mirror in new[] { true, false })
        {
            await using var restarted = new BunyiRuntime(_log, store, defaults: new(mirror));
            Assert.Equal(new ModelSource.Repo(BunyiRuntime.HuggingFaceSourceFor(TtsMode.PresetVoice)), restarted.SourceFor(TtsMode.PresetVoice));
            Assert.Equal(new ModelSource.Repo("custom/designed-voice"), restarted.SourceFor(TtsMode.VoiceDesign));
            Assert.Equal(new ModelSource.Repo(BunyiRuntime.HuggingFaceSourceFor(TtsMode.VoiceClone)), restarted.SourceFor(TtsMode.VoiceClone));
        }
    }

    [Theory]
    [InlineData(TtsMode.PresetVoice)]
    [InlineData(TtsMode.VoiceDesign)]
    [InlineData(TtsMode.VoiceClone)]
    public async Task Recovery_uses_upstream_even_when_the_package_defaults_to_mirror(TtsMode mode)
    {
        var store = new SettingsStore(_log, Path.Combine(_folder, "settings.json"));
        store.SaveStrict(new AppSettings { ModelsFolder = _folder });
        await using var runtime = new BunyiRuntime(_log, store, defaults: new(true));
        var failure = new DownloadServiceException("download_service_unavailable", "Paused",
            new Uri(ModelConfigLibrary.BunyiMirror.For(mode) + "/manifest.sha256"));
        Assert.True(runtime.CanUseHuggingFace(mode, failure));
        runtime.UseHuggingFace(mode, failure);
        Assert.Equal(new ModelSource.Repo(BunyiRuntime.HuggingFaceSourceFor(mode)), runtime.SourceFor(mode));
        Assert.False(runtime.CanUseHuggingFace(mode, failure));
        foreach (var other in Enum.GetValues<TtsMode>().Where(other => other != mode))
            Assert.IsType<ModelSource.BaseUrl>(runtime.SourceFor(other));
        await using var restarted = new BunyiRuntime(_log, store, defaults: new(true));
        Assert.Equal(runtime.SourceFor(mode), restarted.SourceFor(mode));
    }

    private sealed class TestLog : ILogSink
    {
        public List<string> Messages { get; } = [];
        public void Log(string message) => Messages.Add(message);
    }
}
