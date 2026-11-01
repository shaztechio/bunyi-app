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
using Xunit;

namespace Bunyi.App.Tests;

/// <summary>Headless controls and automation names only; not a Narrator/Orca listening test.</summary>
public sealed class SrtUiTests : HeadlessWindows
{
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
            { SrtSourceFor = _ => "test-model", Language = "english", Speaker = "aiden", Instruct = "calm" };
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
            using var model = new MainViewModel(engine, new FakePlayer(), new RecordingLog()) { Script = "Ordinary text" };
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
        finally { File.Delete(file); }
    }
}
