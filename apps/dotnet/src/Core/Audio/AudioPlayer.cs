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
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Bunyi.Core.Audio;

/// <summary>Plays a generated clip (spec §2).</summary>
/// <remarks>
/// Play and stop, with no pause. §2a gives the reasoning for History's rows and
/// it applies here too: these are short clips, and a paused state is a third
/// thing to explain for something a user would nearly always just play again.
/// </remarks>
public interface IAudioPlayer : IDisposable
{
    /// <summary>Whether audio is playing now.</summary>
    bool IsPlaying { get; }

    /// <summary>How far into the clip playback has reached.</summary>
    /// <remarks>
    /// §2a draws progress as a ring around the play button rather than a
    /// separate bar — the control and its progress are the same object, which
    /// is what a list row has space for. Taken from the player rather than a
    /// timer started alongside it, so a clip that stalls does not leave the ring
    /// advancing over audio that is not moving.
    /// </remarks>
    TimeSpan Position { get; }

    /// <summary>The clip's length, or zero when nothing is loaded.</summary>
    TimeSpan Duration { get; }

    /// <summary>The file currently loaded, or null.</summary>
    string? CurrentPath { get; }

    /// <summary>Raised when playback finishes or is stopped, on any thread.</summary>
    event EventHandler? Finished;

    /// <summary>Plays a file from the start, replacing anything already playing.</summary>
    void Play(string path);

    /// <summary>Stops playback. Safe to call when nothing is playing.</summary>
    void Stop();
}

/// <summary>
/// Playback through miniaudio, which works the same on Windows and Linux.
/// </summary>
/// <remarks>
/// The alternatives were Windows-only: <c>System.Media</c> and NAudio's output
/// backends both are, and apps/dotnet/AGENTS.md rules them out for that reason.
/// A failure to play is logged and swallowed — an audio device that will not
/// open is a disappointment, not a reason to take down the window showing the
/// file the user just made.
/// </remarks>
public sealed class SoundFlowAudioPlayer : IAudioPlayer
{
    private readonly ILogSink _log;
    private readonly object _gate = new();

    private MiniAudioEngine? _engine;
    private AudioPlaybackDevice? _device;
    private SoundPlayer? _player;
    private string? _currentPath;

    public SoundFlowAudioPlayer(ILogSink log) => _log = log ?? throw new ArgumentNullException(nameof(log));

    /// <inheritdoc />
    public bool IsPlaying
    {
        get { lock (_gate) return _player?.State == PlaybackState.Playing; }
    }

    /// <inheritdoc />
    public TimeSpan Position
    {
        get { lock (_gate) return TimeSpan.FromSeconds(_player?.Time ?? 0); }
    }

    /// <inheritdoc />
    public TimeSpan Duration
    {
        get { lock (_gate) return TimeSpan.FromSeconds(_player?.Duration ?? 0); }
    }

    /// <inheritdoc />
    public string? CurrentPath
    {
        get { lock (_gate) return _currentPath; }
    }

    /// <inheritdoc />
    public event EventHandler? Finished;

    /// <inheritdoc />
    public void Play(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            lock (_gate)
            {
                StopInternal();

                _engine ??= new MiniAudioEngine();

                var format = new AudioFormat
                {
                    Channels = WavWriter.Channels,
                    SampleRate = WavWriter.SampleRate,
                    Format = SampleFormat.S16,
                };

                _device ??= _engine.InitializePlaybackDevice(null, format);

                // Read the file into memory first rather than streaming it from
                // disk. A clip is seconds long and a few hundred kilobytes, and
                // a disk read mid-playback is what makes audio break up a second
                // after a generation finishes.
                var bytes = File.ReadAllBytes(path);
                var source = new StreamDataProvider(_engine, format, new MemoryStream(bytes));

                _player = new SoundPlayer(_engine, format, source);
                _player.PlaybackEnded += OnPlaybackEnded;

                _device.MasterMixer.AddComponent(_player);
                _device.Start();
                _player.Play();
                _currentPath = path;
            }
        }
        catch (Exception ex)
        {
            _log.Log($"Could not play {Path.GetFileName(path)}: {ex.Message}");
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate) StopInternal();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackEnded(object? sender, EventArgs e) =>
        Finished?.Invoke(this, EventArgs.Empty);

    private void StopInternal()
    {
        if (_player is null) return;

        try
        {
            _player.PlaybackEnded -= OnPlaybackEnded;
            _player.Stop();
            _device?.MasterMixer.RemoveComponent(_player);
        }
        catch (Exception ex)
        {
            _log.Log($"Could not stop playback cleanly: {ex.Message}");
        }
        finally
        {
            _player = null;
            _currentPath = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopInternal();
            _device?.Dispose();
            _device = null;
            _engine?.Dispose();
            _engine = null;
        }
    }
}
