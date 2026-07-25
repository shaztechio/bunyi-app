// Auto-transcription of the clone reference clip when the transcript is
// blank. Cross-platform via Whisper (NOT OS speech APIs). Mirrors macOS
// ReferenceTranscriber. Spec: /spec/FEATURES.md §4.
namespace Qwen3TtsStudio.Core;

public interface IReferenceTranscriber
{
    /// <summary>Transcribe an audio file to text. Empty result is an error.</summary>
    Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct);
}

/// <summary>
/// Whisper implementation (Whisper.net or whisper-ONNX), bundled so it runs
/// offline and identically on Windows + Linux. TODO: implement. Spec §4.
/// </summary>
public sealed class WhisperTranscriber : IReferenceTranscriber
{
    public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken ct)
        => throw new NotImplementedException("Spec §4. Decode to 16 kHz mono, run Whisper.");
}
