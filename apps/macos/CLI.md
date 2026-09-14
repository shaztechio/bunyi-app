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

# Bunyi CLI for macOS

The native `bunyi` command runs Qwen3-TTS locally with MLX on Apple Silicon.
It requires macOS 15 or later. The archive is self-contained; keep
`Frameworks` and `mlx-swift_Cmlx.bundle` beside the executable because they
contain Bunyi's shared runtime and MLX's Metal library.

Run `./bunyi --help` for the complete command surface. Typical setup:

```sh
./bunyi models download --all --jsonl
./bunyi server start --json
./bunyi generate preset --text "Hello from Bunyi" --json
./bunyi server stop --json
```

The CLI stores its data in `~/Library/Application Support/Bunyi` by default.
It does not have access to the desktop app's sandbox container, although both
can be configured to use the same external models folder. A cross-process
lease prevents either process from changing model files while the other is
using them.

Transcription never sends audio to a network service and needs no macOS
privacy grant. The CLI downloads the multilingual Whisper base model on first
use and saves it with the other model assets for reuse. `models download
--all` includes it, so later transcription and clone jobs are fully offline.

Shell completions are in `completions/bash`, `completions/zsh`, and
`completions/fish`. Copy or source the appropriate file according to your
shell's completion setup.

The command contract is documented in `spec/CLI.md` in the Bunyi source
repository. `LICENSE` contains the Apache-2.0 license and `CREDITS.json` lists
the software and models used by Bunyi.
