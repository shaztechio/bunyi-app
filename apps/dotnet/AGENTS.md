# AGENTS.md — Windows + Linux app (.NET + Avalonia + ONNX)

> **Status: complete. All three modes work end to end.**
> Every spec section is implemented — the three modes (§1), generation and
> History (§2, §2a), model management (§3), reference audio and transcription
> (§4), the saved voices library (§5), backup and restore (§6), Settings (§7),
> Logs (§8), busy-close (§9), error copy (§10) and Doctor (§11) — with the
> main window, Settings, Doctor, Logs and Help around them, and packaging for
> both platforms.
>
> One gap remains, and it is a real one: the style instruction does not reach
> the preset-voice model, where macOS passes it. It is described in the root
> [`AGENTS.md`](../../AGENTS.md), scoped in
> [`RESEARCH-ONNX.md`](RESEARCH-ONNX.md), and stated plainly in the shipped
> help — which is the only feature claim in there that is a limitation
> rather than a description.
>
> `dotnet build`, `dotnet test` and a self-contained `dotnet publish` are green
> on Windows and Linux, and CI runs all three on both. The research gate is
> behind us: ONNX inference is proven for preset voice and the stack is
> chosen — see [`RESEARCH-ONNX.md`](RESEARCH-ONNX.md), which is the first thing
> to read before touching the engine or its dependencies.

One C#/.NET application that targets **both Windows and Linux** from a single
codebase. It must implement the exact behavior in `/spec/FEATURES.md` and
the on-disk formats in `/spec/DATA-FORMATS.md`. The macOS app
(`/apps/macos/`) is the reference — match it when the spec is ambiguous.

## Stack

- **UI:** [Avalonia](https://avaloniaui.net) (renders natively on Windows &
  Linux). MVVM.
- **Inference:** [ONNX Runtime](https://onnxruntime.ai)
  (`Microsoft.ML.OnnxRuntime`), **CPU by default on both Windows and Linux**,
  with **CUDA as an opt-in build flavour** (3.7x faster, measured). The
  **vocoder always runs on CPU** — its graph fails on every GPU provider
  tried. Qwen3-TTS
  **ONNX** exports, one per mode:
  - preset voice — `elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX`
  - voice design — `wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX` (`int4`)
  - voice clone  — `wavekat/Qwen3-TTS-0.6B-Base-ONNX` (`int4`, ICL)

  One inference driver for all three modes: `src/Core/Qwen/TalkerLoop.cs`
  over each export, with a prefill builder per mode (`PresetPrefill`,
  `DesignPrefill`, `ClonePrefill`). Preset voice ran on the `ElBruno.QwenTTS`
  NuGet package until #178. [`WHY-NOT-ELBRUNO.md`](WHY-NOT-ELBRUNO.md) is
  the plain-language account of why it was replaced — half the memory, a
  style box that works, progress while it runs — and `RESEARCH-ONNX.md` has
  the measurements, including the half that did not pan out: speed.

  > **Read [`RESEARCH-ONNX.md`](RESEARCH-ONNX.md) before changing any of this.**
  > It records what was measured, and four things that are easy to get wrong:
  > the **vocoder graph only works on the CPU provider** — DirectML and CUDA
  > both die on the same `node_pad_1`, so the vocoder gets its own CPU session;
  > **DirectML loses to plain CPU** and is not used (which is also why ORT is
  > not pinned to its 1.24.4 ceiling), while **CUDA is worth an opt-in
  > flavour**; the clone export must be an **ICL** one or the reference
  > transcript is silently ignored; and memory, not speed, is the binding
  > constraint — 17.7 GB peak for 22 s of audio on the *smallest* model. The
  > three repos this file used to name are all rejected there, with reasons.
  > Linux is proven (WSL2): speed and memory match Windows. One trap it records: ONNX weights are memory-mapped,
  > so a models folder on a slow volume makes **generation** slower, not just
  > loading.
- **Tokenizer:** **our own**, `src/Core/Qwen/QwenTokenizer.cs`. Not
  `Microsoft.ML.Tokenizers`, which was measured against HuggingFace's tokenizer
  on the export's own files and cannot do Qwen2: **special tokens are not
  recognised at all** — `<|im_start|>` comes back as seven tokens of literal
  punctuation, and its `CodeGenTokenizer` has no public constructor that
  accepts them — and runs of spaces and newlines split differently. The chat
  template is nothing but special tokens, so that alone settles it. Ours is
  pinned against ids taken from HuggingFace on every case, kept in
  `tests/Core.Tests/Fixtures/qwen-tokenizer-truth.json`; those ids are what the
  model was trained on and are the only definition of correct.
- **Transcription:** Whisper, via `Whisper.net` — the same words on both OSes,
  and nothing leaves the machine (do **not** use OS speech APIs; they differ per
  platform). The **model** is downloaded on first use rather than shipped, so it
  costs nothing to the people who never open clone mode; the natives are what
  ship, and `Directory.Build.targets` keeps only the ones the target platform
  can load — the package offers every platform's at once.
- **Audio:** a cross-platform lib for playback + resample-to-24 kHz-mono
  (miniaudio via a binding, SDL, or PortAudio). No `System.Media` /
  `NAudio` (Windows-only).
- **Model download:** `HttpClient` (HF `resolve/main` URLs are plain HTTP
  GETs; the self-host path is plain GETs by design — see spec §3c).

## Build / run (on Windows or Linux)

Needs the **.NET 10 SDK**.

```sh
cd apps/dotnet
dotnet restore
dotnet build -c Release
dotnet test  -c Release
dotnet run --project src/App
# Publish self-contained — what a release ships (see /spec, M15):
dotnet publish src/App -c Release -r win-x64   --self-contained -o artifacts/win-x64
dotnet publish src/App -c Release -r linux-x64 --self-contained -o artifacts/linux-x64
```

A self-contained publish is ~260 MB on Windows and ~165 MB on Linux before a
single model is downloaded; the archives a release ships are ~95 MB and ~69 MB.
Worth knowing before promising a small download.

About 21 MB of each is ReadyToRun — native code pre-compiled beside the IL
so a cold start does not pay the JIT for it. It buys 30% off time-to-first-frame
on Linux and 41% on Windows (1189 ms to 698 ms), which is the trade being made
deliberately rather than by accident. The per-phase figures, and what they say
about where a cold start actually goes, are in the comment on the property in
`src/App/Bunyi.App.csproj`.

## Layout

```
apps/dotnet/
  Bunyi.sln
  Directory.Build.props      settings every project inherits (net10.0,
                             nullable, warnings-as-errors, version)
  Directory.Packages.props   every package version, in one place
  NuGet.config               nuget.org only
  src/Core/    class library — engine, model mgmt, backup, voices, download,
               transcription. Platform-agnostic; no UI refs.
  src/App/     Avalonia UI (views + viewmodels) referencing Core.
  tests/Core.Tests/   xUnit tests for src/Core.
  tests/App.Tests/    headless Avalonia tests for the window.
```

**Package versions are managed centrally.** A `PackageReference` here carries no
`Version` attribute; add the version to `Directory.Packages.props` instead. The
scaffold used to carry per-project versions under a comment admitting they were
unverified, which is exactly the state this prevents.

**Add a dependency with the code that uses it, not before.** `src/Core` has no
package references at all today, because nothing in it needs one yet; the
versions are already pinned centrally for when they do.

## Spec feature → C# type (target design)

| Spec section | macOS source | .NET type (in `src/Core`) |
|---|---|---|
| §1 modes / §2 output | `TTSEngine.generate` | `Engine.ITtsEngine` / `OnnxTtsEngine` |
| §1 preset-voice inference | `swift-qwen3-tts` | `Qwen.PresetSpeechSynthesizer` over `Qwen.PresetPipeline` (`ISpeechSynthesizer`) |
| §2 output WAV + metadata | `OutputMetadata.swift` | `Audio.WavWriter`, `Audio.WavMetadata` |
| §3 model source | `ModelSettings.effectiveSource` | `ModelSource`, `ModelSettings` |
| §3b download, resume, progress | `TTSEngine.download*` | `Models.ModelDownloader`, `HttpFileDownloader`, `StallMonitor` |
| §3b/§3c manifests and path rules | `manifest`, `safeRelativePath` | `Models.ManifestParser`, `ManifestPath` |
| DATA-FORMATS completeness | `hasCompleteModel` | `ModelDownloader.Inspect`, `ModelLayout` |
| §3c self-host + manifest | `downloadFromBaseURL` | `ModelDownloader.DownloadFromBaseUrl` |
| §3 tokenizer auto-fetch | `ensureTokenizerJSON` | `ModelDownloader.EnsureTokenizer` |
| §4 resample 24k mono | `loadReferenceAudio` | `AudioIO.LoadReferenceMono24k` |
| §4 auto-transcribe | `ReferenceTranscriber` | `IReferenceTranscriber` / `WhisperTranscriber` |
| §5 saved voices | `VoiceLibrary` | `VoiceLibrary`, `SavedVoice` |
| §6 backup/restore | `BackupManager` | `BackupManager` |
| §7 settings | `SettingsView` | `ViewModels.SettingsViewModel`, `Views.SettingsWindow` |
| §3a saved configurations | `ModelConfigLibrary` | `Settings.ModelConfigLibrary`, `ModelConfig` |
| §3d downloaded models | `DownloadedModels.swift` | `Models.DownloadedModels` |
| §8 logs | `LogStore` | `Diagnostics.LogStore` / `ILogSink` |
| §2/§3d data locations | (Application Support) | `Infrastructure.AppPaths` |
| §7 settings storage | (`UserDefaults`) | `Settings.SettingsStore`, `AppSettings` |
| §2/§2a reveal in file manager | `NSWorkspace` | `Platform.FileReveal` |
| §9 busy-close | `WindowCloseGuard` | `Views.MainWindow.OnClosing` |
| §2a History | `HistoryView.swift` | `ViewModels.HistoryViewModel`, `Views.HistoryView` |
| §2a folder-is-the-record | `generatedOutputs()` | `Audio.GeneratedOutputs` |
| §2a/§3d move to Trash | `FileManager.trashItem` | `Platform.Trash` |
| §1/§2 main window | `ContentView.swift` | `ViewModels.MainViewModel`, `Views.MainWindow` |
| §1 readiness + examples | `ContentView.canGenerate` | `Engine.GenerationReadiness`, `ExamplePrompts` |
| §2 playback | `AVAudioPlayer` | `Audio.IAudioPlayer` / `SoundFlowAudioPlayer` |

## Help text

`HELP.md` is the only copy of the user-facing help text, and it is an
**EmbeddedResource** in `Bunyi.App` rather than a file beside the executable —
a portable build is a folder someone copies about, and help that lives outside
the assembly is help that goes missing.

- `src/Core/Help/HelpDocument.cs` parses it: headings, paragraphs, bulleted and
  numbered lists, fenced code, and inline bold/italic/code/links. That is the
  whole subset, and it matches what the macOS renderer supports. Keep `HELP.md`
  inside it.
- The parse lives in Core so it is testable with no UI. `HelpWindow` only turns
  blocks into controls.
- It is **not** a copy of `apps/macos/HELP.md`. This app has one working mode,
  so the text describes what exists here and says plainly what does not — a
  test fails on Finder/-Command-/"your Mac" wording.

## Releasing

`.github/workflows/dotnet-release.yml`, triggered by a **`dotnet-v*`** tag or
run from the browser.

Its own tag namespace on purpose. macOS owns `v*`, and
`Directory.Build.props` records that the two apps release separately and are not
expected to march in step — so `dotnet-v1.2.0` cuts this app's release and
cannot start a signed macOS one.

```
Actions -> Windows + Linux release -> Run workflow
  bump: patch | minor | major     (or an exact version)
  bump: none                      builds both platforms and publishes nothing
```

Choosing a bump edits `VersionPrefix`, commits, tags, and pushes. A tag-triggered
run instead **refuses to build** if the tag disagrees with `VersionPrefix`,
because a release whose binaries carry a different number from its tag is worse
than one that failed to build.

What ships, per release:

- `Bunyi-<version>-win-x64.zip`
- `Bunyi-<version>-linux-x64.tar.gz`, which is a tarball rather than a zip
  because the executable bit does not survive the latter
- `Bunyi-<version>-win-x64-cuda.zip` and `-linux-x64-cuda.tar.gz`, the same
  app published with `-p:BunyiCuda=true` so it carries ONNX Runtime's GPU
  package. **Not the default download and not on its way to becoming one:**
  it is 128.6 MB larger zipped, and the provider still needs NVIDIA's CUDA
  Toolkit, which ONNX Runtime does not ship. It is for people who already have
  the toolkit; the app detects CUDA and falls back to the CPU when it cannot
  load, so taking the wrong one is slow rather than broken
- a `.sha256` beside each

Both are **self-contained**: no .NET runtime to install, and nothing written
outside the unpacked folder plus the app-data directories in
`/spec/DATA-FORMATS.md`.

- **Not code-signed.** There is no certificate for either platform, so Windows
  SmartScreen warns on first run, and the release notes say so rather than
  leaving people to guess. The SignPath Foundation application was **rejected**;
  a Certum open-source certificate is **in progress**. Until one exists, no
  claim that Windows builds are signed goes back into the README, the site or
  the release notes — that is what had to be removed once already.
  [`RESEARCH-SIGNING.md`](RESEARCH-SIGNING.md) has the routes and their state.
- **CPU only.** `RESEARCH-ONNX.md` measured DirectML as slower than plain CPU
  and CUDA as worth an opt-in — but CUDA needs a user-installed toolkit, which
  is the wrong trade for this audience. Anyone who wants it can publish with the
  GPU package themselves.
- Release notes come from `tools/release-notes.py`, scoped with
  `--path apps/dotnet` so a release does not list the other app's commits.
  Deliberately not `--path spec`: behaviour changes start in /spec by this
  repository's own rule, so every macOS feature touches it too, and scoping
  to it put fifteen `feat(macos)` lines into this app's first release. A hand-written `release-notes/dotnet-v<version>.md` wins when present,
  because squashed merges make one line out of a PR that carried five changes.

## Rules

- Keep `src/Core` UI-framework-free (mirrors "keep TTSEngine
  UI-framework-free"). UI lives only in `src/App`. **CI enforces this** — it
  fails on a `using Avalonia` or an Avalonia package reference under
  `src/Core`, because the rule is easy to break with an editor's
  using-completion and hard to spot in review.
- **The window's own guarantees are tested headlessly.** `tests/App.Tests`
  opens the real `MainWindow` with no display and asserts the things §2 states
  about it — Generate is *replaced* by Stop rather than disabled, the inputs go
  dead while work runs, and Help and the log do not. That last one was claimed
  to hold "by construction", which stays true only until someone moves a panel.
  It runs with no `DISPLAY`, so CI exercises it too.
- **Two xunit majors, on purpose.** `Core.Tests` is on xunit v2;
  `App.Tests` is on **v3**, because `Avalonia.Headless.XUnit` requires it. They
  are separate projects and `dotnet test` runs both. Worth unifying on v3 when
  something else makes the migration cheap — v3 is the current line — but not
  worth touching 200 passing tests for on its own.
- **Warnings are errors.** If a stub's injected dependency is unread, hold it
  in a field with a null check rather than suppressing the warning: that is the
  code the implementation needs anyway.
- On-disk formats (`models/<org>/<repo>`, `manifest.txt`, `voices.json`,
  backup zip, output WAV) MUST match `/spec/DATA-FORMATS.md` so folders and
  backups are interchangeable within the ONNX runtime family.
- Any behavior change starts in `/spec`, then lands in both apps.
- **ONNX defaults differ from MLX**: the per-mode defaults are the three repos
  above (pinned in `/spec/FEATURES.md` §3a); the source-selection UX is
  identical to macOS.
- **The pipelines return samples; `WavWriter` writes the file.** So the
  filename, the folder and the RIFF `LIST`/`INFO` chunk are ours (§2).
- **`ModelDownloader` fills the folder; the pipelines only read it.** §3b —
  byte-level progress, resume, stall detection, checksums — lives in one place.
  A model library's own downloader, if one ever comes back with a dependency,
  implements none of it and must stay unused.
