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
using Bunyi.Core.Engine;
using SoundFlow.Abstracts;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Enums;
using SoundFlow.Structs;

namespace Bunyi.Core.Audio;

/// <summary>A single generation's preview. Add runs on the inference worker, never the UI thread.</summary>
public interface IStreamingAudioPlayer : IDisposable
{
    bool IsBuffering { get; }
    string? Failure { get; }
    void Add(AudioPreviewChunk chunk, CancellationToken cancellationToken);
    Task CompleteAsync(CancellationToken cancellationToken);
    void Stop();
}

/// <summary>
/// A bounded mono PCM queue with a nonblocking audio callback. Silence means
/// underrun, not end of stream; only CompleteAsync marks the producer finished.
/// </summary>
public sealed class StreamingAudioPlayer(ILogSink log) : IStreamingAudioPlayer
{
    private const int SampleRate = 24000;
    private const int TailSamples = 256;
    private const int PrebufferSamples = SampleRate * 2 - TailSamples;
    private readonly float[] _ring = new float[SampleRate * 10];
    private readonly object _deviceGate = new();
    private long _written;
    private long _read;
    private long _expectedOffset;
    private int _stopped;
    private int _complete;
    private int _buffering = 1;
    private MiniAudioEngine? _engine;
    private AudioPlaybackDevice? _device;
    private PreviewSource? _source;
    private string? _failure;
    private long _lastRead;
    private long _firstPlaybackTimestamp;
    private long _lastReadAt = Environment.TickCount64;
    private float _gain = 1f;
    // Hold about 10 ms so a newly discovered louder chunk can lower the gain
    // smoothly before its boundary. Gain never rises or amplifies quiet PCM.
    private float[] _tail = [];

    public bool IsBuffering => Volatile.Read(ref _buffering) != 0;
    public string? Failure => Volatile.Read(ref _failure);
    /// <summary>Stopwatch timestamp of the first device callback consuming PCM; zero until then.</summary>
    public long FirstPlaybackTimestamp => Interlocked.Read(ref _firstPlaybackTimestamp);

    public void Add(AudioPreviewChunk chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _stopped) != 0) return;
        if (chunk.Failure is { } failure)
        {
            Fail(failure);
            return;
        }
        if (chunk.SampleRate != SampleRate || chunk.SampleOffset != _expectedOffset)
            throw new InvalidDataException("Streaming audio chunks must be contiguous 24 kHz mono samples.");
        var peak = 0f;
        foreach (var sample in chunk.Samples)
        {
            if (!float.IsFinite(sample)) throw new InvalidDataException("Streaming audio contains a non-finite sample.");
            peak = Math.Max(peak, Math.Abs(sample));
        }
        _expectedOffset += chunk.Samples.Length;
        var gain = Math.Min(_gain, peak > .95f ? .95f / peak : 1f);
        if (Failure is not null) return;
        if (!EnsureDevice()) return;
        if (_tail.Length > 0)
        {
            var ratio = gain / _gain;
            for (var i = 0; i < _tail.Length; i++)
                _tail[i] *= 1f + (ratio - 1f) * (i + 1f) / _tail.Length;
            Write(_tail, 1f, cancellationToken);
        }
        var held = Math.Min(TailSamples, chunk.Samples.Length);
        Write(chunk.Samples.AsSpan(0, chunk.Samples.Length - held), gain, cancellationToken);
        _tail = chunk.Samples.AsSpan(chunk.Samples.Length - held).ToArray();
        for (var i = 0; i < _tail.Length; i++) _tail[i] *= gain;
        _gain = gain;
    }

    private void Write(ReadOnlySpan<float> samples, float gain, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopped) != 0 || Failure is not null) return;
            var write = Volatile.Read(ref _written);
            var free = _ring.Length - (int)(write - Volatile.Read(ref _read));
            if (free == 0)
            {
                CheckDeviceProgress();
                if (cancellationToken.WaitHandle.WaitOne(10)) cancellationToken.ThrowIfCancellationRequested();
                continue;
            }
            var count = Math.Min(free, samples.Length - offset);
            if (write - Volatile.Read(ref _read) < PrebufferSamples) _lastReadAt = Environment.TickCount64;
            for (var i = 0; i < count; i++)
                _ring[(int)((write + i) % _ring.Length)] = samples[offset + i] * gain;
            Volatile.Write(ref _written, write + count);
            offset += count;
        }
    }

    private bool EnsureDevice()
    {
        lock (_deviceGate)
        {
            if (Volatile.Read(ref _stopped) != 0) return false;
            if (_device is not null) return true;
            try
            {
                _engine = new MiniAudioEngine();
                var format = new AudioFormat { Channels = 1, SampleRate = SampleRate, Format = SampleFormat.F32 };
                _device = _engine.InitializePlaybackDevice(null, format);
                _source = new PreviewSource(_engine, format, this);
                _device.MasterMixer.AddComponent(_source);
                _device.Start();
                _lastReadAt = Environment.TickCount64;
                return true;
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                return false;
            }
        }
    }

    private void Read(Span<float> buffer, int channels)
    {
        buffer.Clear();
        if (Volatile.Read(ref _stopped) != 0 || Failure is not null || channels < 1) return;
        var read = Volatile.Read(ref _read);
        var available = Volatile.Read(ref _written) - read;
        if (IsBuffering && available < PrebufferSamples && Volatile.Read(ref _complete) == 0) return;
        var count = (int)Math.Min(available, buffer.Length / channels);
        if (count > 0)
            Interlocked.CompareExchange(ref _firstPlaybackTimestamp, System.Diagnostics.Stopwatch.GetTimestamp(), 0);
        for (var frame = 0; frame < count; frame++)
        {
            var value = _ring[(int)((read + frame) % _ring.Length)];
            for (var channel = 0; channel < channels; channel++) buffer[frame * channels + channel] = value;
        }
        Volatile.Write(ref _read, read + count);
        Volatile.Write(ref _buffering, count < buffer.Length / channels ? 1 : 0);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _complete, 1);
        // The inference producer is finished; flush its held tail off the UI
        // thread because a full ring can backpressure this last write too.
        await Task.Run(() => Write(_tail, 1f, cancellationToken), cancellationToken).ConfigureAwait(false);
        _tail = [];
        _lastReadAt = Environment.TickCount64;
        while (Volatile.Read(ref _stopped) == 0 && Failure is null && Volatile.Read(ref _read) < Volatile.Read(ref _written))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckDeviceProgress();
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        // The last callback has handed these samples to the device; let its
        // short hardware buffer finish before disposing the output.
        if (Volatile.Read(ref _stopped) == 0 && Failure is null)
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
    }

    private void CheckDeviceProgress()
    {
        var read = Volatile.Read(ref _read);
        if (read != _lastRead) { _lastRead = read; _lastReadAt = Environment.TickCount64; }
        else if (Environment.TickCount64 - _lastReadAt > 5000)
            Fail("The audio device stopped consuming preview audio.");
    }

    private void Fail(string detail)
    {
        if (Interlocked.CompareExchange(ref _failure, detail, null) is null)
            log.Log($"Streaming preview unavailable: {detail}. Generation will still save the audio file.");
    }

    public void Stop() => Interlocked.Exchange(ref _stopped, 1);

    public void Dispose()
    {
        Stop();
        lock (_deviceGate)
        {
            try
            {
                if (_source is not null) _device?.MasterMixer.RemoveComponent(_source);
                _source?.Dispose();
                _device?.Dispose();
                _engine?.Dispose();
            }
            catch (Exception ex) { log.Log($"Could not close streaming playback: {ex.Message}"); }
            _source = null;
            _device = null;
            _engine = null;
        }
    }

    private sealed class PreviewSource(AudioEngine engine, AudioFormat format, StreamingAudioPlayer owner)
        : SoundComponent(engine, format)
    {
        protected override void GenerateAudio(Span<float> buffer, int channels) => owner.Read(buffer, channels);
    }
}
