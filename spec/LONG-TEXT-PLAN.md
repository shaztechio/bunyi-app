# Long-text generation

Observable behavior is defined in [FEATURES.md §2](FEATURES.md#2-generation-output).
Implemented for Windows/Linux in
[PR #230](https://github.com/shaztechio/bunyi-app/pull/230) and released in
[1.3.0](https://github.com/shaztechio/bunyi-app/releases/tag/dotnet-v1.3.0).
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

- [#224](https://github.com/shaztechio/bunyi-app/issues/224): implement sectioning,
  retry limits and designed-voice conditioning in macOS; retain existing
  completed-file playback on both apps. Native progress already shows frames
  and seconds through #221; section/attempt/retry progress remains to be added.
- Broader long-passage and multilingual listening checks for omissions,
  repetitions, voice continuity and audible joins on Windows and Linux.
- Compare the reported long clone failure with the export reference before
  claiming that its underlying model/conditioning cause has been resolved.
