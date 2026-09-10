<!--
Copyright 2026 Shazron Abdullah and Bunyi contributors

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
-->

# Bunyi CLI implementation plan

This plan adds an agent-oriented `bunyi` command for OpenClaw, Hermes Agent,
and other systems that can invoke local executables. The normative command,
output, progress, and server behavior is in [`spec/CLI.md`](spec/CLI.md).

The CLI has the same observable contract on every supported platform, but it
does not introduce shared runtime code:

- macOS uses a native Swift/MLX executable on Apple Silicon.
- Windows and Linux use a C#/.NET executable over the existing ONNX runtime.
- both implementations consume the existing feature and data-format specs.

The CLI covers Bunyi's non-visual functionality. Window appearance, playback
controls, dialogs, and revealing a file in Finder or Explorer are GUI
affordances rather than headless operations; the CLI returns absolute paths
and structured results instead.

Audio playback itself is exposed by `bunyi play <audio>`: foreground local
playback with completion, cancellation, and structured errors. It is separate
from generation and does not require the model server.

## Tracking issues

- [#217 — native macOS CLI and persistent MLX server](https://github.com/shaztechio/bunyi-app/issues/217)
- [#218 — Windows/Linux CLI and persistent ONNX server](https://github.com/shaztechio/bunyi-app/issues/218)

The Windows/Linux implementation and standalone distribution guide are in
[`apps/dotnet/CLI.md`](apps/dotnet/CLI.md). Native macOS remains the separate
#217 implementation; this plan does not replace MLX with ONNX on macOS.

## Product shape

One command supports two execution paths:

```text
bunyi command
     |
     +-- one-shot --> platform runtime --> model --> WAV --> unload --> exit
     |
     +-- server ----> local IPC --> persistent runtime --> keep one model loaded
```

Normal model-dependent commands use the server when it is already running and
otherwise run one-shot. `--one-shot` always bypasses the server;
`--require-server` refuses to proceed without it. Starting a background
process is never an implicit side effect: `bunyi server start` does that
explicitly.

The server keeps at most one model resident. Repeated work in one mode avoids
the load cost; changing mode unloads the previous model before downloading or
loading the next. `server stop` gracefully cancels or drains work, unloads the
model, and exits.

## Shared command surface

The first release should implement the complete surface before being described
as feature-equivalent:

```text
bunyi generate preset
bunyi generate design
bunyi generate clone

bunyi models list
bunyi models status
bunyi models download --all
bunyi models download --mode <mode>
bunyi models verify --mode <mode>
bunyi models remove --mode <mode>

bunyi speakers
bunyi transcribe <audio>
bunyi play <audio>

bunyi voices list|add|remove
bunyi history list|show|remove
bunyi doctor [--mode <mode>] [--deep]
bunyi backup create|restore
bunyi config get|set|list
bunyi logs path|tail|clear

bunyi server run|start|status|preload|unload|stop
bunyi jobs status|follow|cancel <operation-id>
bunyi version
```

Generation gets mode-specific arguments so an agent cannot construct an
internally contradictory request:

```text
bunyi generate preset --text "Hello" --speaker Ryan --style "calm"
bunyi generate design --text "Hello" --voice "warm documentary narrator"
bunyi generate clone --text "Hello" --reference voice.wav --transcript "..."
bunyi generate clone --text "Hello" --reference voice.wav --auto-transcribe
```

Script text also comes from `--text-file` or standard input. Exactly one of
the three text sources is accepted. Successful generation returns the
canonical WAV in Bunyi's Outputs folder rather than silently moving it outside
History's folder-of-record.

## Agent output and long-running work

All commands have three output forms:

- default human text, with progress and diagnostics on standard error;
- `--json`, one final JSON document on standard output;
- `--jsonl`, flushed progress events followed by one final result on standard
  output.

Machine-readable standard output must never contain logs, ANSI styling, or
unstructured warnings. Every event carries `schemaVersion`, `type`,
`operationId`, and `operation`. Failures use stable string error codes and a
nonzero process exit status.

An attached command streams progress until it finishes. An agent whose command
runner has a short timeout starts work through the server with `--detach`, then
uses `jobs status` to poll or `jobs follow` to resume a JSON-lines stream.

`models download --all` includes the three mode models and every separately
downloaded transcription asset needed by that platform. It first plans the
whole transfer and checks aggregate free space, then downloads sequentially.
Progress reports both the current asset and monotonic aggregate bytes. Existing
complete files and resumable partials count toward completed bytes.

## Runtime work shared within each platform

The desktop UI and CLI must compose the same platform runtime rather than
copying model defaults, source parsing, Doctor wiring, generation metadata, or
download rules.

Both runtime families need these headless abstractions:

1. A runtime facade that owns settings, model source resolution, downloader,
   Doctor, transcription, model lifecycle, output writing, and disposal.
2. A download planner that resolves manifests and sizes before execution and
   emits aggregate progress across several assets.
3. A presentation-neutral event model for downloading, loading,
   transcribing, generating, stopping, completion, and failure.
4. A cross-process lease in the models root. The desktop app, local CLI, and
   server all take it before model mutation or inference so two processes
   cannot write the same partial file or exhaust memory with concurrent runs.
5. A server operation registry with bounded FIFO execution, cancellation,
   current progress, and terminal results.

The existing partial-file, resume, checksum, manifest path-safety, offline
reuse, and stall-detection behavior remains authoritative.

## Windows and Linux implementation

Add `apps/dotnet/src/Cli/Bunyi.Cli.csproj`, producing `bunyi.exe` on Windows
and `bunyi` on Linux. The executable contains both the command client and the
internal server process; there is no second server binary.

Refactor the composition currently in `apps/dotnet/src/App/App.axaml.cs` into
a UI-free runtime facade under `src/Core`. This moves the three default model
sources, current-settings lookup, models-folder resolution, synthesizer
factory, Doctor probe, and Whisper setup into one place. The Avalonia app then
uses that facade without changing its behavior.

Use local, per-user IPC rather than TCP:

- a current-user named pipe on Windows;
- a Unix-domain socket with owner-only permissions on Linux.

The protocol is newline-framed, versioned JSON. One connection can receive
progress events and a terminal result for its request. A detached operation
continues after the client disconnects and is addressable by operation ID.

The .NET `--all` set is preset voice, voice design, voice clone, and the
Whisper base transcription model. The downloader should gain a plan/execute
split rather than a second download implementation.

Publish CLI-only, self-contained archives separately from the Avalonia app so
headless installations do not carry UI dependencies. Produce CPU and CUDA
variants for `win-x64` and `linux-x64`, with the same CUDA fallback policy as
the desktop app.

## macOS implementation

The macOS CLI is native Swift/MLX, Apple Silicon only, with the same macOS 15
minimum as the desktop app. It must not route macOS through the ONNX codebase:
MLX model residency and unified-memory performance are the reason the native
implementation exists.

### Runtime extraction

Split the non-visual work out of the current `@MainActor @Observable`
`TTSEngine` into a `BunyiMLXCore` target:

- `MLXTtsRuntime`, an actor that owns the non-Sendable Qwen model and serializes
  model access;
- model source, path, completeness, manifest, download, and tokenizer services;
- reference-audio decoding and resampling;
- output naming, WAV writing, and embedded metadata;
- Doctor, histories, voices, backup/restore, and logging services;
- common command progress and result types.

The SwiftUI `TTSEngine` becomes a thin observable adapter over this runtime,
marshalling events onto the main actor. The CLI calls the actor directly.
Cancellation retains the existing truthfulness rule: it reports `stopping`
until upstream inference has actually released the model. After every run MLX
working buffers are cleared, while the server's loaded model remains resident.

Add a `BunyiCLI` command-line target in `apps/macos/project.yml`, depending on
`BunyiMLXCore` and the same pinned `swift-qwen3-tts` package. Its product name
is `bunyi`. Add a native Unix-domain-socket server using owner-only
permissions and the same JSON protocol and job behavior as the .NET server.

### macOS storage

A standalone CLI cannot silently share the desktop app's private sandbox
container: Apple documents each sandbox container as private to its owning app
and App Groups as the explicit sharing mechanism. Its default data root is the nonsandboxed
`~/Library/Application Support/Bunyi`, with the existing `Models`, `Outputs`,
`Voices`, and `ModelConfigs` subfolders. The desktop app retains its current
sandbox container.

An explicit models-folder setting allows the user to point both products at a
common folder. The desktop side must obtain that access through its existing
security-scoped folder picker. The shared cross-process lease then protects
that folder. Do not move the desktop app to an App Group as part of this work;
that would be a separate storage migration involving every existing model.

### Transcription research gate

Before implementing `transcribe` and clone `--auto-transcribe`, build and sign
a minimal command-line probe for `SFSpeechRecognizer` and verify all of these
on a clean macOS 15 account:

- the authorization prompt has usable identity and purpose text;
- a foreground CLI can request permission successfully;
- a background server can reuse an existing grant without UI;
- cancellation terminates recognition;
- recognition is forced on-device and never sends reference audio to Apple's
  server.

If a notarized standalone executable cannot obtain a reliable Speech grant,
or if an on-device recognizer is unavailable, use a local Whisper
implementation for the CLI and include its model in `models download --all`.
Do not weaken clone validation: without either an explicit transcript or
successful auto-transcription, generation fails before any multi-gigabyte
model download begins.

### macOS distribution

Produce a dedicated signed and notarized CLI archive rather than hiding the
only copy inside `Bunyi.app`. Package the executable with its required MLX
frameworks, Metal library, licence notices, and shell-completion files. Extend
the release workflow to verify:

- Developer ID signatures on the executable and every nested framework;
- hardened runtime;
- notarization of the distributable;
- `arm64` only and the macOS 15 deployment target;
- executable permissions after archive extraction;
- a `bunyi version --json` smoke test before publication.

Whether a stapled installer package is preferable to a notarized tar archive
is a small packaging spike, decided before release rather than during runtime
work.

## OpenClaw and Hermes integration

Ship small, separate skill directories for OpenClaw and Hermes Agent after the
CLI contract is stable. Each skill should teach only:

- when to use preset, design, or clone;
- how to pass text and reference files safely;
- `--json` for bounded calls and `--jsonl` for attached progress;
- server start/status/stop and one-shot overrides;
- `models download --all --detach` plus job polling;
- that returned audio paths are local and absolute.

The first release does not need MCP. Both target systems can invoke a CLI from
their terminal tool and load workflow skills ([OpenClaw tools](https://docs.openclaw.ai/tools),
[Hermes tools reference](https://hermes-agent.nousresearch.com/docs/reference/tools-reference)).
Keeping the local server protocol private leaves a later thin MCP adapter
possible without coupling the core implementation to one agent framework.

Apple references for the macOS storage decision:
[App Sandbox](https://developer.apple.com/documentation/Security/app-sandbox) and
[Configuring App Groups](https://developer.apple.com/documentation/xcode/configuring-app-groups).

## Verification

Tests exist in both implementations for the same contract cases:

- argument validation and mode-specific required inputs;
- JSON and JSON-lines schema fixtures, stdout purity, and stable errors;
- progress monotonicity, resumed bytes, exact terminal events, and cancellation;
- all-model membership for each runtime family;
- complete offline reuse, checksum mismatch, unsafe manifests, and partials;
- one-shot disposal and model unload;
- one server load for two same-mode generations;
- mode-switch unload, explicit preload/unload, graceful stop, and stale-server recovery;
- detached status, follow, cancel, and client disconnect behavior;
- FIFO serialization and cross-process exclusion with the desktop app;
- local-user-only IPC;
- data-format compatibility for models, voices, backups, and WAV metadata;
- self-contained release artifacts and `version --json` smoke tests on every OS.

Large real-model runs remain release smoke tests rather than CI fixtures. Unit
and integration tests use fake synthesizers, temporary models, and local model
servers so Windows, Linux, and macOS CI stay deterministic.

## Landing sequence

Every stage is a pull request with a Conventional Commit title:

1. `docs(spec): define the bunyi cli contract`
2. `refactor(dotnet): extract the headless bunyi runtime`
3. `feat(dotnet): add one-shot bunyi cli generation`
4. `feat(dotnet): add cli model downloads and progress`
5. `feat(dotnet): add the persistent cli server and jobs`
6. `feat(dotnet): complete the bunyi cli command surface`
7. `refactor(macos): extract the mlx runtime from the app`
8. `feat(macos): add one-shot bunyi cli generation`
9. `feat(macos): add cli downloads, progress, and transcription`
10. `feat(macos): add the persistent cli server and jobs`
11. `feat(macos): complete and package the bunyi cli`
12. `docs(cli): add openclaw and hermes agent skills`

The CLI is announced only after both runtime families pass the shared contract
suite. Earlier PRs may ship behind an undocumented preview flag, but must not
claim cross-platform parity while only one implementation exists.
