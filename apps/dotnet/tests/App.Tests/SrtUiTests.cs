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

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bunyi.App.ViewModels;
using Bunyi.App.Views;
using Bunyi.Core;
using Bunyi.Core.Settings;
using Xunit;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bunyi.App.Tests;

/// <summary>Headless controls and automation names only; not a Narrator/Orca listening test.</summary>
public sealed class SrtUiTests : HeadlessWindows
{
    [AvaloniaFact]
    public async Task Speed_controls_fit_preview_and_reset_cached_audio_without_generating()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bunyi-speed-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var project = Path.Combine(directory, "speed.bunyi-srt");
        try
        {
            var cues = new[]
            {
                new Bunyi.Core.Engine.SrtCue(Guid.NewGuid().ToString("N"), "1", "Hello.", "Hello.", "00:00:00,000", "00:00:00,800"),
                new Bunyi.Core.Engine.SrtCue(Guid.NewGuid().ToString("N"), "2", "Hello.", "Hello.", "00:00:01,000", "00:00:02,000")
            };
            byte[] raw;
            using (var memory = new MemoryStream())
            {
                using var writer = new BinaryWriter(memory);
                for (var i = 0; i < 28800; i++) writer.Write((float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 24000)));
                raw = memory.ToArray();
            }
            using (var zip = ZipFile.Open(project, ZipArchiveMode.Create))
            {
                foreach (var cue in cues) { using var stream = zip.CreateEntry($"clips/{cue.Id}.f32").Open(); stream.Write(raw); }
                using var manifest = zip.CreateEntry("project.json").Open();
                JsonSerializer.Serialize(manifest, new
                {
                    schemaVersion = 1, normalization = "srt-basic-v1", configuration = "{}",
                    voice = new { mode = 0, language = "english", speaker = "ryan", modelSource = "test-model" },
                    cues, clips = cues.Select(c => new { id = c.Id, key = "Hello.", samples = 28800, frames = 15, sha256 = Convert.ToHexString(SHA256.HashData(raw)) })
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
            var engine = new FakeEngine();
            var player = new FakePlayer();
            using var model = new MainViewModel(engine, player, new RecordingLog()) { Settings = EnabledSettings(Path.Combine(directory, "settings.json")), SrtSourceFor = _ => "test-model" };
            Open(new MainWindow { DataContext = model });
            model.ConfirmDiscardSrt = () => Task.FromResult(true);
            model.ChooseSrtProject = () => Task.FromResult<string?>(project);
            await model.OpenSrtProjectCommand.ExecuteAsync(null);
            var row = model.SrtRows[0];
            var previewBeforeDisable = row.Preview;
            model.Settings!.ExperimentalSrtEnabled = false;
            Assert.True(model.HasSrt);
            Assert.True(model.SrtDisabledWithProject);
            Assert.False(model.CanGenerate);
            await model.SpeedUpSrtCommand.ExecuteAsync(row);
            await model.GenerateCommand.ExecuteAsync(null);
            Assert.Equal(previewBeforeDisable, row.Preview);
            Assert.Null(engine.LastRequest);
            model.Settings.ExperimentalSrtEnabled = true;
            Assert.False(model.SrtDisabledWithProject);
            Assert.Equal(previewBeforeDisable, row.Preview);
            Assert.True(row.HasError);
            Assert.True(row.CanSpeedUp);
            var original = row.Preview;
            await model.SpeedUpSrtCommand.ExecuteAsync(row);
            Assert.Null(engine.LastRequest);
            Assert.False(row.HasError);
            Assert.True(row.HasSpeedAdjustment);
            Assert.False(row.CanSpeedUp);
            Assert.Contains("1.20×", row.SpeedDescription);
            Assert.NotEqual(original, row.Preview);
            Assert.Equal("Export track", model.GenerateActionText);
            model.PreviewSrtCommand.Execute(row);
            Assert.Equal(row.Preview, player.CurrentPath);
            model.ResetSrtSpeedCommand.Execute(row);
            Assert.True(row.HasError);
            Assert.False(row.HasSpeedAdjustment);
            Assert.Equal(original, row.Preview);
            Assert.Equal("Check timing", model.GenerateActionText);
            Assert.True(model.SrtUnsaved);
            model.SrtRows[1].Start = "00:00:00,500";
            Assert.False(row.CanSpeedUp);
            row.AllowSpeedLimitOverride = true;
            Assert.True(row.CanSpeedUp);
            Assert.Contains("unintelligible", row.SpeedDescription);
            await model.SpeedUpSrtCommand.ExecuteAsync(row);
            Assert.Contains("2.40×", row.SpeedDescription);
            Assert.False(row.HasError);
            Assert.Null(engine.LastRequest);
            row.AllowSpeedLimitOverride = false;
            Assert.Equal(original, row.Preview);
            Assert.True(row.HasError);
            await model.ClearSrtCommand.ExecuteAsync(null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Save_and_open_restore_voice_and_edits_and_guard_unsaved_drafts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bunyi-project-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "script.srt");
        var project = Path.Combine(directory, "script.bunyi-srt");
        try
        {
            await File.WriteAllTextAsync(source, "1\n00:00:01,000 --> 00:00:02,000\nOriginal words.");
            using var model = new MainViewModel(new FakeEngine(), new FakePlayer(), new RecordingLog())
            { Settings = EnabledSettings(Path.Combine(directory, "settings.json")), SrtSourceFor = _ => "test-model", Language = "english", Speaker = "aiden", Instruct = "calm" };
            Open(new MainWindow { DataContext = model });
            model.ConfirmDiscardSrt = () => Task.FromResult(true);
            model.ChooseSrt = () => Task.FromResult<string?>(source);
            await model.ImportSrtCommand.ExecuteAsync(null);
            Assert.True(model.SrtUnsaved);
            var savePickers = 0;
            model.ChooseSrtProjectSave = _ => { savePickers++; return Task.FromResult<string?>(project); };
            await model.SaveSrtProjectCommand.ExecuteAsync(null);
            Assert.False(model.SrtUnsaved);
            Assert.True(File.Exists(project));
            Assert.Contains("saved", model.SrtProjectLabel);
            model.RefreshModelNotice();
            Assert.False(model.SrtUnsaved); // Closing unchanged Settings must not clear takes.
            model.Speaker = "AIDEN";
            Assert.False(model.SrtUnsaved); // Model/UI capitalization is the same voice.
            model.SrtRows[0].End = "00:00:03,000";
            Assert.True(model.SrtUnsaved);
            await model.SaveSrtProjectCommand.ExecuteAsync(null);
            Assert.Equal(1, savePickers); // Save uses the established destination.
            model.SrtRows[0].Text = "Changed words.";
            model.Language = "french";
            model.Instruct = "quick";
            model.ChooseSrtProject = () => Task.FromResult<string?>(project);
            model.ConfirmDiscardSrt = () => Task.FromResult(false);
            await model.OpenSrtProjectCommand.ExecuteAsync(null);
            Assert.Equal("Changed words.", model.SrtRows[0].Text);
            Assert.True(model.SrtUnsaved);
            model.ConfirmDiscardSrt = () => Task.FromResult(true);
            await model.OpenSrtProjectCommand.ExecuteAsync(null);
            Assert.False(model.SrtUnsaved);
            Assert.Equal("Original words.", model.SrtRows[0].Text);
            Assert.Equal("00:00:03,000", model.SrtRows[0].End);
            Assert.Equal("english", model.Language);
            Assert.Equal("AIDEN", model.Speaker);
            Assert.Equal("calm", model.Instruct);
            await File.WriteAllTextAsync(project, "not a project");
            await model.OpenSrtProjectCommand.ExecuteAsync(null);
            Assert.Contains("Could not open", model.Status);
            Assert.Equal("Original words.", model.SrtRows[0].Text);
            Assert.False(model.SrtUnsaved);
            await model.ClearSrtCommand.ExecuteAsync(null);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [AvaloniaFact]
    public async Task Imported_cues_are_editable_flagged_excludable_and_unavailable_in_design()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".srt");
        try
        {
            await File.WriteAllTextAsync(file, "1\nbad --> 00:00:02,000\n<i>Hello.</i>\n\n2\n00:00:03,000 --> 00:00:04,000\n[music]");
            var engine = new FakeEngine();
            using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog()) { Settings = EnabledSettings(file + ".settings"), Script = "Ordinary text" };
            var window = Open(new MainWindow { DataContext = model });
            model.ConfirmDiscardSrt = () => Task.FromResult(true);
            model.ChooseSrt = () => Task.FromResult<string?>(file);
            await model.ImportSrtCommand.ExecuteAsync(null);
            Assert.True(model.HasSrt);
            Assert.Equal(2, model.SrtRows.Count);
            Assert.Equal("Hello.", model.SrtRows[0].SpokenText);
            Assert.Contains("non-speech", model.SrtRows[1].Flags);
            Assert.True(model.SrtRows[1].Included);
            Assert.True(model.SrtRows[0].HasError);
            await model.GenerateCommand.ExecuteAsync(null);
            Assert.Null(engine.LastRequest);
            Assert.Same(model.SrtRows[0], model.SelectedSrtCue);
            model.SrtRows[0].Start = "00:00:01,000";
            Assert.False(model.SrtRows[0].HasError);
            model.SrtRows[1].Included = false;
            Assert.Contains("Excluded", model.SrtRows[1].StatusText);
            model.Mode = TtsMode.VoiceDesign;
            Assert.False(model.HasSrt);
            Assert.False(model.CanImportSrt);
            Assert.Equal("Ordinary text", model.Script);
            model.Mode = TtsMode.VoiceClone;
            Assert.True(model.HasSrt);
            model.Mode = TtsMode.PresetVoice;
            var run = model.GenerateCommand.ExecuteAsync(null);
            Assert.True(model.IsBusy);
            Assert.NotNull(engine.LastRequest!.Srt);
            var table = window.GetLogicalDescendants().OfType<SrtCueTable>().Single();
            Assert.False(table.IsEffectivelyEnabled);
            engine.Complete("test.wav");
            await run;
            Assert.True(table.IsEffectivelyEnabled);
            await model.ClearSrtCommand.ExecuteAsync(null);
            Assert.False(model.HasSrt);
        }
        finally { File.Delete(file); File.Delete(file + ".settings"); }
    }
    private static SettingsViewModel EnabledSettings(string path)
    {
        var log = new RecordingLog();
        return new SettingsViewModel(new SettingsStore(log, path),
            new ModelConfigLibrary(log, path + ".configs"), log, _ => { }, _ => "test-model")
            { ExperimentalSrtEnabled = true };
    }

}
