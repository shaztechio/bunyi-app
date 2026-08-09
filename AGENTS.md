# AGENTS.md — Bunyi (multi-platform)

Guidance for AI agents and developers working in this repository. This is
the canonical entry point; `CLAUDE.md` files just point here.

## What this repo is

Bunyi (formerly "Qwen3 TTS Studio") is a local, no-terminal text-to-speech
desktop app for [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS), for
non-technical users. It ships as **native apps per platform**, kept at
feature parity by a shared specification — **not** shared code (the runtimes
have no common denominator).

> Rename note: the macOS bundle ID stays `com.geppettoforge.Qwen3TTSStudio`
> so existing installs keep their sandbox container (models, saved voices,
> settings). On-disk container subfolders likewise keep the `Qwen3TTSStudio`
> path component. Only user-facing names changed.

```
apps/macos/     Swift + MLX + SwiftUI    → macOS (Apple Silicon)   [built here]
apps/dotnet/    C# .NET + Avalonia + ONNX → Windows AND Linux       [scaffold]
spec/           platform-agnostic feature & data-format specs (SOURCE OF TRUTH)
.github/workflows/  per-platform CI
```

Two codebases, three operating systems, one spec.

## Why no shared code

- **macOS** runs inference on **MLX** (Apple-Silicon/Metal only) via
  `swift-qwen3-tts`; UI is SwiftUI/AppKit; audio/STT are AVFoundation +
  Speech. None of this exists off-Apple.
- **Windows + Linux** run inference on **ONNX Runtime** (DirectML/CUDA/CPU)
  from one C#/.NET Avalonia app. Qwen3-TTS ONNX exports and a C# ONNX
  reference already exist.

Because the inference engine and UI cannot be shared, parity is a
*discipline*, enforced by the spec below.

## The parity rule (read before changing any feature)

1. **`spec/FEATURES.md` is the source of truth** for observable behavior;
   **`spec/DATA-FORMATS.md`** pins on-disk formats so a models folder /
   backup / voices library is interchangeable between apps of the same
   runtime family.
2. **Any feature change updates the spec first, then every app.** A change
   landed in one app but not the spec (and the other app, or a tracked
   follow-up) is incomplete.
3. The macOS app is the **reference implementation** — when the spec is
   ambiguous, match its behavior (`spec/FEATURES.md` cross-references the
   exact Swift source for each feature).

## Where to work

- Building/maintaining **macOS** → `apps/macos/AGENTS.md`.
- Building/maintaining **Windows/Linux** → `apps/dotnet/AGENTS.md`
  (currently a scaffold; the app is not yet implemented).
- Changing **what a feature does** → start in `spec/`, then both apps.

## Status

- **macOS**: complete and building. See `apps/macos/`.
- **.NET (Windows + Linux)**: **scaffold only** — project structure, build
  docs, and stubs that reference the spec. Not yet implemented; cannot be
  built on a Mac. Pick it up on a Windows or Linux machine with the .NET
  SDK per `apps/dotnet/AGENTS.md`.
