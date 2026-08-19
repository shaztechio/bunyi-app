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

// Auto-transcription of the clone reference clip when the transcript is
// blank. Cross-platform via Whisper (NOT OS speech APIs). Mirrors macOS
// ReferenceTranscriber. Spec: /spec/FEATURES.md §4.
namespace Bunyi.Core;

public interface IReferenceTranscriber
{
    /// <summary>Transcribe an audio file to text. Empty result is an error.</summary>
    Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct);
}

// The implementation lives in Transcription/WhisperTranscriber.cs.
