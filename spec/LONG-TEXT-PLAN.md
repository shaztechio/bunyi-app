# Long-text generation

Observable behavior is defined in [FEATURES.md §2](FEATURES.md#2-generation-output).
The .NET app splits requests with an upper speech estimate above 20 seconds into
sentence-aware sections. An overlong attempt is discarded and subdivided, with
at most two subdivision levels per original section. Only completed sections
are joined, and playback starts after one complete WAV is saved.

Preset keeps its speaker and style; Clone reuses reference features. Design
creates a short completed opening, then uses it as a fixed clone reference for
the remainder, unloading the design model before loading clone. The UI explains
the extra model dependency and metadata records the continuation model.

Windows real-model validation is recorded in
[the long-text probe](../apps/dotnet/tools/LongTextProbe/VALIDATION.md).
Bounded recovery prevents an individual section from generating indefinitely;
it does not prove that every EOS-terminated section is word-perfect.

Tracked follow-ups:

- Implement sectioning, retry limits, designed-voice conditioning and matching
  progress in macOS; retain existing completed-file playback on both apps.
- Broader long-passage and multilingual listening checks for omissions,
  repetitions, voice continuity and audible joins on Windows and Linux.
- Compare the reported long clone failure with the export reference before
  claiming that its underlying model/conditioning cause has been resolved.
