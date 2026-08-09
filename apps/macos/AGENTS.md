# AGENTS.md — macOS app (Swift + MLX + SwiftUI)

The **reference implementation** of Bunyi. Behavior is specified
in `/spec/FEATURES.md` and `/spec/DATA-FORMATS.md` (source of truth for all
platforms); see `/AGENTS.md` for the multi-platform picture and the parity
rule. This file holds macOS build details and hard-won implementation notes.

> Renamed to **Bunyi** (was "Qwen3 TTS Studio"). The bundle ID
> (`com.geppettoforge.Qwen3TTSStudio`), log subsystem, and container
> subfolders (`Qwen3TTSStudio/Models|Outputs|Voices`) deliberately keep the
> old name so existing installs keep their models, voices, and settings.

Native macOS SwiftUI app for local text-to-speech using Qwen3-TTS on Apple
Silicon via MLX. For non-technical end users: no terminal, models
auto-download with a progress bar, outputs are playable WAVs. Distributed as
a notarized .app (Developer ID, hardened runtime).

Upstream model: [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS) (QwenLM),
wrapped for Swift/MLX by
[swift-qwen3-tts](https://github.com/AtomGradient/swift-qwen3-tts).

## Build & verify

**Requires Xcode 26** (Swift 6.2 features + mlx-swift's Metal Toolchain);
it does not build on Xcode 16.x. CI uses the `macos-26` runner.

Run from `apps/macos/`. The Xcode project is generated from `project.yml`
via XcodeGen — edit `project.yml` and rerun `xcodegen generate`, never
hand-edit `Bunyi.xcodeproj`. `Info.plist` and the entitlements
file are generated too.

```sh
cd apps/macos
brew install xcodegen                 # once
xcodegen generate
xcodebuild -scheme "Bunyi" -destination 'platform=macOS' build
```

- **Xcode 26** needs the Metal Toolchain or mlx-swift's shader build fails:
  `xcodebuild -downloadComponent MetalToolchain` (one-time, ~690 MB).
- Runtime smoke test: launch, Preset voice, short English sentence, speaker
  Ryan → downloads ~1.4 GB model once, then produces and auto-plays a WAV
  in the app's `Outputs` folder (Application Support, inside the sandbox
  container).

## Project structure

- `BunyiApp.swift` — @main entry point; WindowGroup + Logs Window +
  Settings scene
- `ContentView.swift` — UI: segmented mode picker, text editor, per-mode
  controls, status area, AVAudioPlayer playback, fileImporter for clone
  reference audio, saved-voice picker
- `TTSEngine.swift` — @MainActor @Observable engine: model download via
  swift-transformers HubApi.snapshot, one model resident at a time (evict +
  `MLX.GPU.clearCache()` on switch), generation, WAV output to
  `Qwen3TTSStudio/Outputs` under the container's Application Support.
  Skips the network when a complete model is on disk
  (`hasCompleteModel`); a disk monitor logs bytes-on-disk every 10 s. Also
  hosts self-hosted base-URL downloads (`downloadFromBaseURL` + manifest.txt
  / built-in list) and reference-audio resampling.
- `ModelSettings.swift` / `SettingsView.swift` — tabbed Settings (⌘,):
  per-mode source (`TTSMode.effectiveRepoID` / `effectiveSource`; HF repo ID
  OR http(s) self-host base URL, scheme decides) and a custom models folder
  via security-scoped bookmark (`ModelsLocation`). Plain http is blocked by
  ATS — https only unless an Info.plist exception is added.
- `VoiceLibrary.swift` — saved voice-clone prompts in
  `<AppSupport>/Qwen3TTSStudio/Voices` (`voices.json` + copied audio).
- `BackupManager.swift` — zip backup/restore, `zip -0` stored, live progress
  + Stop (child process killed via `RunningProcess`), volume-aware save off
  the main actor.
- `ReferenceTranscriber.swift` — on-device auto-transcription (Speech) when
  the transcript field is blank; feeds PCM buffers, not a file URL.
- `LogStore.swift` / `LogsView.swift` — Logs window (⌘L), mirrored to OSLog
  subsystem `com.geppettoforge.Qwen3TTSStudio`.
- `WindowCloseGuard.swift` — confirm-and-stop when the window is closed
  mid-operation (NSWindowDelegate bridge, forwards to SwiftUI's delegate).
- App is sandboxed: default models dir is
  `~/Library/Containers/com.geppettoforge.Qwen3TTSStudio/Data/Library/
  Application Support/Qwen3TTSStudio/Models`.

## Help book

`HELP.md` is the only copy of the user-facing help text. `tools/packaging/
make-help.sh` renders it to `Bunyi.help` inside the built bundle
(`render-help.swift` → HTML, then `hiutil` for the search index), and
`project.yml` runs it as a **post-build script**, so ⌘R and `build-dist.sh`
both ship help without a separate step. Nothing generated is committed.

- Help Viewer resolves a book by identifier: `CFBundleHelpBookName`
  (`app.bunyi.help`) and `CFBundleHelpBookFolder` (`Bunyi.help`) in the app's
  Info.plist must match the help bundle's `CFBundleIdentifier`. A mismatch
  shows an **empty Help window**, not an error.
- The book's version is set from the app's, because `helpd` caches a
  registered book by identifier *and* version — a frozen version serves stale
  help after `HELP.md` changes.
- `BunyiApp.swift` replaces SwiftUI's default Help item
  (`CommandGroup(replacing: .help)`) with one that calls
  `NSApplication.shared.showHelp(nil)`.
- Editing help = editing `HELP.md`. Keep it to what the renderer supports:
  headings, paragraphs, lists, code blocks, and inline bold/italic/code/links.

## Releasing (Developer ID + notarization)

`.github/workflows/release.yml` builds, signs, notarizes, staples, and
publishes. It runs **only** on `v*` tags and manual dispatch — never on
pull requests, because a fork's PR would otherwise get the signing
certificate.

```sh
./apps/macos/build-dist.sh Release          # ad-hoc signed, local use
./apps/macos/tools/packaging/sign-and-notarize.sh   # Developer ID + notarize + .dmg
```

- `sign-and-notarize.sh` never builds; it signs exactly what was tested.
  Nested code (the help book, frameworks) is signed before the outer bundle —
  `--deep` is deliberately unused.
- It verifies the **sandbox** and **network** entitlements survived signing.
  Losing the sandbox entitlement is the quiet failure that matters: the app
  launches fine and then resolves Application Support *outside* the container,
  so the models folder looks empty and re-downloads gigabytes.
- The app and the .dmg are notarized **separately**, so dragging the app out
  of the image and launching it offline still passes its first check.
- Local runs use a `notarytool` keychain profile (`BUNYI_NOTARY_PROFILE`,
  default `bunyi`); CI passes `BUNYI_APPLE_ID` / `BUNYI_TEAM_ID` /
  `BUNYI_APP_PASSWORD` instead, since a runner has no keychain profile.
- Repo secrets the workflow needs: `APPLE_CERTIFICATE_P12`,
  `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGN_IDENTITY`, `APPLE_ID`,
  `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`. Set them with
  `tools/packaging/set-release-secrets.sh`.
- The version lives in `project.yml` (`MARKETING_VERSION` /
  `CURRENT_PROJECT_VERSION`), **not** `Info.plist`, which XcodeGen
  regenerates. The workflow bumps it there and fails a tag that disagrees
  with the built version.
- `make-dmg.sh` replays a committed `.DS_Store` for the window layout rather
  than driving Finder on a runner, which needs a desktop session. The layout
  and background came from Sandfort; the app record's filename key was
  rewritten `Sandfort.app` → `Bunyi.app`, because positions are keyed by
  filename and a plain copy would have left the app unplaced. Icons sit at
  x=165 (app) and x=495 (Applications) on a 660×400 background.

## Key API facts (from swift-qwen3-tts)

- `Qwen3TTSModel.fromPretrained(_ localPath:) async throws`
- `model.generateStream(...)` → AsyncThrowingStream emitting `.token(Int)`,
  then `.info`, then final `.audio(MLXArray)`
- `model.generateVoiceClone(text:referenceAudio:referenceText:language:...)`
  — synchronous, Base model only, **no `instruct`** (no emotion for clones)
- `model.sampleRate` == 24000, `model.supportedSpeakers: [String]`
- Default models: CustomVoice 0.6B bf16 / VoiceDesign 1.7B bf16 / Base 1.7B
  bf16, all under mlx-community.

## Known risks / implementation notes

- mlx-community repos omit `tokenizer.json` (loader requires it); the engine
  auto-fetches a compatible one (`ensureTokenizerJSON`). See DATA-FORMATS.
- `HubApi.snapshot(...)` signature drifts across swift-transformers
  releases; match the resolved version. The progress closure must be
  `@Sendable`.
- `generateVoiceClone` is synchronous/heavy; run in `Task.detached` off the
  main actor, bridging `onToken` over an AsyncStream. Model/MLXArray are
  non-Sendable → carried via the private `Unchecked<T>` box (safe: one job
  at a time).
- Voice clone reference audio MUST be 24 kHz mono (`loadReferenceAudio`
  resamples; do not use `loadAudioArray`, which keeps native rate).
- Voice clone is ICL: it REQUIRES the transcript. Blank → auto-transcribe
  via Speech, feeding PCM buffers (the recognition daemon can't read a
  sandboxed file URL). Needs `NSSpeechRecognitionUsageDescription`.
- Metal library errors at runtime → the README `default.metallib` copy step.
- swift-qwen3-tts is young: pin the resolved commit in Package.resolved
  before any release.

## Conventions

- Swift 6 / strict concurrency where feasible; @Observable, not
  ObservableObject.
- Keep TTSEngine UI-framework-free except the AppKit Finder reveal.
- No new dependencies without a strong reason.
- User-facing copy: plain verbs, sentence case, no jargon.

## Roadmap

1. Batch mode: one WAV per line from a dropped .txt script.
2. Optional smaller default model for distribution
   (`AtomGradient/Qwen3-TTS-0.6B-CustomVoice-4bit-pruned-vocab-lite`, 808 MB)
   — A/B audio quality first.
3. Bunyi-coloured DMG background — the current one is Sandfort's generic
   chevron, which works but is not the app's indigo/violet.
   ~~Signing + notarization pipeline~~ — done, see "Releasing" above.
