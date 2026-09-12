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
using Bunyi.Core;
using Bunyi.Core.Audio;
using Bunyi.Core.Diagnostics;
using Bunyi.Core.Engine;
using Bunyi.Core.Models;
using Bunyi.Core.Qwen;

internal static class SectionProbe
{
    public static async Task<int> Run(string mode, string modelParent, string output, bool play, ILogSink log)
    {
        const string script = "The morning light filled the room as we prepared to leave for the station. " +
            "Outside, a gentle breeze moved through the trees and carried the sound of birds across the garden. " +
            "We checked our bags once more, made sure the windows were closed, and left a note on the kitchen table. " +
            "There was still plenty of time before the train arrived, so we decided to walk along the river. " +
            "At the bridge, we stopped for a moment to watch the sunlight dancing on the water before continuing our journey.";
        var selected = mode switch { "preset" => TtsMode.PresetVoice, "design" => TtsMode.VoiceDesign, "clone" => TtsMode.VoiceClone, _ => throw new ArgumentException("Unknown section mode") };
        var random = new Random(7);
        var synths = new Dictionary<TtsMode, ISpeechSynthesizer>
        {
            [TtsMode.PresetVoice] = new PresetSpeechSynthesizer(log, (f, l) => new PresetPipeline(f, l, new TokenSampler(random.NextDouble), ExecutionProviderChoice.Cpu)),
            [TtsMode.VoiceDesign] = new DesignSpeechSynthesizer(log, open: (f, v, l) => new DesignPipeline(f, v, l, new TokenSampler(random.NextDouble), ExecutionProviderChoice.Cpu)),
            [TtsMode.VoiceClone] = new CloneSpeechSynthesizer(log, open: (f, v, l) => new ClonePipeline(f, v, l, new TokenSampler(random.NextDouble), ExecutionProviderChoice.Cpu))
        };
        ModelSource Source(TtsMode m) => new ModelSource.BaseUrl(new Uri("https://models.bunyi.app/onnx/" +
            (m == TtsMode.PresetVoice ? "customvoice" : m == TtsMode.VoiceDesign ? "voicedesign" : "voiceclone")));
        using var http = new HttpClient();
        var root = Directory.GetParent(Directory.GetParent(modelParent)!.FullName)!.FullName;
        await using var engine = new OnnxTtsEngine(m => synths[m], new ModelDownloader(http, log), log, Source,
            m => m == TtsMode.PresetVoice ? ModelLayout.PresetVoice : m == TtsMode.VoiceDesign ? ModelLayout.VoiceDesign : ModelLayout.VoiceClone,
            () => root, () => output);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var player = play ? new StreamingAudioPlayer(log) : null;
        player?.ConfigureBuffer(SpeechDurationEstimate.ForText(script).UpperSeconds);
        var clock = Stopwatch.StartNew();
        var started = Stopwatch.GetTimestamp();
        long offset = 0;
        var arrivals = new List<double>();
        var acceptedSeconds = new List<double>();
        var referencePath = Path.Combine(output, "reference.wav");
        if (selected == TtsMode.VoiceClone)
        {
            var raw = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(output, "reference.f32"))).ToArray();
            WavWriter.Write(referencePath, raw.Select(s => (short)Math.Round(Math.Clamp(s, -1, 1) * short.MaxValue)).ToArray());
        }
        try
        {
            var request = new GenerateRequest(selected, script, "english", "ryan",
                "A calm male narrator with a warm, clear voice.", referencePath,
                "Hello! We'll begin in just a few minutes.", chunk =>
                {
                    if (chunk.Failure is not null) { player?.Stop(); return; }
                    if (chunk.SampleOffset != offset) throw new InvalidDataException("Noncontiguous section audio");
                    offset += chunk.Samples.Length;
                    if (chunk.Samples.Length > 2880)
                    {
                        arrivals.Add(clock.Elapsed.TotalSeconds);
                        acceptedSeconds.Add(offset / 24000.0);
                        Console.WriteLine($"SECTION ready={offset / 24000.0:F2}s elapsed={clock.Elapsed.TotalSeconds:F2}s");
                    }
                    player?.Add(chunk, cancel.Token);
                    player?.ReportGeneratedSeconds(offset / 24000.0);
                });
            var result = await engine.GenerateAsync(request, null, cancel.Token);
            var completed = clock.Elapsed.TotalSeconds;
            if (player is not null) await player.CompleteAsync(cancel.Token);
            if (arrivals.Count < 2 || arrivals[0] >= completed || Math.Abs(offset / 24000.0 - result.Duration.TotalSeconds) > .001)
                throw new InvalidDataException("Sections did not arrive early or final length differs");
            var report = new { mode, script, audioSeconds = result.Duration.TotalSeconds, synthesisSeconds = completed,
                sectionArrivalSeconds = arrivals, acceptedSeconds,
                firstDeviceReadSeconds = player?.FirstPlaybackTimestamp > 0 ? (player.FirstPlaybackTimestamp - started) / (double)Stopwatch.Frequency : (double?)null,
                playerFailure = player?.Failure, output = Path.GetFileName(result.OutputPath),
                metadata = WavMetadata.TryRead(result.OutputPath), peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
            File.WriteAllText(Path.Combine(output, mode + "-sections.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return player?.Failure is null ? 0 : 1;
        }
        catch (Exception ex) { player?.Stop(); Console.Error.WriteLine(ex); return 1; }
    }
}
