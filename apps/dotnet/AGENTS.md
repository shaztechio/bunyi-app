# AGENTS.md — Windows + Linux app (.NET + Avalonia + ONNX)

> **Status: SCAFFOLD — the app is not implemented.**
> The stubs have never been filled in. What *has* happened is the research
> gate: ONNX inference is proven to work for preset voice on Windows, and the
> stack is chosen. See [`RESEARCH-ONNX.md`](RESEARCH-ONNX.md). Build and
> implement on a Windows or Linux machine with the .NET SDK, filling the stubs
> against `/spec/FEATURES.md`.

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
- **Transcription:** Whisper (Whisper.net or whisper-ONNX), bundled — works
  offline and identically on both OSes (do **not** use OS speech APIs; they
  differ per platform).
- **Audio:** a cross-platform lib for playback + resample-to-24 kHz-mono
  (miniaudio via a binding, SDL, or PortAudio). No `System.Media` /
  `NAudio` (Windows-only).
- **Model download:** `HttpClient` (HF `resolve/main` URLs are plain HTTP
  GETs; the self-host path is plain GETs by design — see spec §3c).

## Build / run (on Windows or Linux)

```sh
cd apps/dotnet
dotnet restore
dotnet build
dotnet run --project src/App
# Publish self-contained:
dotnet publish src/App -c Release -r win-x64   --self-contained
dotnet publish src/App -c Release -r linux-x64 --self-contained
```

## Layout

```
apps/dotnet/
  Qwen3TtsStudio.sln
  src/Core/    class library — engine, model mgmt, backup, voices, download,
               transcription. Platform-agnostic; no UI refs.
  src/App/     Avalonia UI (views + viewmodels) referencing Core.
```

## Spec feature → C# type (target design)

| Spec section | macOS source | .NET type (in `src/Core`) |
|---|---|---|
| §1 modes / §2 output | `TTSEngine.generate` | `ITtsEngine` / `OnnxTtsEngine` |
| §3 model source | `ModelSettings.effectiveSource` | `ModelSource`, `ModelSettings` |
| §3b download, resume, progress | `TTSEngine.download*` | `ModelDownloader` |
| §3c self-host + manifest | `downloadFromBaseURL` | `ModelDownloader.DownloadFromBaseUrl` |
| §3 tokenizer auto-fetch | `ensureTokenizerJSON` | `ModelDownloader.EnsureTokenizer` |
| §4 resample 24k mono | `loadReferenceAudio` | `AudioIO.LoadReferenceMono24k` |
| §4 auto-transcribe | `ReferenceTranscriber` | `IReferenceTranscriber` / `WhisperTranscriber` |
| §5 saved voices | `VoiceLibrary` | `VoiceLibrary`, `SavedVoice` |
| §6 backup/restore | `BackupManager` | `BackupManager` |
| §7 settings | `SettingsView` | `SettingsViewModel` |
| §8 logs | `LogStore` | `LogStore` |
| §9 busy-close | `WindowCloseGuard` | `MainWindow.OnClosing` |

## Rules

- Keep `src/Core` UI-framework-free (mirrors "keep TTSEngine
  UI-framework-free"). UI lives only in `src/App`.
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
