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
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Bunyi.Core.Audio;

public sealed record AudioPlaybackProgress(double PositionSeconds, double DurationSeconds);

/// <summary>Completes only after playback and audio-resource cleanup. Never loads a model.</summary>
public interface IFileAudioPlayback
{
    Task<double> PlayAsync(string path, IProgress<AudioPlaybackProgress>? progress, CancellationToken ct);
}

/// <summary>Strict, foreground playback for automation; unlike the GUI player, errors propagate.</summary>
public sealed class FileAudioPlayback : IFileAudioPlayback
{
    private readonly ILogSink log;
    private readonly Func<MiniAudioEngine> createEngine;

    public FileAudioPlayback(ILogSink log) : this(log, () => new MiniAudioEngine()) { }
    internal FileAudioPlayback(ILogSink log, Func<MiniAudioEngine> createEngine)
    {
        this.log = log;
        this.createEngine = createEngine;
    }

    public Task<double> PlayAsync(string path, IProgress<AudioPlaybackProgress>? progress, CancellationToken ct) => Task.Run(async () =>
    {
        ct.ThrowIfCancellationRequested();
        // Stream local audio instead of allocating an entire potentially long recording.
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var engine = createEngine();
        if (NativeAudioBackends.Name((int)engine.ActiveBackend) == "Null")
            throw new IOException("No usable audio output is available. Configure a default audio device and try again.");
        log.Log(NativeAudioBackends.Describe((int)engine.ActiveBackend));
        var format = new AudioFormat { Channels = 2, SampleRate = 48_000, Format = SampleFormat.F32 };
        using var source = new StreamDataProvider(engine, format, input);
        using var player = new SoundPlayer(engine, format, source);
        double duration = player.Duration;
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidDataException("The file contains no playable audio. Use a non-empty WAV, MP3, or FLAC file.");
        ct.ThrowIfCancellationRequested();
        using var device = engine.InitializePlaybackDevice(null, format);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ended(object? sender, EventArgs args) => finished.TrySetResult();
        player.PlaybackEnded += Ended;
        device.MasterMixer.AddComponent(player);
        try
        {
            device.Start();
            player.Play();
            progress?.Report(new(0, duration));
            var completion = finished.Task.WaitAsync(ct);
            while (!completion.IsCompleted)
            {
                await Task.WhenAny(completion, Task.Delay(250, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!completion.IsCompleted)
                {
                    if (!device.IsRunning) throw new IOException("The audio output stopped before playback finished.");
                    progress?.Report(new(Math.Clamp(player.Time, 0, duration), duration));
                }
            }
            await completion.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return duration;
        }
        finally
        {
            player.PlaybackEnded -= Ended;
            // Stop the device before dismantling components used by its native callback.
            device.Stop();
            player.Stop();
            device.MasterMixer.RemoveComponent(player);
        }
    }, CancellationToken.None);
}
