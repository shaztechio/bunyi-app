# AGENTS.md — Windows + Linux app (.NET + Avalonia + ONNX)

> **Status: SCAFFOLD ONLY — not yet implemented, never built.**
> Everything here is structure + intent. There is no working app. It was
> authored on macOS with no .NET/Windows/Linux toolchain, so nothing has
> been compiled. Build and implement this on a Windows or Linux machine with
> the .NET SDK, filling the stubs against `/spec/FEATURES.md`.

One C#/.NET application that targets **both Windows and Linux** from a single
codebase. It must implement the exact behavior in `/spec/FEATURES.md` and
the on-disk formats in `/spec/DATA-FORMATS.md`. The macOS app
(`/apps/macos/`) is the reference — match it when the spec is ambiguous.

## Stack

- **UI:** [Avalonia](https://avaloniaui.net) (renders natively on Windows &
  Linux). MVVM.
- **Inference:** [ONNX Runtime](https://onnxruntime.ai)
  (`Microsoft.ML.OnnxRuntime`) with the DirectML EP on Windows and CUDA/CPU
  on Linux. Qwen3-TTS **ONNX** exports:
  - `xkos/Qwen3-TTS-12Hz-1.7B-ONNX`
  - `sivasub987/Qwen3-TTS-0.6B-ONNX-INT8`
  - `arubeh/qwen3-tts-12hz-1.7b-base-onnx`
  Reference C# ONNX pipeline: `elbruno/ElBruno.QwenTTS`.
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
- **ONNX defaults differ from MLX**: pick ONNX repos for the default
  per-mode sources; the source-selection UX is identical to macOS.
