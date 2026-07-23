# Qwen3 TTS Studio

Native macOS SwiftUI app for local text-to-speech using Qwen3-TTS on
Apple Silicon via MLX. Built for non-technical end users: no terminal,
models auto-download with a progress bar, outputs are playable WAVs.
Will be distributed as a notarized .app (Developer ID, hardened runtime).

Upstream model: [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) (QwenLM),
wrapped for Swift/MLX by
[swift-qwen3-tts](https://github.com/AtomGradient/swift-qwen3-tts).

## Current state

Builds and launches (2026-07-23). The Xcode project is generated from
`project.yml` via XcodeGen — edit `project.yml` and rerun
`xcodegen generate`, never hand-edit `Qwen3TTSStudio.xcodeproj`.
Entitlements/Info.plist are generated too. The only source fix needed
was making the HubApi.snapshot progress closure `@Sendable`. Runtime
(model download + generation in all three modes) is not yet verified.
Note: Xcode 26 needs the Metal Toolchain component
(`xcodebuild -downloadComponent MetalToolchain`) or mlx-swift's shader
build fails.

## Project structure

- `Qwen3TTSStudioApp.swift` — @main entry point, single WindowGroup
- `ContentView.swift` — UI: segmented mode picker (preset voice / voice
  design / voice clone), text editor, per-mode controls, status area,
  AVAudioPlayer playback, fileImporter for clone reference audio
- `TTSEngine.swift` — @MainActor @Observable engine: model download via
  swift-transformers HubApi.snapshot, one model resident at a time
  (evict + `MLX.GPU.clearCache()` on switch), generation, WAV output to
  `~/Music/Qwen3 TTS`. Skips the network entirely when a complete model
  is on disk (`hasCompleteModel`); during downloads a disk monitor logs
  bytes-on-disk every 10 s (the Hub fraction only ticks per completed
  file, so it looks frozen during the big safetensors)
- `LogStore.swift` / `LogsView.swift` — in-app Logs window (Window →
  Logs, ⌘L), mirrored to OSLog subsystem com.geppettoforge.Qwen3TTSStudio
- `ModelSettings.swift` / `SettingsView.swift` — Settings (⌘,): per-mode
  repo ID overrides (UserDefaults, `TTSMode.effectiveRepoID`) and a
  custom models folder via security-scoped bookmark (`ModelsLocation`)
- `VoiceLibrary.swift` — saved voice-clone prompts (name + reference clip
  + transcript) in `<AppSupport>/Qwen3TTSStudio/Voices` (`voices.json` +
  copied audio). The clip is COPIED into the container so it works after
  relaunch without a security-scoped bookmark. Cloned voices can't become
  real presets — those are trained speaker tokens in `talkerConfig.spkId`
  — so this saves the recipe and re-runs the clone path each time.
- `BackupManager.swift` — Settings → Backup: zip backup/restore of the
  models folder, with live progress and a Stop button. Default container
  folder zips via `zip -0` (stored/no compression — safetensors don't
  compress, so it's ~4x faster than DEFLATE and the output size tracks
  the source, driving a determinate bar by polling the growing temp zip).
  A custom (bookmarked) folder can't be read by a child process, so it
  falls back to NSFileCoordinator .forUploading (in-process, no progress,
  Stop can't interrupt it mid-file). Restore extracts via
  /usr/bin/ditto -xk on container temp paths, then FileManager merge
  (per-repo, skip existing). Cancellation terminates the child
  zip/ditto process via `RunningProcess` (Task.cancel alone won't kill a
  child); cancelled backups delete the partial destination. Saving the
  finished zip out of the container is volume-aware: same volume =
  instant `moveItem` rename; different volume (network/external drive) =
  a streamed `copyFile` with its own progress bar. Both run off the main
  actor in `Task.detached` — a synchronous multi-GB copy on the main
  actor was freezing the UI (beachball) on slow destinations.
- App is sandboxed: the default models dir resolves to
  `~/Library/Containers/com.geppettoforge.Qwen3TTSStudio/Data/Library/
  Application Support/Qwen3TTSStudio/Models`, layout
  `models/<org>/<repo>` with Hub partials under `.cache/huggingface/`.
  Pre-downloading with `hf download <repo> --local-dir <that path>` works
  and the app will use the files offline
- `README.md` — human setup notes (Xcode project creation, entitlements,
  distribution)

## Setup (if the Xcode project doesn't exist yet)

1. macOS App template, SwiftUI, product name "Qwen3 TTS Studio",
   deployment target macOS 15.0
2. Replace template sources with the three .swift files above
3. Add package: https://github.com/AtomGradient/swift-qwen3-tts.git
   (branch main), product `Qwen3TTS`. Transitive deps: mlx-swift,
   mlx-swift-examples, swift-transformers (provides `import Hub`)
4. App Sandbox entitlements: outgoing network (client), user-selected
   files read, Music folder read/write

## Build & verify

- Prefer CLI so errors are readable:
  `xcodebuild -scheme "Qwen3 TTS Studio" -destination 'platform=macOS' build`
- Runtime smoke test: launch, Preset voice mode, short English sentence,
  speaker Ryan → should download ~1.4 GB model (one-time), then produce
  and auto-play a WAV in `~/Music/Qwen3 TTS`

## Key API facts (from swift-qwen3-tts README)

- `Qwen3TTSModel.fromPretrained(_ localPath: String) async throws`
- `model.generate(text:speaker:instruct:language:...) async throws -> MLXArray`
- `model.generateStream(...)` -> AsyncThrowingStream emitting
  `.token(Int)` during generation, then `.info`, then final `.audio(MLXArray)`
- `model.generateVoiceClone(text:referenceAudio:referenceText:language:...)`
  — synchronous (not async), Base model only
- Helpers: `loadAudioArray(from:) -> (Int, MLXArray)`,
  `saveAudioArray(_:sampleRate:to:)`
- `model.sampleRate` == 24000, `model.supportedSpeakers: [String]`
- Models per mode (see `TTSMode.repoID`):
  CustomVoice 0.6B bf16 / VoiceDesign 1.7B bf16 / Base 1.7B bf16,
  all under mlx-community on Hugging Face

## Known risks / likely first errors

- The mlx-community Qwen3-TTS repos ship vocab.json + merges.txt but NOT
  tokenizer.json, which the package's `AutoTokenizer.from(modelFolder:)`
  hard-requires (throws configurationMissing("tokenizer.json")). All
  Qwen3-TTS variants share one text tokenizer, so the engine auto-fetches
  tokenizer.json from the AtomGradient pruned-vocab repo (verified
  identical 151,643-token vocab) via `ensureTokenizerJSON(in:)`.

- `HubApi.snapshot(from:matching:progressHandler:)` signature drifts
  across swift-transformers releases; also verify `HubApi(downloadBase:)`
  initializer and `Hub.Repo(id:)` vs `Repo(id:)`. Match against the
  resolved version, don't fight it.
- Enum name `Qwen3TTSGeneration` case spellings (.token/.info/.audio)
  should be verified against package source.
- `generateVoiceClone` is synchronous and heavy; the engine runs it in a
  `Task.detached` off the main actor so the UI stays responsive, bridging
  its `onToken` callback back as live progress over an AsyncStream. Model
  and MLXArray are non-Sendable, carried across via the private
  `Unchecked<T>` box (safe: generation is serialized to one job).
- Voice clone reference audio MUST be 24 kHz mono. `generateVoiceClone`
  takes only an MLXArray (no sample-rate arg) and asserts 24 kHz
  internally (Qwen3.swift extractSpeakerEmbedding), and the package's
  `loadAudioArray` does NOT resample — it returns the file's native rate.
  Feeding a 44.1/48 kHz clip unresampled produces distorted, wrong-pitch
  clones. `TTSEngine.loadReferenceAudio` resamples to `model.sampleRate`
  mono via AVAudioConverter; don't revert to `loadAudioArray` for clones.
- Voice clone is ICL: it REQUIRES the reference transcript (it aligns the
  reference audio codes to words in `prepareICLGenerationInputs`). An
  empty transcript yields gibberish that ignores the target text. When
  the transcript field is blank, `ReferenceTranscriber` auto-transcribes
  the clip on-device (Speech framework, `SFSpeechRecognizer`,
  on-device preferred) — needs `NSSpeechRecognitionUsageDescription`
  (set in project.yml Info.plist) and one-time user authorization.
  MUST feed PCM buffers via `SFSpeechAudioBufferRecognitionRequest`, not a
  file URL: the recognition daemon is a separate process and can't read a
  sandbox security-scoped file, so a URL request returns an empty
  transcript. The engine reads the file in-process and appends buffers.
- Metal library errors at runtime: the repo README's `default.metallib`
  copy step targets its SPM CLI demo; Xcode builds should bundle it via
  mlx-swift's plugin. If hit anyway, that copy step is the fix.
- Package is young (10 stars, 9 commits) on branch main: pin the exact
  resolved commit in Package.resolved before any release; consider
  vendoring/forking later.

## Conventions

- Swift 6 / strict concurrency where feasible; @Observable, not
  ObservableObject
- Keep TTSEngine UI-framework-free except the AppKit Finder reveal
- No new dependencies without a strong reason
- User-facing copy: plain verbs, sentence case, no jargon ("Generate",
  not "Synthesize")

## Roadmap (after it compiles and runs)

1. Fix compile errors, run all three modes end to end
2. Saved-voices library: persist voice-clone prompts (reference audio +
   transcript) so users design a character voice once and reuse it
3. Batch mode: generate one WAV per line from a dropped .txt script
4. Optional smaller default model for distribution:
   AtomGradient/Qwen3-TTS-0.6B-CustomVoice-4bit-pruned-vocab-lite
   (808 MB) — A/B the audio quality first
5. Signing + notarization pipeline (xcodebuild archive + notarytool)
