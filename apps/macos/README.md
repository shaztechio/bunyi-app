# Bunyi — native macOS app

A SwiftUI app wrapping [swift-qwen3-tts](https://github.com/AtomGradient/swift-qwen3-tts)
(MLX), the Swift/MLX port of Qwen's
[Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS). Three modes — preset
voices, voice design, voice cloning — with automatic model download, live
generation progress, playback, and outputs saved to the app's `Outputs`
folder (Application Support, inside the sandbox container).
Targets macOS 15+ (Apple Silicon).

## Build (Xcode 26, Apple Silicon)

Requires **Xcode 26** — the app uses Swift 6.2 features and mlx-swift's
Metal Toolchain, and does not build on Xcode 16.x. CI runs on the
`macos-26` runner.

The Xcode project is **generated** — `Bunyi.xcodeproj`,
`Info.plist`, and the entitlements file are all produced from
`project.yml`, so they aren't in the repo. Edit `project.yml` and
regenerate; never hand-edit the `.xcodeproj`.

```sh
brew install xcodegen
xcodegen generate                    # creates Bunyi.xcodeproj
xcodebuild -scheme "Bunyi" -destination 'platform=macOS' build
```

Then `open Bunyi.xcodeproj` and ⌘R, or launch the built `.app`
from DerivedData. `./build-dist.sh` builds Release into
`../../dist/macos/Bunyi.app`.

Xcode resolves the package dependencies on first build:
[swift-qwen3-tts](https://github.com/AtomGradient/swift-qwen3-tts) (branch
`main`), which pulls in mlx-swift, mlx-swift-examples, and
swift-transformers (the `Hub` import in `TTSEngine.swift`). Exact commits
are pinned in the checked-in `Package.resolved`.

Sandbox entitlements are declared in `project.yml`: outgoing network
(model downloads), user-selected files read/write (reference clips and
backup archives), and on-device speech recognition (reference-clip
transcription for voice cloning). Generated WAVs land in the app's
`Outputs` folder under the container's Application Support, so no
file-access entitlement is needed for output.

First generation in each mode downloads that mode's model (~1.5–4.5 GB)
with a progress bar; after that it runs fully offline.

**Xcode 26 note:** the Metal compiler ships separately, and mlx-swift
compiles Metal shaders, so a first build fails with *"cannot execute tool
'metal'"* until you run:

```sh
xcodebuild -downloadComponent MetalToolchain
```

Note: the package README's `default.metallib` copy step applies to its SPM
CLI demo. Xcode app builds bundle the Metal library through mlx-swift's
build plugin, so you shouldn't need it — if you hit a Metal library error
at runtime, that step is the fix.

## Self-hosting the models

Each mode's field in **Settings → Models** accepts either a Hugging Face
repo ID (default) **or** an `https://` base URL you control — the app
decides by scheme. With a base URL it downloads the model files directly
from your server (no Hugging Face API involved), so you can serve them from
your own host, an internal mirror, or your own HF "fork" (point the URL at
its `.../resolve/main` directory).

How the app finds the file list:

1. It fetches `<base>/manifest.txt` — a newline-separated list of relative
   paths — and downloads each `<base>/<path>`. Generate it from a model
   folder with:
   ```sh
   cd model-dir && find . -type f ! -name manifest.txt | sed 's|^\./||' > manifest.txt
   ```
2. If there's no `manifest.txt`, it falls back to the standard Qwen3-TTS
   file set. `config.json` and `model.safetensors` are required (a 404
   fails the download); everything else is best-effort (single-shard repos
   have no `.index.json`, and a missing `tokenizer.json` is backfilled from
   Hugging Face automatically).

Files download to `models/self-hosted/<slug>` in the models folder and are
reused offline afterwards, exactly like Hub downloads.

**Use https.** Plain `http://` is blocked by App Transport Security; to
allow it (e.g. a LAN server) add an `NSAppTransportSecurity` exception to
the Info.plist section of `project.yml` and regenerate — the default is
https-only so the app isn't weakened globally.

## Things you'll likely tune

- **Models** (`TTSMode.repoID`): defaults are the bf16 conversions the
  package documents support for. For a smaller end-user download, try
  `AtomGradient/Qwen3-TTS-0.6B-CustomVoice-4bit-pruned-vocab-lite`
  (808 MB, preset voices only) — test quality before shipping it.
- **Hub API surface**: `HubApi.snapshot(from:matching:progressHandler:)`
  has shifted slightly across swift-transformers releases. If the call
  doesn't compile against the resolved version, ⌘-click `HubApi` and
  match the signature — it's a one-line fix.
- **Voice clone** runs the package's synchronous `generateVoiceClone` in a
  detached task (off the main actor) and surfaces its `onToken` callback as
  a live token count, so long texts don't stall the UI.

## Voice cloning & reference transcript

Voice clone is an in-context-learning (ICL) path: the model needs the
reference audio **and its transcript** to align audio to words before it
can speak your text in that voice. A missing transcript produces gibberish
that ignores your text, so if the transcript field is left blank the app
auto-transcribes the clip on-device with Apple's Speech framework
(`SFSpeechRecognizer`). Reference audio is also resampled to 24 kHz mono
first — the model asserts that rate, and feeding a 44.1/48 kHz clip
unchanged is what causes distorted, wrong-pitch clones.

### Emotion / style control (not available for clones today)

Voice clone runs on the **Base** model, which isn't instruction-tuned:
`generateVoiceClone` takes no `instruct` parameter, and the package's own
dispatcher passes `instruct: nil` with the comment *"Base model doesn't
support instruct"*. Only preset voice (`generateCustomVoice`) and voice
design accept an emotion/style instruction — which is why the app shows a
style field in those two modes and not in Voice clone. For a cloned voice,
emotion has to come from the reference clip's own delivery, so keep one
reference per emotion (the saved-voices library makes that practical).

This is a limitation of the current **12 Hz** stack rather than of this app.
Qwen's technical report lists unreleased **25 Hz** models and tokenizer
(`Qwen-TTS-Tokenizer-25Hz`, plus 0.6B/1.7B Base, CustomVoice, and
**VoiceEditing** variants), which are the likely path to emotion control on
cloned voices. Track the release in
[QwenLM/Qwen3-TTS#34](https://github.com/QwenLM/Qwen3-TTS/issues/34).

Caveats on that expectation, so nobody plans around a promise that wasn't
made: issue #34 is Hugging Face asking Qwen to publish the 25 Hz artifacts,
and the maintainer replied only "we will notify you when it's released."
The statement that emotion can't be changed for a cloned voice with the
12 Hz tokenizer comes from a **community comment** in that thread, not from
a maintainer, and no official capability claim for 25 Hz cloning appears
there. The issue is closed and the checkpoints were still unreleased as of
July 2026. If/when they ship, expect a new tokenizer as well — the repo IDs
in Settings would need to change together, since tokenizer rate and model
must match.

**Transcriber macOS support:** the app targets macOS 15.0+, and every
Speech API used is available below that floor, so no version gating is
needed:

| API | Available since |
| --- | --- |
| `SFSpeechRecognizer` + file recognition (`SFSpeechURLRecognitionRequest`) | macOS 10.15 |
| On-device recognition (`supportsOnDeviceRecognition` / `requiresOnDeviceRecognition`) | macOS 10.15 |
| `addsPunctuation` | macOS 13 |

On-device recognition is preferred (kept fully local); when a language
isn't available on-device it falls back to Apple's server recognizer,
which is why the app keeps the network entitlement. On-device *language*
coverage widens with newer macOS releases and is checked at runtime, so
behavior degrades gracefully on older systems rather than failing. Needs
`NSSpeechRecognitionUsageDescription` (set in `project.yml`) and a one-time
authorization prompt. On macOS 26+ the newer `SpeechTranscriber` /
`SpeechAnalyzer` API is a reasonable upgrade if you raise the deployment
target, but `SFSpeechRecognizer` was chosen to cover the whole 15+ range.

## Distributing to non-technical users

An unsigned build will be blocked by Gatekeeper on your users' Macs, so
budget for the full path:

1. Apple Developer Program ($99/yr), Developer ID Application certificate.
2. Archive in Xcode (Product → Archive) → Distribute App → Direct
   Distribution. This signs with hardened runtime and submits to Apple's
   **notarization** service (usually minutes).
3. Ship the exported `.app` in a DMG or zip. Users drag to Applications
   and double-click — no warnings, no terminal.

Alternative: Mac App Store distribution works too, but review the model
download UX (large post-install downloads are allowed; document it in the
review notes) and confirm the Apache-2.0 model licenses fit your use.

## Architecture notes

- `TTSEngine` is a `@MainActor @Observable` class owning one loaded model
  at a time; switching modes evicts the previous model and calls
  `MLX.GPU.clearCache()` to return unified memory.
- Models download to `~/Library/Application Support/Bunyi/Models`
  via the Hub snapshot API (incremental — cached files are skipped).
- Preset/design modes use `generateStream` and surface `.token` events as
  a live counter; final audio arrives as one `MLXArray`, written with the
  package's `saveAudioArray`.
