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

using Avalonia.Headless.XUnit;
using Bunyi.App.ViewModels;
using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.App.Tests;

public sealed class CompletedRecordingTests : HeadlessWindows
{
    [AvaloniaTheory]
    [InlineData(TtsMode.PresetVoice)]
    [InlineData(TtsMode.VoiceDesign)]
    [InlineData(TtsMode.VoiceClone)]
    public async Task Every_mode_waits_for_the_complete_recording_then_plays_once(TtsMode mode)
    {
        var engine = new FakeEngine();
        var player = new FakePlayer();
        using var model = new MainViewModel(engine, player, new RecordingLog())
        {
            Mode = mode, Script = string.Join(' ', Enumerable.Repeat("word", 100)),
            Instruct = "A calm voice", ReferenceAudioPath = "reference.wav", ReferenceTranscript = "Hello"
        };
        Assert.Contains("generated in shorter sections", model.SpeechEstimateText);
        var pending = model.GenerateCommand.ExecuteAsync(null);
        engine.Publish(new(EngineState.Generating, Detail: "Section 2 of 4 · 18 seconds ready", Frames: 225));
        Assert.Empty(player.Played);
        engine.Publish(new(EngineState.Finalizing));
        Assert.Empty(player.Played);
        engine.Complete("complete.wav");
        await pending;
        Assert.Equal("complete.wav", Assert.Single(player.Played));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_generation_never_plays_a_partial_recording(bool cancel)
    {
        var engine = new FakeEngine();
        var player = new FakePlayer();
        using var model = new MainViewModel(engine, player, new RecordingLog())
            { Script = string.Join(' ', Enumerable.Repeat("word", 100)) };
        var pending = model.GenerateCommand.ExecuteAsync(null);
        engine.Publish(new(EngineState.Generating, Frames: 300));
        if (cancel) { model.StopCommand.Execute(null); engine.Pending.TrySetCanceled(); }
        else engine.Pending.TrySetException(new InvalidOperationException("Section failed"));
        await pending;
        Assert.Empty(player.Played);
        Assert.Null(model.LastOutputPath);
    }
}
