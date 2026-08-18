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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Xunit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Bunyi.App.Tests;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// Avalonia's headless session is process-global — one Application, one
// dispatcher, one visual world — and xunit runs test CLASSES in parallel by
// default. Two classes touching that single Application from two threads is
// exactly "the calling thread cannot access this object because a different
// thread owns it", which is how this suite failed in CI perhaps one run in
// three: on a different test each time, on both operating systems, including
// tests that open no window and one that only reads a resource.
//
// It never reproduced locally, which is the tell. A machine with more cores
// schedules the collections differently, and the race needs two classes to
// overlap at the wrong moment.
//
// These tests are milliseconds each, so serialising them costs nothing worth
// having. Core's tests are unaffected and still run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bunyi.App.Tests;

/// <summary>
/// The application the headless tests run inside.
/// </summary>
/// <remarks>
/// Not <see cref="Bunyi.App.App"/> itself, because that is the composition root:
/// it builds the real ONNX engine and audio device, neither of which belongs in
/// a test. It loads the same theme, the same brand resources and the same
/// control styles, in the same order, so what the tests exercise is the real
/// window as it is actually drawn.
/// </remarks>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());

        Resources.MergedDictionaries.Add(
            new ResourceInclude((Uri?)null) { Source = new Uri("avares://Bunyi.App/Themes/Brand.axaml") });

        // The app's own control styles, in the same order App.axaml adds them.
        // Without this the tests ran against bare Fluent, so anything the app
        // restyles — corner radii, icon buttons, centred button labels — was
        // invisible to them. That is the gap that let "Stop" sit against the
        // left edge of its button, and the dialog buttons ship square, with a
        // green suite either time.
        Styles.Add(new StyleInclude((Uri?)null)
        {
            Source = new Uri("avares://Bunyi.App/Themes/Controls.axaml"),
        });
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>An engine that reports whatever a test needs it to, and runs nothing.</summary>
public sealed class FakeEngine : ITtsEngine
{
    private EngineStatus _status = EngineStatus.Idle;

    public EngineStatus Status => _status;
    public event EventHandler<EngineStatus>? StatusChanged;
    public string? LastOutputPath { get; set; }
    public IReadOnlyList<string> Speakers { get; set; } = [];

    /// <summary>Held so a test can observe the busy window before it finishes.</summary>
    public TaskCompletionSource<GenerateResult> Pending { get; } = new();

    public GenerateRequest? LastRequest { get; private set; }
    public int StopRequests { get; private set; }

    public void ClearLastOutput() => LastOutputPath = null;

    public int Unloads { get; private set; }

    public Task UnloadAsync()
    {
        Unloads++;
        return Task.CompletedTask;
    }

    public Task<GenerateResult> GenerateAsync(
        GenerateRequest request, IProgress<EngineStatus>? progress, CancellationToken ct)
    {
        LastRequest = request;
        Publish(new EngineStatus(EngineState.Generating));
        return Pending.Task;
    }

    public void RequestStop()
    {
        StopRequests++;
        Publish(new EngineStatus(EngineState.Stopping));
    }

    public Task<bool> WaitForIdleAsync(TimeSpan timeout, CancellationToken ct = default) =>
        Task.FromResult(true);

    /// <summary>Drives the engine's state from a test.</summary>
    public void Publish(EngineStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Finishes the run the window is waiting on.</summary>
    public void Complete(string outputPath)
    {
        LastOutputPath = outputPath;
        Publish(EngineStatus.Idle);
        Pending.TrySetResult(new GenerateResult(outputPath, TimeSpan.FromSeconds(1), 12, TimeSpan.Zero));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A player that records what it was asked to do.</summary>
public sealed class FakePlayer : IAudioPlayer
{
    public List<string> Played { get; } = [];
    public bool IsPlaying { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(4);
    public string? CurrentPath { get; private set; }
    public event EventHandler? Finished;

    public void Play(string path)
    {
        Played.Add(path);
        CurrentPath = path;
        IsPlaying = true;
    }

    public void Stop()
    {
        IsPlaying = false;
        CurrentPath = null;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends the clip the way the real device does: on its own.</summary>
    public void RaiseFinished()
    {
        IsPlaying = false;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() { }
}

/// <summary>A log that keeps what it was told.</summary>
public sealed class RecordingLog : ILogSink
{
    private readonly List<string> _lines = [];
    public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }
    public void Log(string message) { lock (_lines) _lines.Add(message); }
}
