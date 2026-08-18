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

// Model download: Hub repo or self-hosted base URL, resumable, offline
// reuse, progress/ETA, tokenizer auto-fetch. Mirrors macOS TTSEngine
// download* / downloadFromBaseURL / ensureTokenizerJSON.
// Spec: /spec/FEATURES.md §3, /spec/DATA-FORMATS.md.
using Bunyi.Core.Diagnostics;

namespace Bunyi.Core;

public sealed class ModelDownloader(HttpClient http, ILogSink log)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly ILogSink _log = log ?? throw new ArgumentNullException(nameof(log));

    // Standard file set when a self-hosted server has no manifest.txt.
    // ONNX variant — differs from the macOS .safetensors list. TODO: confirm
    // against the chosen ONNX export's actual contents (spec §3c).
    public static readonly string[] DefaultOnnxFiles =
    [
        "config.json",
        "model.onnx",
        "tokenizer.json",
        "vocab.json",
        "merges.txt",
        "tokenizer_config.json",
        "generation_config.json",
        "preprocessor_config.json",
        // speech_tokenizer/* as applicable to the export
    ];

    public static readonly HashSet<string> RequiredFiles = ["config.json", "model.onnx"];

    /// <summary>Resolve to a local model dir, downloading if not complete.</summary>
    public Task<string> EnsureModelAsync(ModelSource source, string modelsRoot, IProgress<double> progress, CancellationToken ct)
        => throw new NotImplementedException("Spec §3b/§3c. Reuse HasCompleteModel; skip network when complete.");

    /// <summary>manifest.txt if present, else DefaultOnnxFiles. Spec §3c.</summary>
    public Task<IReadOnlyList<string>> FileListAsync(Uri baseUrl, CancellationToken ct)
        => throw new NotImplementedException();

    /// <summary>Fetch a compatible tokenizer.json if missing. Spec §3, DATA-FORMATS.</summary>
    public Task EnsureTokenizerAsync(string modelDir, ModelSource source, CancellationToken ct)
        => throw new NotImplementedException();

    /// <summary>config.json + a weights file present, no partials. DATA-FORMATS.</summary>
    public static bool HasCompleteModel(string dir)
        => throw new NotImplementedException();
}
