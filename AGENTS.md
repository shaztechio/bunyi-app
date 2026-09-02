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
apps/dotnet/    C# .NET + Avalonia + ONNX → Windows AND Linux       [built here]
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

### It is the runtime that cannot be shared, not the UI toolkit

"Avalonia is Windows and Linux only" would be a tidier reason, and it is not
the reason — Avalonia runs on macOS perfectly well. Every native dependency
this app has already ships an Apple Silicon build: `Avalonia.Native`, ONNX
Runtime, SoundFlow, and Whisper.net, the last of those with a Metal backend.
The OS-conditional surface in `apps/dotnet/src` is about a dozen sites in four
files, several of which already carry a macOS branch. A port would not be hard.

**macOS stays Swift because MLX has no cross-platform equivalent.** On Apple
Silicon, MLX puts the model on the GPU with unified memory. An Avalonia build
would land on the CPU for two independent reasons already recorded in
`apps/dotnet/RESEARCH-ONNX.md`: the vocoder graph runs on the CPU execution
provider under every runtime, because `node_pad_1` computes a negative
dimension that only the CPU kernel tolerates; and the talker's int4 weights use
`MatMulNBits`, a contrib op a CoreML execution provider is not expected to
accept. The second half is inference from the op set rather than a measurement.

The measured CPU cost is **RTF 5.0 and a 17.7 GB peak working set** on long
text — on a 16 GB Mac that may not fit at all. So the trade is retiring a
native app that uses the hardware for a portable one that ignores it.

**The MLX figure on the same text: RTF 1.16**, mean of three warm runs ranging
0.96–1.53, on an Apple M3 with 16 GB, generating *"Hello! We'll begin in just a
few minutes."* through Preset voice's 0.6B CustomVoice model. The ONNX CPU
provider is **RTF 5.68** on that same text. Roughly five times, and the
absolute matters as much as the ratio: MLX is at realtime, so the wait a user
notices is the 2.6 s model load rather than the speech. Including that load a
cold run is RTF 2.14. Measured 2026-08-24 from the shipping app's own log.

Memory is not directly comparable — MLX reports 2.45 GB resident with 3.3–5.0
GB of buffer cache released after each run, against a Windows *peak working
set* of 8.73 GB for the same short text — but nothing suggests MLX is the
heavier of the two.

**This is settled, and no further measurement is planned.** The remaining
imprecision — the ONNX numbers come from Windows 11 with an RTX 4090, not from
a Mac — cannot change the outcome, because speed was never the binding
constraint. **Memory is.** The CPU provider peaked at 17.7 GB on 22 seconds of
audio from the *smallest* model, and it grows with output length because the KV
cache grows per frame. A 16 GB Mac is the common case, not the edge case, so an
ONNX build there would fail on the machines it most needs to work on.

So MLX stays on macOS on resource grounds first and speed second, and the
fivefold gap is a supporting figure rather than the argument. A same-machine
run would refine a number that is not deciding anything.

ONNX Runtime's WebGPU provider was the last unmeasured GPU path, and it is now
closed: the packages the app can reference (`Microsoft.ML.OnnxRuntime` 1.29 and
its `.Gpu` twin) do not carry it — `AppendExecutionProvider("WebGPU")` answers
"not supported in this build" — so there is nothing to measure without
building ONNX Runtime ourselves. See `apps/dotnet/RESEARCH-ONNX.md`.

Two smaller facts worth having: ONNX Runtime 1.29 ships no `osx-x64` build, so
a port would not extend reach to Intel Macs; and the macOS app is sandboxed,
while .NET has no security-scoped bookmark API, so a relocatable models folder
there would need P/Invoke into AppKit or dropping the sandbox — which would
strand every existing user's models in an orphaned container a second time.

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

The title rule is enforced. `tools/check-pull-request.py` runs on every pull
request from `.github/workflows/pull-request.yml`, on `opened`, `edited`,
`reopened` and `synchronize` — `edited` being the one that is not on by
default and the one that matters, since a title fixed after a red run has to
re-run the check.

The **structural** half is the gate: the type, the colon and space, a lowercase
summary, no trailing full stop. The **imperative mood** half is a warning
beside it rather than part of the gate, because a regex cannot tell a verb from
a noun — `fix(site): downloads for every platform` is in this history, and an
earlier version of the checker rejected it. A gate that blocks correct work
gets deleted, and takes the mechanical rules with it.

### Verifying signatures locally

Commits made locally are SSH-signed, and SSH verification needs a file naming
the keys to trust. Point git at the one in this repo, once per clone:

```sh
git config gpg.ssh.allowedSignersFile .allowed_signers
```

Without it `git verify-commit` and `git log --show-signature` fail with
*"gpg.ssh.allowedSignersFile needs to be configured and exist"*, which reads
like a broken setup rather than a missing setting.

**This covers locally-authored commits only.** Commits on `main` are squash
merges, committed by `GitHub <noreply@github.com>` and signed with GitHub's
*PGP* key — a different mechanism, needing gpg and GitHub's public key rather
than anything here. The web UI verifies those; locally they will report
`cannot run gpg` unless you have set that up separately, and that is expected
rather than a problem to fix.

## Licensing

The project is **Apache-2.0** (`/LICENSE`). Every source file carries the
license header as a comment block at the top — Swift, shell, Python, and the
workflow and project YAML. New files get one too; the wording is identical
everywhere, so copy it from any neighboring file.

## Where to work

- Building/maintaining **macOS** → `apps/macos/AGENTS.md`.
- Building/maintaining **Windows/Linux** → `apps/dotnet/AGENTS.md`.
- Changing **what a feature does** → start in `spec/`, then both apps.

## Status

- **macOS**: complete and building. See `apps/macos/`.
- **.NET (Windows + Linux)**: complete and building. All three modes, the
  history, settings, saved voices, backup and restore, Doctor, logs and help.
  Cannot be built on a Mac — it needs a Windows or Linux machine with the
  .NET SDK, per `apps/dotnet/AGENTS.md`.

  One known gap, tracked rather than hidden: macOS passes the style
  instruction to the preset-voice model and this app does not, because the
  library driving that export refuses it on the 0.6B variant. Design and
  clone modes are unaffected. Closing it means driving preset voice through
  this app's own pipeline, which `apps/dotnet/RESEARCH-ONNX.md` scopes.
