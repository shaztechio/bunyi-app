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

# Bunyi CLI specification

**Status: Windows/Linux implementation tracked in #218; native macOS tracked
in #217. Neither CLI has been released yet.**

This document is the source of truth for the observable behavior of the Bunyi
command-line interface. [`FEATURES.md`](FEATURES.md) remains authoritative for
speech generation and product behavior, and [`DATA-FORMATS.md`](DATA-FORMATS.md)
remains authoritative for models, voices, backups, and output WAV files. This
document specifies how those features are exposed to people and agentic
systems through `bunyi`.

The contract is identical on:

- macOS 15 or later on Apple Silicon, using Swift and MLX;
- 64-bit Windows and Linux, using .NET and ONNX Runtime.

Runtime-specific model files and model defaults remain the ones defined in
`FEATURES.md` §3a. An implementation must not substitute the ONNX runtime on
macOS or MLX on Windows or Linux merely to share CLI code.

## 1. General command behavior

The executable is named `bunyi` (`bunyi.exe` on Windows). Commands are
non-interactive unless their documentation explicitly says otherwise. A
missing required value is an error; the CLI never opens a prompt or window to
ask for it.

Global options:

```text
--json             one final JSON document
--jsonl            progress events followed by one final JSON document
--one-shot         do not use a running Bunyi server
--require-server   require a running Bunyi server
--config <file>    settings file override (Windows/Linux)
--help             command help
```

`--json` and `--jsonl` are mutually exclusive. `--one-shot` and
`--require-server` are mutually exclusive.

A model-dependent command uses the already-running server by default. If no
server is running, it executes in the calling process and releases its model
before exit. Starting a server is explicit and never happens as an unnoticed
side effect of another command.

`--detach` requires an already-running server and cannot be combined with
`--one-shot`. An unavailable server is an error for a detached request; the
command never silently runs it synchronously. A failed handshake with an
existing server is also an error, not permission to run a second engine.

Arguments representing filesystem locations accept relative paths but all
reported paths are absolute. Existing input paths are resolved before an
operation begins. A missing or unreadable input fails before model download or
load.

## 2. Output contract

### 2.1 Human output

Without `--json` or `--jsonl`, concise results go to standard output. Progress,
warnings, and diagnostics go to standard error. ANSI styling and animated
progress are used only when the destination is an interactive terminal.

### 2.2 JSON output

`--json` reserves standard output for exactly one JSON object and suppresses
progress output. Diagnostics may still be written to standard error.

Every successful object contains at least:

```json
{
  "schemaVersion": 1,
  "ok": true,
  "type": "result",
  "operation": "generate.preset",
  "operationId": "01K..."
}
```

Every failure exits nonzero and writes:

```json
{
  "schemaVersion": 1,
  "ok": false,
  "type": "error",
  "operation": "generate.preset",
  "operationId": "01K...",
  "error": {
    "code": "missing_input",
    "message": "Type or provide some text to speak."
  }
}
```

An error may add structured `details`, but `code` and `message` are always
present. Messages must not expose credentials or full HTTP authorization
headers.

### 2.3 JSON-lines progress

`--jsonl` reserves standard output for newline-delimited JSON. Each line is a
complete object and is flushed when written. Zero or more event objects are
followed by exactly one `result` or `error` object. No line follows that
terminal object.

Every event contains `schemaVersion`, `type`, `operation`, `operationId`, and
`timestamp`. Progress event types are:

- `queued`;
- `checking`;
- `resolving`;
- `sizing`;
- `downloading`;
- `verifying`;
- `loading`;
- `transcribing`;
- `generating`;
- `finalizing`;
- `playing`;
- `stopping`.

Foreground `server run` also emits `ready` after binding its endpoint.

`checking` identifies local model preparation; `finalizing` identifies writing
the generated audio. Neither means the operation is complete: only the terminal
`result` confirms success. These stages also apply to the resident server's jobs.

A download event may contain:

```json
{
  "schemaVersion": 1,
  "type": "downloading",
  "operation": "models.download",
  "operationId": "01K...",
  "timestamp": "2026-09-10T10:14:32.123Z",
  "item": { "id": "preset", "index": 1, "count": 4 },
  "bytesCompleted": 2147483648,
  "bytesTotal": 15738000000,
  "rateBytesPerSecond": 8200000,
  "etaSeconds": 1656,
  "currentFile": "talker_decode.onnx.data"
}
```

Byte totals are integers. Unknown totals and ETAs are `null`, not zero. Within
one operation, `bytesCompleted` never decreases. Rate and ETA are estimates.
Completed bytes measure unique usable or transferred file coverage, including
reused files and resumable prefixes, rather than counting retry traffic twice.
Known totals remain stable after sizing. A required file with unknown size
makes the aggregate total unknown until completion; a known subtotal must not
be presented as the total. Download progress is throttled to avoid flooding
agent context, with immediate phase changes and an unthrottled terminal result.
Generation progress reports frames and seconds of audio produced so far rather
than inventing a completion percentage.

### 2.4 Process exit status

- `0`: success;
- `2`: invalid command or arguments;
- `3`: preflight blocker or missing required input;
- `4`: server unavailable, busy, or protocol-incompatible;
- `5`: cancelled;
- `10`: download, model, transcription, generation, or filesystem failure.

CLI/server generation has no automatic frame budget; it waits for EOS or user
cancellation. If a diagnostic caller explicitly supplies a limit, exhaustion returns
`generation_did_not_finish` with exit code `10`, including the same retry advice
as the desktop app. It creates no output file or successful result. A resident
server records the job as failed and remains available for another job. All
CLI/server generation also uses the shared output clipping protection in
`FEATURES.md` §2.

Download waits follow `FEATURES.md` service recovery: JSONL emits `waiting`
events with `retryAt`, `retryAfterSeconds`, `retryAttempt`, and `host`, preserving
available byte counters. Cancellation remains exit 5. A mirror pause/unavailable
service returns `download_service_unavailable`; exhausted or excessive 429 waits
return `download_rate_limited`, both exit 10, with retry timing when available.
No source changes occur implicitly. For an exact built-in mirror configuration,
the error offers the canonical upstream repository and the `config set`
instruction for the affected mode (plus an argument array preserving the active
configuration file). The user explicitly applies it and reruns the
command; the server remains usable after the failed job.

The JSON error code is the stable programmatic reason. Exit status only groups
reasons broadly.

## 3. Generation

Three subcommands expose the modes in `FEATURES.md` §1:

```text
bunyi generate preset
bunyi generate design
bunyi generate clone
```

Every generation accepts exactly one text source:

- `--text <text>`;
- `--text-file <path>`;
- standard input when `--stdin` is present.

Common options are `--language <language>`, `--json`, `--jsonl`,
`--one-shot`, and `--require-server`. Languages and their spelling are the
list in `FEATURES.md` §1.

Preset voice accepts `--speaker <speaker>` and optional `--style <text>`.
When omitted, the speaker is the default defined by the platform's model.
Voice design requires `--voice <description>`. Voice clone requires
`--reference <audio-path>` and either `--transcript <text>` or
`--auto-transcribe`.

Alternatively, clone accepts `--saved-voice <id>` from `voices list`. It cannot
be combined with reference or transcript options.

Required inputs are validated before Doctor, model download, or model load.
The same preflight checks as desktop generation run before downloading its
TTS model. Explicit auto-transcription first prepares Whisper (with its own
download disk-space check), transcribes the reference, and disposes Whisper
before loading the TTS model.

The successful result contains:

- absolute `outputPath`;
- `mode`;
- `durationSeconds`;
- `frames`;
- `elapsedSeconds`;
- effective `modelSource`;
- the metadata fields embedded in the WAV.

The file is written to the platform's Bunyi Outputs folder, named and tagged
according to `DATA-FORMATS.md`. A one-shot invocation unloads and disposes the
model after the result has been written. A server invocation retains the model
but releases generation working memory.

## 4. Transcription and speakers

`bunyi transcribe <audio-path> [--language <language>]` returns the transcript
as text or JSON. It processes the full clip; clone-reference auto-transcription
uses the same ten-second trim as reference preparation in the desktop app.
Transcription is local on every platform. The macOS implementation requires
on-device Speech recognition or uses a local Whisper fallback; it never sends
reference audio to Apple's server.

If macOS Speech permission has not been granted, non-interactive use fails
with `speech_permission_required` and tells the user how to grant it. A
background server never attempts to present an authorization prompt.

`bunyi speakers` returns the speakers supported by the configured preset
model. It may load that model. One-shot use unloads afterward; server use may
retain it.

## 5. Models

`models list` enumerates downloaded models. `models status` reports the source,
folder, completeness, missing or partial files, approximate size, and whether
the model is loaded by the server.

`models download --mode <mode>` downloads one mode without loading it.
`models download --all` downloads:

- preset voice;
- voice design;
- voice clone;
- every separate transcription model that implementation needs.

The macOS Speech framework is an operating-system service and is not a model
asset. If the macOS CLI instead uses a downloaded Whisper model, that model is
part of `--all`. The result lists the assets considered so an agent never has
to assume whether there are three or four.

An all-model download has two phases:

1. inspect local completeness, resolve manifests, obtain available sizes, and
   check aggregate free disk space;
2. download assets sequentially with resume, checksum, stall, and cancellation
   behavior from `FEATURES.md` §3b.

Progress reports current-asset and aggregate byte counts. Reused complete
bytes count as completed. Completion means every required file passes the
runtime family's `DATA-FORMATS.md` completeness rule; it does not mean the
model was loaded.

`models verify --mode <mode>` performs the on-demand integrity check from
`FEATURES.md` §11. `models remove` refuses to delete a loaded model until it is
unloaded, uses the platform's recoverable Trash operation where available,
and returns the removed folder.

## 6. Persistent server

```text
bunyi server run
bunyi server start
bunyi server status
bunyi server preload --mode <mode>
bunyi server unload
bunyi server stop
```

`run` stays in the foreground and is suitable for a service supervisor.
`start` launches the same server in the background, waits until it accepts a
status request, then returns. Starting an already-running compatible server is
a successful no-op. A stale server record is repaired automatically.
`run` emits a ready event in JSON-lines mode after binding the endpoint; it is
a long-running stream whose terminal result is graceful shutdown. In JSON
mode it writes only the shutdown result. `start` emits its bounded result only
after readiness has been verified. Neither form installs a login service.
On Linux, a supervisor's SIGTERM requests the same cooperative shutdown as
`server stop`; it does not report stopped while model work is still active.

The server:

- accepts connections only from the same local operating-system user;
- exposes no TCP listener by default;
- executes at most one model operation at a time;
- keeps at most one model loaded;
- queues accepted operations in FIFO order;
- exposes its process ID, protocol version, loaded mode, active operation,
  queue length, and uptime through `status`;
- releases generation working memory after every run;
- unloads the model before graceful exit.

The ONNX server bounds its waiting queue at 32 jobs and rejects excess work
with a structured busy error. It retains up to 256 terminal job records in
memory; older records may be evicted. Progress streams coalesce updates for
slow readers (64 buffered events); terminal state is never dropped. Clients
must not interpret a missing intermediate progress update as a failure.

`preload` downloads if needed and loads a model without generating. `unload`
waits for active model work to finish or be cancelled, then releases it.

The transport and wire protocol are private implementation details, but client
and server perform an explicit protocol-version handshake. An incompatible
client fails with `server_protocol_mismatch` rather than sending an operation
that may be misread.

## 7. Detached jobs

A long-running server-capable command accepts `--detach`. It returns once the
server has accepted the job:

```json
{
  "schemaVersion": 1,
  "ok": true,
  "type": "accepted",
  "operation": "models.download",
  "operationId": "01K..."
}
```

`jobs status <operation-id>` returns the latest event plus queued, running,
succeeded, failed, or cancelled state. `jobs follow <operation-id> --jsonl`
emits the current snapshot, subsequent changes, and the terminal object.
`jobs cancel <operation-id>` requests cancellation.

A disconnected attached client cancels its operation. A detached operation
continues. If the server stops during a download, files already accepted remain
and partial files resume on the next request. Completed job records may be
discarded when the server exits; model and output files are the durable record.

## 8. Other feature commands

### Audio playback

```text
bunyi play <audio-path> [--json | --jsonl]
```

Plays a local WAV, MP3, or FLAC file through the calling user's default audio
output, without opening an external player window. Bunyi-generated WAV files
are supported, including embedded metadata. The command waits until playback
finishes, then releases its player and audio device before returning success.
It never changes the file, creates a history entry, downloads or loads models,
or acquires a model-folder lease.

Playback always runs in the calling process, even when a model server is
running. `--one-shot` is accepted but redundant; `--require-server` and
`--detach` are rejected (exit 2). This is playback on the machine running the
CLI, not remote audio streaming. Generation remains silent unless an agent or
user separately invokes `play` on the returned `outputPath`.

A missing or unreadable local file fails with `missing_input` (exit 3).
An empty, corrupt, unsupported audio file, unavailable audio output, or other
playback failure returns `playback_failed` (exit 10), never a false success
through a silent/null backend. Ctrl+C, and SIGTERM on Linux, stops playback and
releases resources before returning `cancelled` (exit 5).

JSON-lines mode emits `playing` events with `positionSeconds` and
`durationSeconds`, at most four per second. A successful terminal result has
`operation: "play"`, absolute `inputPath`, `durationSeconds`, and
`played: true`. It means the audio backend completed playback, not that a
listener heard it (system volume or hardware may be muted). JSON mode emits
only the terminal object. Native macOS implementation follows the same
contract in #217.

### Library and maintenance commands

The exact secondary command forms are:

```text
bunyi voices add --name <name> --reference <path> (--transcript <text> | --auto-transcribe)
bunyi voices remove <voice-id>
bunyi history show|remove <output-path>
bunyi backup create|restore <zip-path>
bunyi config get <key>
bunyi config set <key> <value>
bunyi logs tail [--lines <count>]
```

Configuration keys are `modelsFolder`, `unloadOnModeSwitch`, and
`modelSource.preset|design|clone`. Setting a models folder requires an existing
directory. Backup creation refuses to overwrite an existing destination.

- `voices list|add|remove` uses the saved-voice behavior and `voices.json`
  format in the shared specs. Adding a voice accepts a transcript or explicit
  auto-transcription.
- `history list|show|remove` reads the Outputs folder as the record and parses
  embedded WAV metadata. Results are newest first.
- `doctor` exposes every finding, severity, and suggested action. `--deep`
  enables checksum verification.
- `backup create|restore` preserves stored ZIP, merge, progress, and Stop
  behavior. Paths are explicit; a headless command never opens a file picker.
- `config get|set|list` exposes model sources, models folder, and model-unload
  policy without editing storage files directly.
- `logs path|tail|clear` returns or operates on the CLI runtime's durable log.
  `tail` does not pollute another command's JSON output.

Destructive commands require an exact identifier or path. They do not accept
an unqualified `--all` unless that behavior is separately specified and tested.

## 9. Storage and compatibility

Windows and Linux use the existing data and settings roots in
`DATA-FORMATS.md`, so the Avalonia app and CLI share models, outputs, voices,
and configuration.

For isolated automation, Windows/Linux accept `BUNYI_DATA_DIR` as an absolute
directory overriding both data and configuration roots. `--config <file>`
overrides the settings file only (`BUNYI_CONFIG_FILE` is its environment
equivalent). Use the same overrides for server startup and subsequent
commands: the local endpoint is scoped to the user and those overrides.
Custom profiles still respect the shared lease if they select the same models
folder. Neither override migrates existing desktop data.

The standalone macOS CLI is not entitled to the desktop app's private sandbox
container. Its defaults are:

```text
~/Library/Application Support/Bunyi/
  Models/
  Outputs/
  Voices/
  ModelConfigs/
  Logs/
  settings.json
```

This has the same subtree formats as the desktop app but is physically outside
`~/Library/Containers/app.bunyi.Bunyi`. A common custom models folder may be
used explicitly. The desktop app must receive access through its folder picker
and security-scoped bookmark; the CLI must not copy or manufacture a bookmark.

Transient server metadata and job snapshots are not part of the portable data
format and are never included in backup archives.

## 10. Concurrency, cancellation, and recovery

All Bunyi processes using one models folder share an exclusive operation
lease for model mutation and inference. Read-only history, model-status, and
configuration commands do not require it. A contending process waits only when
asked to; otherwise it returns `bunyi_busy` with the owning operation where
available.

A process retains the lease while a model remains resident, even between
generations. This prevents another process from deleting or replacing its
memory-mapped files. `server unload`, `server stop`, or unloading the desktop
model releases that lease. Commands within the owning runtime can reuse it;
model mutations still wait for that runtime's active inference to finish.
The Windows/Linux implementation establishes this protection between its
updated desktop app and CLI. Native macOS adoption is tracked in #217.

Ctrl+C and `jobs cancel` request cooperative cancellation. The operation
reports `stopping` until inference has actually stopped using its model. It
must not report idle early, unload a model still in use, or admit a second
generation against it.

Cancellation never deletes a previously complete file or a successful WAV.
Download partials remain in the runtime family's resumable format. A checksum
mismatch is discarded as required by `DATA-FORMATS.md`.

## 11. Privacy and security

Generation, transcription, and audio processing happen on the local machine.
Network access is limited to model downloads. The CLI never sends generated or
reference audio to a network service; the Bunyi server process is local to the
user.

The server endpoint is owner-only. Server metadata contains no prompts,
reference transcripts, generated audio, credentials, or authorization headers.
Logs follow `FEATURES.md` §8 and redact secrets while retaining actionable
model and filesystem errors.

## 12. Agent integration contract

OpenClaw, Hermes Agent, and other integrations invoke `bunyi`; they do not
depend on the private server wire protocol. Integration instructions use:

- `--json` for bounded commands;
- `--jsonl` for attached progress;
- `--detach` followed by `jobs status` when the host has short execution
  timeouts;
- absolute paths from results rather than guessing Bunyi's data directory;
- `server status` before requiring model residency;
- `models download --all` when preparing a machine for offline operation.

An integration must treat the process exit status and terminal JSON object as
authoritative. A progress percentage or disconnected stream is not evidence
that an operation succeeded.
