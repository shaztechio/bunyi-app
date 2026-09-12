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

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: LongTextProbe preset|design|clone MODELS_PARENT OUTPUT_FOLDER"); return 2; }
        var mode = args[0];
        var modelParent = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        var log = new LogStore(e => Console.WriteLine(e.Message));
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
                "Hello! We'll begin in just a few minutes.");
            var result = await engine.GenerateAsync(request, null, cancel.Token);
            var report = new { mode, script, audioSeconds = result.Duration.TotalSeconds,
                synthesisSeconds = result.Elapsed.TotalSeconds, output = Path.GetFileName(result.OutputPath),
                metadata = WavMetadata.TryRead(result.OutputPath), peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 };
            File.WriteAllText(Path.Combine(output, mode + "-sections.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
