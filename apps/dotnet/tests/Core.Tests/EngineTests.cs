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
using Bunyi.Core.Models;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// The rules in §2 and §9 that are easy to get wrong and impossible to reach
/// through a real 5.88 GB model on a CI machine: one run at a time, a distinct
/// stopping state, memory released on every exit path, and the caller's thread
/// left alone.
/// </summary>
public sealed class EngineTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private FakeModelServer _server = null!;
    private HttpClient _http = null!;
    private RecordingLog _log = null!;

    private static ModelLayout Layout { get; } = new(
        "test", [new ModelFile("model.onnx", Required: true)]);

    public async Task InitializeAsync()
    {
        _server = await FakeModelServer.StartAsync();
        _server.AddBinary("model.onnx", 2_048);
        _http = new HttpClient();
        _log = new RecordingLog();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private OnnxTtsEngine NewEngine(FakeSynthesizer synth) => new(
        synth,
        new ModelDownloader(_http, _log),
        _log,
        _ => new ModelSource.BaseUrl(_server.BaseUrl),
        _ => Layout,
        () => _root,
        () => Path.Combine(_root, "Outputs"));

    private static GenerateRequest Request(string text = "Hello there.") =>
        new(TtsMode.PresetVoice, text, "english", "ryan");

    [Fact]
    public async Task A_run_writes_a_24_kHz_wav_and_reports_where()
    {
        var synth = new FakeSynthesizer();
        await using var engine = NewEngine(synth);

        var result = await engine.GenerateAsync(Request(), null, default);

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(result.OutputPath, engine.LastOutputPath);
        Assert.EndsWith(".wav", result.OutputPath);
        Assert.Contains("Preset-voice-", Path.GetFileName(result.OutputPath));
        Assert.Equal(EngineState.Idle, engine.Status.State);
    }

    [Fact]
    public async Task The_output_carries_what_produced_it()
    {
        var synth = new FakeSynthesizer();
        await using var engine = NewEngine(synth);

        var result = await engine.GenerateAsync(
            Request("Speak this.") with { Instruct = "cheerful" }, null, default);

        var metadata = WavMetadata.TryRead(result.OutputPath);
        Assert.NotNull(metadata);
        Assert.Equal("Preset voice", metadata.Mode);
        Assert.Equal("Speak this.", metadata.Text);
        Assert.Equal("ryan", metadata.Speaker);
        Assert.Equal("cheerful", metadata.Style);
        // A preset-voice style must never be recorded as a voice description.
        Assert.Null(metadata.VoiceDescription);
    }

    [Fact]
    public async Task Voice_design_records_its_description_not_a_style()
    {
        var synth = new FakeSynthesizer();
        await using var engine = NewEngine(synth);

        var result = await engine.GenerateAsync(
            new GenerateRequest(TtsMode.VoiceDesign, "Once.", "english",
                Instruct: "Warm documentary narrator"),
            null, default);

        var metadata = WavMetadata.TryRead(result.OutputPath)!;
        Assert.Equal("Warm documentary narrator", metadata.VoiceDescription);
        Assert.Null(metadata.Style);
        Assert.Null(metadata.Speaker);
    }

    [Fact]
    public async Task The_caller_is_released_before_inference_finishes()
    {
        // §2: the UI thread never does inference work and never writes the
        // output, so the window stays responsive for the whole run. Asserted
        // rather than assumed, because the failure is a frozen window rather
        // than an exception.
        //
        // Phrased as "the caller got control back", not "a different thread ran
        // it". Thread identity is the wrong test: xUnit runs on the thread pool,
        // so Task.Run may legitimately reuse the very thread the test started
        // on — which made an earlier version of this test fail about one run in
        // three while the app was behaving perfectly.
        var synth = new FakeSynthesizer { Gate = new SemaphoreSlim(0) };
        await using var engine = NewEngine(synth);

        var run = engine.GenerateAsync(Request(), null, default);
        await synth.Entered.Task;

        // Inference is underway and this thread is still free to work.
        Assert.False(run.IsCompleted);
        Assert.Equal(EngineState.Generating, engine.Status.State);

        synth.Gate.Release();
        var result = await run;
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task A_second_run_is_refused_while_one_is_in_progress()
    {
        // The single gate that makes a second job against the same model
        // impossible rather than merely unlikely.
        var synth = new FakeSynthesizer { Gate = new SemaphoreSlim(0) };
        await using var engine = NewEngine(synth);

        var first = engine.GenerateAsync(Request(), null, default);
        await synth.Entered.Task;

        await Assert.ThrowsAsync<EngineBusyException>(
            () => engine.GenerateAsync(Request(), null, default));

        synth.Gate.Release();
        await first;
    }

    [Fact]
    public async Task Stopping_reports_a_stopping_state_before_it_reports_idle()
    {
        // §2 is explicit that this is a state of its own: reporting ready early
        // invites a second job against a model the abandoned work still holds.
        var synth = new FakeSynthesizer { Gate = new SemaphoreSlim(0) };
        await using var engine = NewEngine(synth);
        var states = new List<EngineState>();
        engine.StatusChanged += (_, s) => { lock (states) states.Add(s.State); };

        var run = engine.GenerateAsync(Request(), null, default);
        await synth.Entered.Task;

        engine.RequestStop();
        Assert.Equal(EngineState.Stopping, engine.Status.State);

        synth.Gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        lock (states)
        {
            Assert.Contains(EngineState.Stopping, states);
            Assert.Equal(EngineState.Idle, states[^1]);
        }
    }

    [Fact]
    public async Task Working_memory_is_released_when_a_run_is_stopped()
    {
        // A run is stopped most often precisely because the machine is short of
        // memory. Releasing only on success would hold it in exactly the cases
        // that needed it back.
        var synth = new FakeSynthesizer { Gate = new SemaphoreSlim(0) };
        await using var engine = NewEngine(synth);

        var run = engine.GenerateAsync(Request(), null, default);
        await synth.Entered.Task;
        engine.RequestStop();
        synth.Gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(1, synth.Releases);
    }

    [Fact]
    public async Task Working_memory_is_released_when_a_run_fails()
    {
        var synth = new FakeSynthesizer { Throw = new InvalidOperationException("model exploded") };
        await using var engine = NewEngine(synth);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.GenerateAsync(Request(), null, default));

        Assert.Equal(1, synth.Releases);
        Assert.Equal(EngineState.Error, engine.Status.State);
    }

    [Fact]
    public async Task An_error_leaves_the_engine_usable_rather_than_stuck()
    {
        var synth = new FakeSynthesizer { Throw = new InvalidOperationException("once") };
        await using var engine = NewEngine(synth);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.GenerateAsync(Request(), null, default));

        synth.Throw = null;
        var result = await engine.GenerateAsync(Request(), null, default);
        Assert.True(File.Exists(result.OutputPath));
    }

    [Fact]
    public async Task The_engine_is_never_left_busy_after_a_run_ends()
    {
        // Regression: the engine reported download progress through
        // Progress<T>, which posts asynchronously, so a stale "Downloading"
        // could land after the run's final status and leave the engine busy
        // forever — refusing every future generation. It passed on one OS and
        // failed on a faster CI runner, which is what a timing bug looks like.
        var synth = new FakeSynthesizer();
        await using var engine = NewEngine(synth);

        for (var i = 0; i < 5; i++)
        {
            await engine.GenerateAsync(Request($"run {i}"), null, default);
            Assert.False(engine.Status.IsBusy);
            Assert.Equal(EngineState.Idle, engine.Status.State);
        }
    }

    [Fact]
    public async Task Starting_a_run_clears_the_previous_result()
    {
        // §2: nothing may offer to play the old audio while new audio is being
        // made, and a cancelled run leaves nothing to play rather than falling
        // back to the file from before.
        var synth = new FakeSynthesizer();
        await using var engine = NewEngine(synth);
        var first = await engine.GenerateAsync(Request(), null, default);
        Assert.NotNull(engine.LastOutputPath);

        synth.Gate = new SemaphoreSlim(0);
        var second = engine.GenerateAsync(Request(), null, default);
        await synth.Entered.Task;

        Assert.Null(engine.LastOutputPath);

        synth.Gate.Release();
        await second;
        // The earlier file is untouched on disk; only the reference was cleared.
        Assert.True(File.Exists(first.OutputPath));
    }

    [Fact]
    public async Task Waiting_for_idle_returns_true_once_the_run_has_stopped()
    {
        var synth = new FakeSynthesizer { Gate = new SemaphoreSlim(0) };
        await using var engine = NewEngine(synth);

        var run = engine.GenerateAsync(Request(), null, default);
        await synth.Entered.Task;

        var waiting = engine.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        engine.RequestStop();
        synth.Gate.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(await waiting);
    }

    [Fact]
    public async Task Waiting_for_idle_gives_up_rather_than_trapping_the_window()
    {
        // §9's timeout: a window must not be held open by an engine that will
        // not finish, which is why the confirmation says it closes anyway.
        var synth = new FakeSynthesizer { Gate = new SemaphoreSlim(0) };
        await using var engine = NewEngine(synth);

        var run = engine.GenerateAsync(Request(), null, default);
        await synth.Entered.Task;

        Assert.False(await engine.WaitForIdleAsync(TimeSpan.FromMilliseconds(50)));

        synth.Gate.Release();
        await run;
    }

    [Fact]
    public async Task Waiting_for_idle_when_nothing_is_running_returns_at_once()
    {
        await using var engine = NewEngine(new FakeSynthesizer());

        Assert.True(await engine.WaitForIdleAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task Stopping_when_nothing_is_running_does_nothing()
    {
        await using var engine = NewEngine(new FakeSynthesizer());

        engine.RequestStop();

        Assert.Equal(EngineState.Idle, engine.Status.State);
    }

    [Fact]
    public async Task A_run_reports_downloading_then_generating()
    {
        var synth = new FakeSynthesizer();
        await using var engine = NewEngine(synth);
        var states = new List<EngineState>();

        await engine.GenerateAsync(
            Request(), new Progress<EngineStatus>(s => { lock (states) states.Add(s.State); }), default);
        await Task.Delay(100);   // Progress<T> posts asynchronously

        lock (states)
        {
            Assert.Contains(EngineState.Downloading, states);
            Assert.Contains(EngineState.Generating, states);
        }
    }

    [Fact]
    public async Task A_model_already_loaded_is_not_loaded_again()
    {
        var synth = new FakeSynthesizer();
        await using var engine = NewEngine(synth);

        await engine.GenerateAsync(Request(), null, default);
        await engine.GenerateAsync(Request(), null, default);

        Assert.Equal(1, synth.Loads);
    }

    [Fact]
    public async Task A_model_that_produces_nothing_is_an_error_not_an_empty_file()
    {
        var synth = new FakeSynthesizer { Samples = [] };
        await using var engine = NewEngine(synth);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.GenerateAsync(Request(), null, default));
    }

    [Fact]
    public async Task A_style_that_was_not_applied_is_not_recorded_as_though_it_was()
    {
        // Whatever the reason a style was discarded — today it is the
        // preset-voice pipeline refusing one the model actually supports —
        // keeping it in the metadata would make the file claim a delivery it
        // never had, and send anyone reproducing it down the wrong path. The
        // same reason §1 refuses a clone model that ignores its transcript.
        var synth = new FakeSynthesizer { SupportsInstruct = false };
        await using var engine = NewEngine(synth);

        var result = await engine.GenerateAsync(
            Request() with { Instruct = "cheerful and quick" }, null, default);

        Assert.Null(WavMetadata.TryRead(result.OutputPath)!.Style);
        Assert.Contains(_log.Lines, l => l.Contains("style instruction was not applied"));
    }

    [Fact]
    public async Task A_style_that_was_applied_is_recorded()
    {
        var synth = new FakeSynthesizer { SupportsInstruct = true };
        await using var engine = NewEngine(synth);

        var result = await engine.GenerateAsync(
            Request() with { Instruct = "cheerful and quick" }, null, default);

        Assert.Equal("cheerful and quick", WavMetadata.TryRead(result.OutputPath)!.Style);
    }

    /// <summary>A synthesizer that does everything except run a model.</summary>
    private sealed class FakeSynthesizer : ISpeechSynthesizer
    {
        public SemaphoreSlim? Gate { get; set; }
        public Exception? Throw { get; set; }
        public short[] Samples { get; set; } = new short[24_000];
        public int Loads { get; private set; }
        public int Releases { get; private set; }
        public int SynthesizedOnThread { get; private set; }

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Speakers { get; } = ["ryan", "serena"];
        public bool IsLoaded { get; private set; }
        public bool SupportsInstruct { get; set; } = true;

        public Task LoadAsync(string modelFolder, CancellationToken ct)
        {
            Loads++;
            IsLoaded = true;
            return Task.CompletedTask;
        }

        public async Task<SynthesisResult> SynthesizeAsync(GenerateRequest request, CancellationToken ct)
        {
            SynthesizedOnThread = Environment.CurrentManagedThreadId;
            Entered.TrySetResult();

            if (Gate is not null) await Gate.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (Throw is not null) throw Throw;

            return new SynthesisResult(Samples, WavWriter.SampleRate, Samples.Length / 2_000);
        }

        public void ReleaseWorkingMemory() => Releases++;

        public Task UnloadAsync()
        {
            IsLoaded = false;
            Unloads++;
            return Task.CompletedTask;
        }

        public int Unloads { get; private set; }

        public ValueTask DisposeAsync()
        {
            Gate?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLog : ILogSink
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }
        public void Log(string message) { lock (_lines) _lines.Add(message); }
    }
}
