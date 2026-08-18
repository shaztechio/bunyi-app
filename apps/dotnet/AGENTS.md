# AGENTS.md — Windows + Linux app (.NET + Avalonia + ONNX)

> **Status: the app is not implemented; the build and the groundwork are.**
> `dotnet build`, `dotnet test` and a self-contained `dotnet publish` are green
> on Windows and Linux, and CI runs all three on both. The research gate is
> also behind us: ONNX inference is proven for preset voice and the stack is
> chosen — see [`RESEARCH-ONNX.md`](RESEARCH-ONNX.md), which is the first thing
> to read before touching the engine or its dependencies.
>
> What is missing is the product. Most of `src/Core` is still a stub that
> throws, and `src/App` is a placeholder window. Fill them in against
> `/spec/FEATURES.md`.

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

  C# pipeline for preset voice: the `ElBruno.QwenTTS` NuGet package (MIT). It
  covers **CustomVoice only** — design and clone need their own inference code
  against the Python reference scripts those exports ship.

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
  > Linux is proven (WSL2): NAudio arrives but is never called, and speed and
  > memory match Windows. One trap it records: ONNX weights are memory-mapped,
  > so a models folder on a slow volume makes **generation** slower, not just
  > loading.
- **Transcription:** Whisper (Whisper.net or whisper-ONNX), bundled — works
  offline and identically on both OSes (do **not** use OS speech APIs; they
  differ per platform).
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

A self-contained publish is ~206 MB on Windows and ~104 MB on Linux before a
single model is downloaded. Worth knowing before promising a small download.

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
| §1 modes / §2 output | `TTSEngine.generate` | `ITtsEngine` / `OnnxTtsEngine` |
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
| §7 settings | `SettingsView` | `SettingsViewModel` |
| §8 logs | `LogStore` | `Diagnostics.LogStore` / `ILogSink` |
| §2/§3d data locations | (Application Support) | `Infrastructure.AppPaths` |
| §7 settings storage | (`UserDefaults`) | `Settings.SettingsStore`, `AppSettings` |
| §2/§2a reveal in file manager | `NSWorkspace` | `Platform.FileReveal` |
| §9 busy-close | `WindowCloseGuard` | `MainWindow.OnClosing` |

## Rules

- Keep `src/Core` UI-framework-free (mirrors "keep TTSEngine
  UI-framework-free"). UI lives only in `src/App`. **CI enforces this** — it
  fails on a `using Avalonia` or an Avalonia package reference under
  `src/Core`, because the rule is easy to break with an editor's
  using-completion and hard to spot in review.
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
- **Never let the library write the output file.** Use `SynthesizeToPcmAsync`
  and write the WAV ourselves, so the filename, the folder and the RIFF
  `LIST`/`INFO` chunk are ours (§2).
- **Never use `ElBruno.HuggingFace.Downloader`**, which arrives transitively.
  It implements none of §3b — no byte-level progress, resume, stall detection
  or checksums. Our `ModelDownloader` fills the folder; the pipeline only
  reads it.
