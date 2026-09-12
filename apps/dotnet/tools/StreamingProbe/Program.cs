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

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Qwen;
using Bunyi.Core.Models;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: StreamingProbe reference|preset|design|clone MODELS_PARENT OUTPUT_FOLDER [--play]");
    return 2;
}
var mode = args[0];
var modelRoot = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
const string referenceText = "Hello! We'll begin in just a few minutes.";
const string text = "Welcome to this demonstration of streaming speech. You can hear the opening while the next part is still being generated. The voice continues in one uninterrupted take, keeping its rhythm and expression throughout the passage. When the recording is complete, the application saves the whole result so you can listen again whenever you like.";
var estimate = SpeechDurationEstimate.ForText(mode == "reference" ? referenceText : text, "english");
var log = new ProbeLog();
if (args.Contains("--sections"))
    return await SectionProbe.Run(mode, modelRoot, output, args.Contains("--play"), log);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
using IStreamingAudioPlayer? player = args.Contains("--play") ? new StreamingAudioPlayer(log) : null;
player?.ConfigureBuffer(estimate.UpperSeconds);
var chunks = new List<float[]>();
var chunkTimes = new List<double>();
long sampleOffset = 0;
var clock = Stopwatch.StartNew();
var startedTimestamp = Stopwatch.GetTimestamp();
double firstChunk = -1;
void Preview(AudioPreviewChunk chunk)
{
    if (chunk.Failure is not null) throw new InvalidDataException(chunk.Failure);
    if (chunk.SampleRate != 24_000 || chunk.SampleOffset != sampleOffset)
        throw new InvalidDataException("Preview sample sequence is not contiguous.");
    if (firstChunk < 0) firstChunk = clock.Elapsed.TotalSeconds;
    chunks.Add(chunk.Samples);
    chunkTimes.Add(clock.Elapsed.TotalSeconds);
    sampleOffset += chunk.Samples.Length;
    Console.WriteLine($"PREVIEW chunk={chunks.Count} seconds={sampleOffset / 24000.0:F2} elapsed={clock.Elapsed.TotalSeconds:F2}");
    player?.Add(chunk, cancellation.Token);
    player?.ReportGeneratedSeconds(sampleOffset / 24000.0);
    if (player is not null)
        Console.WriteLine($"BUFFER ready={player.BufferedSeconds:F2} target={player.BufferTargetSeconds:F0} playing={player.HasStarted && !player.IsBuffering}");
}

try
{
    SpeechResult result;
    var random = new Random(7);
    if (mode is "preset" or "reference")
    {
        using var pipeline = new PresetPipeline(Path.Combine(modelRoot, "models.bunyi.app-onnx-customvoice"), log,
            sampler: new TokenSampler(random.NextDouble), provider: ExecutionProviderChoice.Cpu);
        result = pipeline.Generate(new PresetRequest(mode == "reference" ? referenceText : text, "ryan", null, "english"),
            ct: cancellation.Token, audioPreview: mode == "reference" || !estimate.ShouldStream ? null : Preview);
    }
    else if (mode == "design")
    {
        using var pipeline = new DesignPipeline(Path.Combine(modelRoot, "models.bunyi.app-onnx-voicedesign"), "int4", log,
            sampler: new TokenSampler(random.NextDouble), provider: ExecutionProviderChoice.Cpu);
        result = pipeline.Generate(new DesignRequest(text, "A calm male narrator with a warm, clear voice.", "english"),
            ct: cancellation.Token, audioPreview: estimate.ShouldStream ? Preview : null);
    }
    else if (mode == "clone")
    {
        var raw = File.ReadAllBytes(Path.Combine(output, "reference.f32"));
        var reference = MemoryMarshal.Cast<byte, float>(raw).ToArray();
        if (reference.Length > ClonePipeline.ReferenceSamples)
            throw new InvalidDataException("Reference exceeds ten seconds; do not silently truncate its transcript.");
        using var pipeline = new ClonePipeline(Path.Combine(modelRoot, "models.bunyi.app-onnx-voiceclone"), "int4", log,
            sampler: new TokenSampler(random.NextDouble), provider: ExecutionProviderChoice.Cpu);
        result = pipeline.Generate(new CloneRequest(text, referenceText, "english"), reference,
            ct: cancellation.Token, audioPreview: estimate.ShouldStream ? Preview : null);
    }
    else throw new ArgumentException("Unknown mode.");
    var synthesisSeconds = clock.Elapsed.TotalSeconds;
    if (player is not null) await player.CompleteAsync(cancellation.Token);
    var preview = chunks.SelectMany(x => x).ToArray();
    WavWriter.Write(Path.Combine(output, mode + "-final.wav"), Pcm16(result.Samples));
    if (mode == "reference") File.WriteAllBytes(Path.Combine(output, "reference.f32"), MemoryMarshal.AsBytes(result.Samples.AsSpan()).ToArray());
    else
    {
        if (preview.Length != result.Samples.Length || firstChunk < 0 || firstChunk >= synthesisSeconds)
            throw new InvalidDataException("No complete early preview, or preview and final sample lengths differ.");
        WavWriter.Write(Path.Combine(output, mode + "-preview.wav"), Pcm16(preview));
    }
    double mse = 0, signal = 0, maxError = 0;
    for (var i = 0; i < preview.Length; i++)
    {
        var error = (double)preview[i] - result.Samples[i];
        mse += error * error;
        signal += (double)result.Samples[i] * result.Samples[i];
        maxError = Math.Max(maxError, Math.Abs(error));
    }
    var report = new
    {
        mode, text = mode == "reference" ? referenceText : text, estimate,
        frames = result.Frames, audioSeconds = result.Duration(24000).TotalSeconds,
        firstChunkSeconds = firstChunk, synthesisSeconds,
        firstDeviceReadSeconds = player is StreamingAudioPlayer live && live.FirstPlaybackTimestamp > 0
            ? (live.FirstPlaybackTimestamp - startedTimestamp) / (double)Stopwatch.Frequency : (double?)null,
        playbackCompletedSeconds = clock.Elapsed.TotalSeconds,
        previewChunks = chunks.Count, previewSamples = preview.Length, finalSamples = result.Samples.Length,
        rootMeanSquareError = preview.Length > 0 ? Math.Sqrt(mse / preview.Length) : 0,
        relativeRootMeanSquareError = signal > 0 ? Math.Sqrt(mse / signal) : 0,
        maximumAbsoluteError = maxError, chunkArrivalSeconds = chunkTimes,
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
        playerFailure = player?.Failure,
        note = "CPU; same generated codec sequence, rolling preview versus authoritative full decoder. Preview WAV uses one gain for comparison; live preview has its own clipping protection."
    };
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(output, mode + "-metrics.json"), json);
    Console.WriteLine(json);
    return player?.Failure is null ? 0 : 1;
}
catch (Exception ex)
{
    player?.Stop();
    Console.Error.WriteLine(ex);
    return 1;
}

static short[] Pcm16(float[] samples)
{
    if (samples.Any(s => !float.IsFinite(s))) throw new InvalidDataException("Non-finite PCM.");
    var peak = samples.Length > 0 ? samples.Max(s => Math.Abs((double)s)) : 0;
    var gain = peak > 1 ? .98 / peak : 1;
    return samples.Select(s => (short)Math.Round(s * gain * short.MaxValue)).ToArray();
}

sealed class ProbeLog : ILogSink
{
    public void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
}
