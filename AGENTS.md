# AGENTS.md — Bunyi (multi-platform)

Guidance for AI agents and developers working in this repository. This is
the canonical entry point; `CLAUDE.md` files just point here.

## What this repo is

Bunyi (formerly "Qwen3 TTS Studio") is a local, no-terminal text-to-speech
desktop app for [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS), for
non-technical users. It ships as **native apps per platform**, kept at
feature parity by a shared specification — **not** shared code (the runtimes
have no common denominator).

> Rename note: the macOS bundle ID is now `app.bunyi.Bunyi` (was
> `com.geppettoforge.Qwen3TTSStudio`). That re-keys the sandbox container, so
> anyone upgrading from an older build starts with an empty one — models,
> saved voices, and settings stay behind in the old container and cannot be
> migrated in code, because a sandboxed app cannot read another app's
> container. The on-disk subfolders were renamed to `Bunyi/Models|Outputs|
> Voices` in the same move — free to do while the container was changing
> anyway, and nothing of the old name survives in the app.

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
   **`spec/CREDITS.json`** is shared *data* rather than shared code: one list
   of the software and models both apps are built on, tagged by which app
   uses each entry, so the two cannot end up crediting different things.
2. **Any feature change updates the spec first, then every app.** A change
   landed in one app but not the spec (and the other app, or a tracked
   follow-up) is incomplete.
3. The macOS app is the **reference implementation** — when the spec is
   ambiguous, match its behavior (`spec/FEATURES.md` cross-references the
   exact Swift source for each feature).

## How changes land: pull requests only

**Every change goes through a pull request.** No direct commits to `main`,
by humans or agents — including docs, spec edits, and one-line fixes.

1. Branch off `main` (`git switch -c <topic>`), commit there, push, and
   open a PR.
2. **The PR title is a Conventional Commit.** `<type>[(scope)][!]: <summary>`
   — e.g. `feat(macos): add a help button to the main window`. Types:
   `feat`, `fix`, `docs`, `refactor`, `perf`, `test`, `build`, `ci`,
   `chore`, `revert`; `!` marks a breaking change. Lowercase after the
   colon, no trailing period, imperative mood ("add", not "adds"/"added").
   This matters because PRs squash: the title *becomes* the commit subject
   on `main` and then a line in the release notes that
   `tools/packaging/release-notes.py` groups by type. A title that skips
   the convention lands under "Other changes" forever.
3. CI (`.github/workflows/`) must run on the PR. The macOS workflow is the
   gate for `apps/macos/` changes.
4. A PR that changes a feature must also carry the `spec/` update the
   parity rule above requires. Reviewers reject feature changes that land
   in one app with no spec change and no tracked follow-up.
5. Squash or merge via the PR — don't push the branch's commits straight
   onto `main` to "save a step". A stacked PR merges **bottom-up**: merging
   the base first strands whatever sits above it, which is how #2's work
   missed `main` entirely and needed #3 to rescue it.

The title rule is documented, not enforced — nothing rejects a
non-conforming title today. Sandfort's `tools/packaging/check-pull-request.py`
implements exactly this check and was deliberately left unported; bring it
over, plus the `pull_request: types: [… edited]` trigger it needs, if the
rule should become a gate.

## Licensing

The project is **Apache-2.0** (`/LICENSE`). Every source file carries the
license header as a comment block at the top — Swift, shell, Python, and the
workflow and project YAML. New files get one too; the wording is identical
everywhere, so copy it from any neighboring file.

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
