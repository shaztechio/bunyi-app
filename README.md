# Qwen3 TTS Studio

Local, no-terminal desktop text-to-speech using
[Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS), for non-technical users:
models auto-download with a progress bar, three modes (preset voices, voice
design, voice cloning), and outputs are playable WAVs. Built to run natively
on **macOS, Windows, and Linux**.

## How it's structured

Qwen3-TTS has no single cross-platform runtime — MLX is Apple-Silicon only —
so this is a monorepo of **native apps per platform kept at feature parity by
a shared spec**, not shared code.

| Path | Target | Stack | Status |
|------|--------|-------|--------|
| [`apps/macos/`](apps/macos/) | macOS (Apple Silicon) | Swift + MLX + SwiftUI | **working** |
| [`apps/dotnet/`](apps/dotnet/) | Windows **and** Linux | C# .NET + Avalonia + ONNX Runtime | **scaffold** |
| [`spec/`](spec/) | all | — | source of truth |

Three operating systems, two codebases, one spec.

## The spec is the source of truth

Feature parity is a discipline, enforced by documents — see
[`AGENTS.md`](AGENTS.md) for the rule. Before changing any feature, read:

- [`spec/FEATURES.md`](spec/FEATURES.md) — every feature and its behavior
- [`spec/DATA-FORMATS.md`](spec/DATA-FORMATS.md) — on-disk layout, `manifest.txt`,
  `voices.json`, backup zip, output WAV (so a models folder or backup is
  interchangeable between apps of the same runtime family)

Any feature change updates the spec **and** every app.

## Building

- **macOS** → [`apps/macos/README.md`](apps/macos/README.md) /
  [`apps/macos/AGENTS.md`](apps/macos/AGENTS.md) (XcodeGen + xcodebuild).
- **Windows / Linux** → [`apps/dotnet/AGENTS.md`](apps/dotnet/AGENTS.md)
  (`dotnet build`). Note: the .NET app is currently a **scaffold** —
  structure, build docs, and stubs only; it is not yet implemented and has
  never been built. Pick it up on a Windows or Linux machine with the .NET
  SDK.

CI: [`.github/workflows/`](.github/workflows/) builds macOS (green) and the
.NET matrix for Windows + Linux (red until the app is implemented).
