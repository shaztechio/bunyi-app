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
/// One engine, a synthesizer per mode (spec §1).
/// </summary>
/// <remarks>
/// Preset voice and voice design are different pipelines over different
/// exports. The engine's state machine, downloads, metadata and stop behaviour
/// are mode-agnostic and stay in one place; only the synthesizer changes — and
/// the one being left behind has to let go of several gigabytes.
/// </remarks>
public sealed class EngineRoutingTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private FakeModelServer _server = null!;
    private HttpClient _http = null!;
    private RecordingLog _log = null!;

    private static ModelLayout Layout { get; } = new(
        "routing", [new ModelFile("model.onnx", Required: true)]);

    public async Task InitializeAsync()
    {
        _server = await FakeModelServer.StartAsync();
        _http = new HttpClient();
        _log = new RecordingLog();
        _server.AddBinary("model.onnx", 2_048);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A synthesizer that records how it was used.</summary>
    private sealed class TrackingSynthesizer(string name) : ISpeechSynthesizer
    {
        public string Name { get; } = name;
        public int Loads { get; private set; }
        public int Unloads { get; private set; }
        public int Syntheses { get; private set; }
        public bool IsLoaded { get; private set; }

        public IReadOnlyList<string> Speakers { get; set; } = [];
        public bool SupportsInstruct { get; set; } = true;

        public Task LoadAsync(string modelFolder, CancellationToken ct)
        {
            Loads++;
            IsLoaded = true;
            return Task.CompletedTask;
        }

        public Task UnloadAsync()
        {
            Unloads++;
            IsLoaded = false;
            return Task.CompletedTask;
        }

        public Task<SynthesisResult> SynthesizeAsync(
            GenerateRequest request, CancellationToken ct, IProgress<int>? frames = null)
        {
            Syntheses++;
            return Task.FromResult(new SynthesisResult(new short[2400], 24_000, 2));
        }

        public void ReleaseWorkingMemory() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private (OnnxTtsEngine Engine, TrackingSynthesizer Preset, TrackingSynthesizer Design) New()
    {
        var preset = new TrackingSynthesizer("preset");
        var design = new TrackingSynthesizer("design") { Speakers = [] };
        preset.Speakers = ["ryan", "aiden"];

        var engine = new OnnxTtsEngine(
            mode => mode == TtsMode.VoiceDesign ? design : preset,
            new ModelDownloader(_http, _log),
            _log,
            _ => new ModelSource.BaseUrl(_server.BaseUrl),
            _ => Layout,
            () => _root,
            () => Path.Combine(_root, "Outputs"));

        return (engine, preset, design);
    }

    private static GenerateRequest Request(TtsMode mode) =>
        new(mode, "Hello there.", Instruct: mode == TtsMode.VoiceDesign ? "A warm voice" : null);

    [Fact]
    public async Task Each_mode_is_generated_by_its_own_synthesizer()
    {
        var (engine, preset, design) = New();
        await using var _ = engine;

        await engine.GenerateAsync(Request(TtsMode.PresetVoice), null, default);
        await engine.GenerateAsync(Request(TtsMode.VoiceDesign), null, default);

        Assert.Equal(1, preset.Syntheses);
        Assert.Equal(1, design.Syntheses);
    }

    [Fact]
    public async Task Switching_mode_unloads_the_model_being_left_behind()
    {
        // The reason this is routed at all. Two exports resident is several
        // gigabytes that nothing will ask for again, and §3d makes the same
        // point about deletion: on Windows a loaded session holds its weights
        // open.
        var (engine, preset, design) = New();
        await using var _ = engine;

        await engine.GenerateAsync(Request(TtsMode.PresetVoice), null, default);
        Assert.True(preset.IsLoaded);

        await engine.GenerateAsync(Request(TtsMode.VoiceDesign), null, default);

        Assert.False(preset.IsLoaded);
        Assert.Equal(1, preset.Unloads);
        Assert.True(design.IsLoaded);
    }

    [Fact]
    public async Task Staying_in_one_mode_loads_the_model_once()
    {
        // Re-opening several gigabytes of sessions per generation would make
        // every run as slow as the first.
        var (engine, preset, _) = New();
        await using var __ = engine;

        await engine.GenerateAsync(Request(TtsMode.PresetVoice), null, default);
        await engine.GenerateAsync(Request(TtsMode.PresetVoice), null, default);

        Assert.Equal(1, preset.Loads);
        Assert.Equal(2, preset.Syntheses);
    }

    [Fact]
    public async Task Switching_back_reloads_the_model_that_was_released()
    {
        // It was unloaded, so it has to come back — the engine must not think
        // it is still resident from the first time.
        var (engine, preset, _) = New();
        await using var __ = engine;

        await engine.GenerateAsync(Request(TtsMode.PresetVoice), null, default);
        await engine.GenerateAsync(Request(TtsMode.VoiceDesign), null, default);
        await engine.GenerateAsync(Request(TtsMode.PresetVoice), null, default);

        Assert.Equal(2, preset.Loads);
        Assert.True(preset.IsLoaded);
    }

    [Fact]
    public async Task The_speakers_offered_are_the_current_modes()
    {
        // Design mode has none — the voice comes from a description — so the
        // picker must empty rather than keep offering the other mode's.
        var (engine, _, _) = New();
        await using var __ = engine;

        Assert.Equal(["ryan", "aiden"], engine.Speakers);

        await engine.GenerateAsync(Request(TtsMode.VoiceDesign), null, default);

        Assert.Empty(engine.Speakers);
    }

    [Fact]
    public async Task Speakers_are_available_before_anything_has_been_generated()
    {
        // The picker is filled when the window opens, which is before any run.
        var (engine, _, _) = New();
        await using var __ = engine;

        Assert.NotEmpty(engine.Speakers);
    }

    [Fact]
    public async Task A_description_survives_when_the_mode_acts_on_it()
    {
        // §1: recorded metadata must describe what produced the audio. Design
        // mode's whole voice comes from the description, so dropping it would
        // make the file unreproducible.
        var (engine, _, design) = New();
        await using var __ = engine;

        design.SupportsInstruct = true;

        var result = await engine.GenerateAsync(Request(TtsMode.VoiceDesign), null, default);

        Assert.True(File.Exists(result.OutputPath));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("was not applied"));
    }

    [Fact]
    public async Task A_description_is_dropped_when_the_mode_ignores_it()
    {
        var (engine, _, design) = New();
        await using var __ = engine;

        design.SupportsInstruct = false;

        await engine.GenerateAsync(Request(TtsMode.VoiceDesign), null, default);

        Assert.Contains(_log.Lines, l => l.Contains("was not applied"));
    }

    /// <summary>A log that keeps what it was told.</summary>
    /// <remarks>
    /// Private, as in every other test class here. Six copies is more than
    /// ideal, but sharing one would be a change across all of them and this is
    /// not the place for it.
    /// </remarks>
    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get { lock (_lines) return [.. _lines]; }
        }

        public void Log(string message)
        {
            lock (_lines) _lines.Add(message);
        }
    }

    [Fact]
    public async Task Evicting_releases_whichever_model_is_loaded()
    {
        // §3d's delete-from-Settings path.
        var (engine, preset, _) = New();
        await using var __ = engine;

        await engine.GenerateAsync(Request(TtsMode.PresetVoice), null, default);
        await engine.UnloadAsync();

        Assert.False(preset.IsLoaded);
    }
}
