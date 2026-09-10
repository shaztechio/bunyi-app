<img src="docs/assets/icon.png" alt="" width="128" align="right">

# Bunyi

**Bunyi** (pronounced *BOON-yee*, IPA /ˈbuːɲi/ — the "ny" is the palatal
nasal, like the "ñ" in *jalapeño* or the "ni" in *onion*) is Malay/Indonesian
for **"sound"**.

Local, no-terminal desktop text-to-speech using
[Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS), for non-technical users:
models auto-download with a progress bar, three modes (preset voices, voice
design, voice cloning), and outputs are playable WAVs. Built to run natively
on **macOS, Windows, and Linux**.

![The Bunyi window in Preset voice mode. A mode switcher across the top —
Preset voice, Voice design, Voice clone, History — above an empty script field
offering three example prompts to click. Below it, rows for Language, Speaker
and Style, and a Generate button reading "Ready — press Command Return to
generate". Doctor, Logs and Help sit in the window
toolbar.](docs/assets/screenshot-macos.png)

Home: [bunyi.app](https://bunyi.app)

**Download:**

| Platform | Current release | Requirements |
|----------|-----------------|--------------|
| [macOS](https://bunyi.app/?os=mac#get) | [![Current macOS release](https://bunyi.app/releases/macos.svg)](https://bunyi.app/?os=mac#get) | Apple Silicon, macOS 15 or later |
| [Windows](https://bunyi.app/?os=win#get) | [![Current Windows release](https://bunyi.app/releases/windows.svg)](https://bunyi.app/?os=win#get) | x64, standard and NVIDIA CUDA builds |
| [Linux](https://bunyi.app/?os=linux#get) | [![Current Linux release](https://bunyi.app/releases/linux.svg)](https://bunyi.app/?os=linux#get) | x64, standard and NVIDIA CUDA builds |

On macOS, drag the signed and notarized app from the `.dmg` to Applications
and launch. On Windows and Linux, extract the portable archive and run Bunyi;
no runtime installation is needed. Windows builds are unsigned; see the
code signing policy below for first-launch instructions.

## How it's structured

Qwen3-TTS has no single cross-platform runtime — MLX is Apple-Silicon only —
so this is a monorepo of **native apps per platform kept at feature parity by
a shared spec**, not shared code.

| Path | Target | Stack | Status |
|------|--------|-------|--------|
| [`apps/macos/`](apps/macos/) | macOS (Apple Silicon) | Swift + MLX + SwiftUI | **working** |
| [`apps/dotnet/`](apps/dotnet/) | Windows **and** Linux | C# .NET + Avalonia + ONNX Runtime | **working** — all three modes |
| [`spec/`](spec/) | all | — | source of truth |

Three operating systems, two codebases, one spec.

## The spec is the source of truth

Feature parity is a discipline, enforced by documents — see
[`AGENTS.md`](AGENTS.md) for the rule. Before changing any feature, read:

- [`spec/FEATURES.md`](spec/FEATURES.md) — every feature and its behavior
- [`spec/DATA-FORMATS.md`](spec/DATA-FORMATS.md) — on-disk layout, `manifest.txt`,
  `voices.json`, backup zip, output WAV (so a models folder or backup is
  interchangeable between apps of the same runtime family)
- [`spec/CLI.md`](spec/CLI.md) — planned agent-facing CLI, machine output,
  download progress, one-shot execution, and persistent server behavior

The staged cross-platform implementation design is in
[`CLI-PLAN.md`](CLI-PLAN.md).

Any feature change updates the spec **and** every app.

## Building

- **macOS** → [`apps/macos/README.md`](apps/macos/README.md) /
  [`apps/macos/AGENTS.md`](apps/macos/AGENTS.md) (XcodeGen + xcodebuild).
- **Windows / Linux** → [`apps/dotnet/AGENTS.md`](apps/dotnet/AGENTS.md)
  (`dotnet build`, .NET 10 SDK). All three modes work. Releases are portable
  self-contained builds — unzip and run, with no runtime to install.

## Code signing policy

Signing differs by platform, because the three do not offer the same thing.

**macOS** — signed with an Apple Developer ID certificate, notarized by Apple,
and stapled. The release workflow verifies both the app and the disk image
before publishing, so Gatekeeper accepts them offline.

**Windows** — **not code-signed.** SmartScreen warns the first time you run
it: choose *More info*, then *Run anyway*. There is no certificate behind the
download, so nothing would make that warning go away, and a page claiming
otherwise would be the one thing worse than the warning. Verify the archive
against the SHA-256 checksum published with each release.

**Linux** — nothing signs a portable tarball in a way the system checks, so
there is no equivalent to claim. Every release publishes a SHA-256 checksum for
the archive, which is what there is to verify against.

**Team roles** — committers, reviewers and approvers:
Shazron Abdullah ([@shazron](https://github.com/shazron)).

**Privacy.** This program does not transfer any information to other networked
systems unless you ask it to. Models are downloaded from the source you choose
the first time you use a mode; generation happens on your own machine, and the
audio never leaves it.

Hosting the models yourself (when the Hub is slow, or you are serving a team):
[`SELF-HOSTING.md`](SELF-HOSTING.md), and [`CACHING.md`](CACHING.md) for
putting that bucket behind a CDN once it is serving — plus what to set up so
the bill cannot surprise you.

CI: [`.github/workflows/`](.github/workflows/) builds macOS (green) and the
.NET matrix for Windows + Linux, both green.
[`release.yml`](.github/workflows/release.yml) builds, Developer ID signs,
notarizes, staples, and publishes the macOS app — tags and manual runs only.
See [`apps/macos/AGENTS.md`](apps/macos/AGENTS.md) for the release and help-book
details.

## License

[Apache-2.0](LICENSE). Every source file carries the license header at the top.
