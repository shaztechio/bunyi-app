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
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Enums;
using Xunit;

namespace Bunyi.Core.Tests;

public sealed class FileAudioPlaybackTests
{
    [Fact]
    public async Task NullBackendIsAnErrorInsteadOfSilentSuccess()
    {
        var path = Path.GetTempFileName();
        try
        {
            WavWriter.Write(path, new short[2400]);
            var player = new FileAudioPlayback(new SilentLog(), () => new MiniAudioEngine([(MiniAudioBackend)14]));
            var failure = await Assert.ThrowsAsync<IOException>(() => player.PlayAsync(path, null, default));
            Assert.Contains("No usable audio output", failure.Message);
            // Even the failure path releases its input handle.
            using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PreCancelledPlaybackNeverOpensAnAudioEngine()
    {
        var initialized = false;
        var player = new FileAudioPlayback(new SilentLog(), () => { initialized = true; return new MiniAudioEngine(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.PlayAsync("unused.wav", null, new CancellationToken(true)));
        Assert.False(initialized);
    }

    private sealed class SilentLog : ILogSink { public void Log(string message) { } }
}
