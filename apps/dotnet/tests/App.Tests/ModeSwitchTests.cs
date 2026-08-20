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

using Bunyi.App.ViewModels;
using Bunyi.Core;
using Bunyi.Core.Engine;
using Bunyi.Core.Settings;
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>Letting go of a model when its mode is left (spec §3e).</summary>
public sealed class ModeSwitchTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "bunyi-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingLog _log = new();
    private readonly FakeEngine _engine = new();

    public ModeSwitchTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private MainViewModel NewModel(SettingsViewModel? settings = null) =>
        new(_engine, new FakePlayer(), _log) { Settings = settings };

    private SettingsViewModel NewSettings()
    {
        var store = new SettingsStore(_log, Path.Combine(_folder, "settings.json"));
        var configs = new ModelConfigLibrary(_log, Path.Combine(_folder, "configs.json"));
        return new SettingsViewModel(store, configs, _log, _ => { }, _ => string.Empty);
    }

    [Fact]
    public void Leaving_a_mode_lets_go_of_its_model()
    {
        // §3e. The engine would unload it anyway, but not until the next
        // generate — which is after that run's download, so the model nobody
        // wants would still be resident for the whole of the next one arriving.
        var model = NewModel(NewSettings());

        model.Mode = TtsMode.VoiceDesign;

        Assert.Equal(1, _engine.Unloads);
    }

    [Fact]
    public void The_setting_off_keeps_the_model_loaded()
    {
        // The trade §3e names: memory held, in exchange for coming back to that
        // mode without waiting for the load again.
        var settings = NewSettings();
        settings.UnloadOnModeSwitch = false;

        var model = NewModel(settings);
        model.Mode = TtsMode.VoiceDesign;

        Assert.Equal(0, _engine.Unloads);
    }

    [Fact]
    public void The_setting_is_on_to_begin_with()
    {
        Assert.True(NewSettings().UnloadOnModeSwitch);
    }

    [Fact]
    public void A_view_model_with_no_settings_window_still_lets_go()
    {
        // The behaviour is the app's, not the Settings window's. Reading the
        // setting as "unload unless turned off" is what keeps the two the same
        // when there is nothing to read it from.
        var model = NewModel();

        model.Mode = TtsMode.VoiceClone;

        Assert.Equal(1, _engine.Unloads);
    }

    [Fact]
    public void A_running_generation_keeps_its_model()
    {
        // The tabs are disabled during a run (§2a), so this should not be
        // reachable — but unloading a model out from under a running
        // generation is bad enough to refuse rather than to trust the view for.
        _engine.Publish(new EngineStatus(EngineState.Generating));

        var model = NewModel(NewSettings());
        model.Mode = TtsMode.VoiceDesign;

        Assert.Equal(0, _engine.Unloads);
    }

    [Fact]
    public void Choosing_the_mode_already_showing_keeps_its_model()
    {
        // Returning to the tab you are already on is not leaving anything, and
        // it is a click a user makes by accident. Throwing away a loaded model
        // for it would be a several-gigabyte reload for nothing.
        var model = NewModel(NewSettings());
        model.Mode = TtsMode.VoiceDesign;

        model.Mode = TtsMode.VoiceDesign;

        Assert.Equal(1, _engine.Unloads);
    }
}
