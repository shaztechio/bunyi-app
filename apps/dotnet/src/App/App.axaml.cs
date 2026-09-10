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
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Qwen;
using Bunyi.Core.Transcription;
using Bunyi.Core.Engine;
using Bunyi.Core.Models;
using Bunyi.Core.Settings;

namespace Bunyi.App;

/// <summary>
/// The composition root: the one place that builds everything and wires it
/// together.
/// </summary>
public partial class App : Application
{

    public override void Initialize()
    {
        // Avalonia has windowing and rendering up by the time it calls this. On
        // Linux that span is X11, the GL probe, DBus and the font manager — the
        // phase most likely to be the slow one, and the one this app cannot see
        // from anywhere later.
        Program.Startup?.Mark("platform");

        AvaloniaXamlLoader.Load(this);

        // App.axaml pulls in the Fluent theme and this app's own dictionaries.
        // Parsing them is real work, and not the same work as the line above.
        Program.Startup?.Mark("theme");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = LogStore.Shared;
            Infrastructure.LinuxAccessibilityFocus.Install(log);
            if (OperatingSystem.IsLinux()) Styles.Add(Infrastructure.LinuxAccessibilityPresentation.CreateStyles());
            var runtime = new Bunyi.Core.Runtime.BunyiRuntime(log);
            var settingsStore = runtime.Settings;
            var settings = runtime.CurrentSettings;
            var engine = runtime.Engine;
            try
            {
                using var migration = runtime.AcquireOperation("models.migrate");
                LegacyPaths.MoveMisplacedWhisper(runtime.ModelsRoot, log);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log.Log("Model migration deferred: " + ex.Message); }
            ApplyAppearance(settings.Appearance);
            Task<DoctorReport> RunDoctor(TtsMode mode, bool deep, CancellationToken ct) =>
                runtime.DoctorAsync(mode, deep, ct);

            var settingsViewModel = new SettingsViewModel(
                settingsStore,
                new ModelConfigLibrary(log),
                log,
                ApplyAppearance,
                DefaultSourceFor);

            var viewModel = new MainViewModel(
                engine, new SoundFlowAudioPlayer(log), log,
                voices: new VoiceLibrary(log))
            {
                Settings = settingsViewModel,
                Doctor = RunDoctor,
                Logs = new LogsViewModel(log),
            };

            viewModel.Transcribe = (path, ct) => runtime.TranscribeAsync(path, viewModel.Language, null, ct);
            settingsViewModel.AcquireOperation = runtime.AcquireOperation;

            // §3d: a model being deleted is evicted from memory first,
            // otherwise the app keeps generating from files that are gone — and
            // on Windows the delete simply fails, because a loaded session
            // holds its weights open.
            settingsViewModel.EvictLoadedModel = async () =>
            {
                engine.RequestStop();
                if (!await engine.WaitForIdleAsync(TimeSpan.FromSeconds(15)))
                    throw new EngineBusyException(engine.Status.State);
                await engine.UnloadAsync();
            };

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += async (_, _) => await runtime.DisposeAsync();

            log.Log("Bunyi started.");

            Program.Startup?.Mark("app");
            ReportStartupOnFirstFrame(desktop.MainWindow, log);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Writes the startup line once the first window has been drawn (spec §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two phases, because they answer different questions and the first Linux
    /// reading is why. <see cref="Window.Opened"/> fires when the window is
    /// shown, which is before anything has been rendered into it: that is
    /// <c>show</c>, and on X11 it is the window being mapped and the round
    /// trips that go with it. <c>first frame</c> is what happens after — the
    /// render surface, and the first pass that puts pixels in the window.
    /// </para>
    /// <para>
    /// Together they were 696 ms on Linux against 254 ms on Windows: the only
    /// phase of the five where Linux was slower, and the gap between a window
    /// appearing and a window having anything in it is exactly what reads as a
    /// slow start. One number could not say which half of it was the problem.
    /// </para>
    /// <para>
    /// The post is at a priority below Render, so it runs after the first
    /// layout and render pass rather than merely after they were queued — as
    /// close to "there is something to look at" as the dispatcher can answer.
    /// </para>
    /// </remarks>
    private static void ReportStartupOnFirstFrame(Window window, ILogSink log)
    {
        void Drawn(object? sender, EventArgs e)
        {
            window.Opened -= Drawn;
            Program.Startup?.Mark("show");

            Dispatcher.UIThread.Post(
                () => Program.Startup?.Report(log), DispatcherPriority.Background);
        }

        window.Opened += Drawn;
    }

    /// <summary>
    /// Applies the appearance to every window the app owns (spec §7).
    /// </summary>
    private void ApplyAppearance(Appearance appearance) =>
        RequestedThemeVariant = appearance switch
        {
            Appearance.Light => ThemeVariant.Light,
            Appearance.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,   // System
        };

    /// <summary>
    /// The built-in source for a mode when Settings leaves it blank (spec §3a).
    /// </summary>
    private static string DefaultSourceFor(TtsMode mode) => Bunyi.Core.Runtime.BunyiRuntime.DefaultSourceFor(mode);
}
