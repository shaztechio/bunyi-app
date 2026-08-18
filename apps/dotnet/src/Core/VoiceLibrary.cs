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

// Saved voice-clone prompts (name + copied clip + transcript).
// Mirrors macOS VoiceLibrary. Spec: /spec/FEATURES.md §5,
// voices.json schema in /spec/DATA-FORMATS.md.
namespace Bunyi.Core;

public sealed record SavedVoice(
    Guid Id,
    string Name,
    string FileName,        // copied clip, sibling of voices.json
    string Transcript,
    DateTimeOffset CreatedAt);

public sealed class VoiceLibrary(LogStore log)
{
    private readonly LogStore _log = log ?? throw new ArgumentNullException(nameof(log));

    public IReadOnlyList<SavedVoice> Voices { get; private set; } = Array.Empty<SavedVoice>();

    /// <summary>Copy the clip into app storage and record it. Spec §5.</summary>
    public Task<SavedVoice> SaveAsync(string name, string audioPath, string transcript)
        => throw new NotImplementedException();

    public void Delete(SavedVoice voice)
        => throw new NotImplementedException();

    /// <summary>Load voices.json, pruning entries whose audio is missing.</summary>
    public void Load()
        => throw new NotImplementedException();
}
