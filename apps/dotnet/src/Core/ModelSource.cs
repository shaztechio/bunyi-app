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

// Per-mode model source. Mirrors macOS ModelSettings.effectiveSource.
// Spec: /spec/FEATURES.md §3a, /spec/DATA-FORMATS.md.
namespace Bunyi.Core;

public enum TtsMode { PresetVoice, VoiceDesign, VoiceClone }

/// <summary>A Hugging Face repo id, or an http(s) base URL to self-host.</summary>
public abstract record ModelSource
{
    public sealed record Repo(string Id) : ModelSource;
    public sealed record BaseUrl(Uri Url) : ModelSource;

    /// <summary>
    /// Scheme decides: http(s):// ⇒ BaseUrl, else Repo. Blank ⇒ the mode's
    /// default (ONNX repo — differs from macOS/MLX defaults; see AGENTS.md).
    /// </summary>
    public static ModelSource Parse(string value, string defaultRepoId)
    {
        var v = string.IsNullOrWhiteSpace(value) ? defaultRepoId : value.Trim();
        if ((v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
             || v.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(v, UriKind.Absolute, out var url))
        {
            return new BaseUrl(url);
        }
        return new Repo(v);
    }
}
