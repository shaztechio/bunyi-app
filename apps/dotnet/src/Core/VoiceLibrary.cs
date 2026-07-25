// Saved voice-clone prompts (name + copied clip + transcript).
// Mirrors macOS VoiceLibrary. Spec: /spec/FEATURES.md §5,
// voices.json schema in /spec/DATA-FORMATS.md.
namespace Qwen3TtsStudio.Core;

public sealed record SavedVoice(
    Guid Id,
    string Name,
    string FileName,        // copied clip, sibling of voices.json
    string Transcript,
    DateTimeOffset CreatedAt);

public sealed class VoiceLibrary(LogStore log)
{
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
