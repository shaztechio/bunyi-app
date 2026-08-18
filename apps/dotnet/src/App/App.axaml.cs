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
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var log = LogStore.Shared;
            var settingsStore = new SettingsStore(log);
            var settings = settingsStore.Load();

            ApplyAppearance(settings.Appearance);

            var probe = new SystemProbe();
            var downloader = new ModelDownloader(Http, log);

            // Doctor needs the same view of sources and folders the engine has,
            // so it is built from the same functions rather than a second copy.
            Task<DoctorReport> RunDoctor(TtsMode mode, bool deep, CancellationToken ct) =>
                Bunyi.Core.Diagnostics.Doctor.RunAsync(
                    mode,
                    ModelSource.Parse(settings.SourceFor(mode), DefaultSourceFor(mode)),
                    ModelLayout.PresetVoice,
                    settingsStore.ResolveModelsFolder(settings),
                    Bunyi.Core.Infrastructure.AppPaths.Outputs,
                    probe,
                    Reachable,
                    (folder, token) => downloader.VerifyAsync(
                        ModelSource.Parse(settings.SourceFor(mode), DefaultSourceFor(mode)),
                        ModelLayout.PresetVoice, folder, token),
                    deep,
                    ct);

            var engine = new OnnxTtsEngine(
                new QwenSpeechSynthesizer(log),
                downloader,
                log,
                mode => ModelSource.Parse(settings.SourceFor(mode), DefaultSourceFor(mode)),
                _ => ModelLayout.PresetVoice,
                () => settingsStore.ResolveModelsFolder(settings),
                () => Bunyi.Core.Infrastructure.AppPaths.Outputs,
                typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
                time: null,
                doctor: RunDoctor);

            var settingsViewModel = new SettingsViewModel(
                settingsStore,
                new ModelConfigLibrary(log),
                log,
                ApplyAppearance,
                DefaultSourceFor);

            var viewModel = new MainViewModel(engine, new SoundFlowAudioPlayer(log), log)
            {
                Settings = settingsViewModel,
                Doctor = RunDoctor,
            };

            // §3d: a model being deleted is evicted from memory first,
            // otherwise the app keeps generating from files that are gone — and
            // on Windows the delete simply fails, because a loaded session
            // holds its weights open.
            settingsViewModel.EvictLoadedModel = async () =>
            {
                engine.RequestStop();
                await engine.WaitForIdleAsync(TimeSpan.FromSeconds(15));
                await engine.UnloadAsync();
            };

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += async (_, _) => await engine.DisposeAsync();

            log.Log("Bunyi started.");
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Whether a source answers, for Doctor's reachability check.
    /// </summary>
    /// <remarks>
    /// A HEAD, and a short timeout of its own: the question is whether the
    /// server is there, and waiting the download client's thirty minutes to
    /// find out it is not would defeat the point of asking before the download.
    /// </remarks>
    private static async Task<bool> Reachable(Uri uri, CancellationToken ct)
    {
        try
        {
            using var quick = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await quick.SendAsync(request, ct);

            // Any answer means the server is alive. A 404 on one file is a
            // different problem, and the download reports it far better.
            return response.StatusCode != System.Net.HttpStatusCode.RequestTimeout;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
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
    private static string DefaultSourceFor(TtsMode mode) => mode switch
    {
        TtsMode.PresetVoice => "elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX",
        TtsMode.VoiceDesign => "wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX",
        TtsMode.VoiceClone => "wavekat/Qwen3-TTS-0.6B-Base-ONNX",
        _ => string.Empty,
    };
}
