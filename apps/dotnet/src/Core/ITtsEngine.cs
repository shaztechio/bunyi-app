// Generation engine. Mirrors macOS TTSEngine.generate(...).
// Spec: /spec/FEATURES.md §1, §2. Implementation: OnnxTtsEngine.
namespace Qwen3TtsStudio.Core;

public enum EngineState { Idle, Downloading, Loading, Transcribing, Generating, Error }

public sealed record EngineStatus(EngineState State, double Progress = 0, int Tokens = 0, string? Message = null)
{
    public bool IsBusy => State is not (EngineState.Idle or EngineState.Error);
}

public sealed record GenerateRequest(
    TtsMode Mode,
    string Text,
    string Language = "auto",
    string? Speaker = null,
    string? Instruct = null,          // preset/design only — clone ignores it (spec §1)
    string? ReferenceAudioPath = null,
    string? ReferenceTranscript = null);

public interface ITtsEngine
{
    EngineStatus Status { get; }
    string? LastOutputPath { get; }
    IReadOnlyList<string> Speakers { get; }

    /// <summary>Download+load (if needed) and generate a 24 kHz mono WAV.</summary>
    Task GenerateAsync(GenerateRequest request, CancellationToken ct);

    /// <summary>Cooperative stop for busy-close (spec §9).</summary>
    void Stop();
}

/// <summary>
/// ONNX Runtime implementation. TODO: everything — see /spec/FEATURES.md.
/// Reference C# ONNX pipeline: github.com/elbruno/ElBruno.QwenTTS.
/// </summary>
public sealed class OnnxTtsEngine : ITtsEngine
{
    public EngineStatus Status { get; private set; } = new(EngineState.Idle);
    public string? LastOutputPath { get; private set; }
    public IReadOnlyList<string> Speakers { get; private set; } = Array.Empty<string>();

    public Task GenerateAsync(GenerateRequest request, CancellationToken ct)
        => throw new NotImplementedException("Spec §1-§4. ONNX inference not yet implemented.");

    public void Stop() { /* TODO: cancel + reset. Spec §9. */ }
}
