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

# Bunyi CLI for Windows and Linux

`bunyi` exposes Bunyi's local audio functionality without opening the desktop
app. The CLI and desktop use the same ONNX runtime and storage formats. The
cross-platform command contract is [spec/CLI.md](../../spec/CLI.md); the native
Swift/MLX implementation is tracked separately in [#217](https://github.com/shaztechio/bunyi-app/issues/217).

## Install or build

Extract the **entire** `Bunyi-CLI-<version>-win-x64.zip` or
`Bunyi-CLI-<version>-linux-x64.tar.gz` archive. Run `bunyi.exe` on Windows or
`./bunyi` on Linux, or add the extracted folder to PATH. These standalone
packages include .NET and audio/model native libraries, but not Avalonia or
model weights. The optional `-cuda` archives have the same CUDA requirements
as the desktop builds. Release automation builds and smoke-tests both kinds.

From this directory with the .NET 10 SDK:

```text
dotnet run --project src/Cli -- --help
dotnet publish src/Cli -c Release -r win-x64 --self-contained -o artifacts/cli-win-x64
dotnet publish src/Cli -c Release -r linux-x64 --self-contained -o artifacts/cli-linux-x64
```

## One-shot or resident

```text
bunyi generate preset --text "Hello from Bunyi" --speaker Ryan --one-shot --json
bunyi generate design --text "Welcome" --voice "A warm documentary narrator" --json
bunyi generate clone --text "Welcome back" --reference /absolute/reference.wav --transcript "Words in the reference" --json
```

Use `--text-file <path>` or `--stdin` in place of `--text`. Results contain the
absolute WAV path, timings, model information, and embedded metadata. A
one-shot invocation loads what it needs, generates, disposes its runtime, and
exits. Reference audio stays local. `--auto-transcribe` explicitly requests
local Whisper transcription instead of a supplied reference transcript.

To reuse the loaded model:

```text
bunyi server start --json
bunyi server preload --mode preset --jsonl
bunyi generate preset --text "First sentence" --require-server --json
bunyi generate preset --text "Second sentence" --require-server --json
bunyi server status --json
bunyi server unload --json
bunyi server stop --json
```

Without `--one-shot` or `--require-server`, model commands reuse an existing
compatible server and otherwise run once. They never start a background
process implicitly. `server run --jsonl` instead stays in the foreground for a
supervisor. The server is local and owner-only: Windows named pipe or Linux
Unix socket, no TCP port. It keeps one model resident and runs queued work
serially. An incompatible server reports an error; restart it using the same
CLI build. The desktop and CLI cannot operate on the same model folder while
another process holds its model lease. Unload or close that process first.

## Prepare models and observe progress

```text
bunyi models download --all --one-shot --jsonl
bunyi models status --json
bunyi models verify --mode preset --jsonl
```

`--all` prepares the configured preset, design, and clone ONNX models plus the
Whisper model used for transcription. `--mode preset|design|clone` prepares one
TTS model. Downloads can resume and validate their files. The JSON-lines stream
reports phases, asset identity, bytes completed/total, transfer rate, and ETA.
Unknown totals and ETAs are `null`; agents must tolerate them. Use the terminal
result and exit status, never a percentage, as proof of success.

For agent hosts whose tool calls have short time limits:

```text
bunyi server start --json
bunyi models download --all --detach --json
bunyi jobs status <operationId> --json
bunyi jobs follow <operationId> --jsonl
bunyi jobs cancel <operationId> --json
```

Detached work survives the client exiting, not server termination. Job history
is bounded and transient. Attached work is cancelled when its client
disconnects; Ctrl+C also requests cancellation. Files already completed remain
on disk. `server stop` cancels work cooperatively and waits for safe unloading.

## Other commands

```text
bunyi speakers --json
bunyi transcribe /absolute/reference.wav --json
bunyi voices add --name Narrator --reference /absolute/reference.wav --transcript "Reference words" --json
bunyi voices list --json
bunyi generate clone --saved-voice <voice-id> --text "A new sentence" --json
bunyi voices remove <voice-id> --json
bunyi history list --json
bunyi history show /absolute/output.wav --json
bunyi history remove /absolute/output.wav --json
bunyi doctor --deep --jsonl
bunyi backup create /absolute/new-backup.zip --jsonl
bunyi backup restore /absolute/backup.zip --jsonl
bunyi config list --json
bunyi config set modelsFolder /absolute/existing/models-folder --json
bunyi config get modelSource.preset --json
bunyi logs path --json
bunyi logs tail --lines 100 --json
bunyi logs clear --json
```

`models remove --mode <mode>` and history removal move their exact targets to
Trash. Voice removal deletes the selected library entry and clip. Restoring a
backup requires unloading a resident model first. See `bunyi --help` for the
complete syntax. GUI-only interactions, such as playback controls and file
pickers, are not CLI commands.

## Agent integration and isolation

OpenClaw, Hermes Agent, and other local tool runners should call the executable
directly with an argument array, not interpolate prompts into shell commands.
Use `--json` for one bounded response or `--jsonl` for progress and a terminal
response. Human diagnostics go to stderr. Handle exit codes 2 (arguments),
3 (input/preflight), 4 (busy/server), 5 (cancelled), and 10 (operation failure).
Success is exit 0 with a successful terminal object.

Default storage is shared with the desktop. Set `BUNYI_DATA_DIR` to an absolute
directory to isolate automation data and settings. `--config <absolute-file>`
or `BUNYI_CONFIG_FILE` overrides only settings. Pass identical overrides to
server startup and every client command. Separate data profiles can share an
explicit models folder, but still obey that folder's exclusive model lease.
Never use an isolated profile to bypass the lease or edit models externally
while they are loaded.
