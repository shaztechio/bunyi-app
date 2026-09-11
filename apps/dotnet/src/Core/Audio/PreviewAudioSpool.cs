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

using System.Runtime.InteropServices;

namespace Bunyi.Core.Audio;

/// <summary>Single producer/single reader preview cache. No file access on the device callback.</summary>
internal sealed class PreviewAudioSpool : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bunyi-preview-{Guid.NewGuid():N}.pcm");
    private readonly FileStream _writer;
    private readonly FileStream _reader;
    private long _samplesWritten;
    private long _samplesRead;
    internal long SamplesWritten => Interlocked.Read(ref _samplesWritten);

    internal PreviewAudioSpool()
    {
        _writer = new FileStream(Path, FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            _reader = new FileStream(Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        }
        catch { _writer.Dispose(); throw; }
    }

    internal void Append(ReadOnlySpan<float> samples, float gain, CancellationToken ct)
    {
        Span<float> block = stackalloc float[4096];
        var offset = 0;
        while (offset < samples.Length)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(block.Length, samples.Length - offset);
            for (var i = 0; i < count; i++) block[i] = samples[offset + i] * gain;
            _writer.Write(MemoryMarshal.AsBytes(block[..count]));
            offset += count;
        }
        _writer.Flush();
        Interlocked.Add(ref _samplesWritten, samples.Length);
    }

    internal int Read(Span<float> destination)
    {
        var count = (int)Math.Min(destination.Length, SamplesWritten - _samplesRead);
        if (count == 0) return 0;
        _reader.ReadExactly(MemoryMarshal.AsBytes(destination[..count]));
        _samplesRead += count;
        return count;
    }

    public void Dispose()
    {
        try { _reader.Dispose(); }
        finally { _writer.Dispose(); }
    }
}
