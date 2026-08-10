# Data Formats & On-Disk Layout

These formats are **shared across platforms** so a models folder, a backup
zip, or a saved-voices library produced by one app is usable by another
app of the *same runtime family*. Behavior (folder shape, manifest,
voices.json, WAV) is identical everywhere; only the model **weight files**
differ (MLX `.safetensors` on macOS vs ONNX on the .NET app).

## Models folder

Root is the models folder (default per-user app data; user-relocatable).

```
<models-folder>/
  models/
    <org>/<repo>/                    ← Hub downloads, e.g. mlx-community/Qwen3-TTS-...
      config.json
      *.safetensors                  ← (ONNX app: *.onnx / *.onnx.data instead)
      tokenizer.json  vocab.json  merges.txt  tokenizer_config.json
      generation_config.json  preprocessor_config.json
      speech_tokenizer/…
    self-hosted/<slug>/              ← self-hosted base-URL downloads
      (same file set)
  .cache/huggingface/…               ← Hub partials during download (macOS)
```

- `<slug>` = the base URL's host+path sanitized to filesystem-safe chars.
- During Hub downloads, partial files live under `.cache/huggingface`.

### `hasCompleteModel` rule (identical everywhere)
A model folder is "complete" and used offline when:
1. `config.json` exists, **and**
2. a weights file exists **at the top level** (`*.safetensors` for MLX,
   `*.onnx` for the ONNX app), **and**
3. every shard named by a weights index (`model.safetensors.index.json`'s
   `weight_map`) is present, if such an index exists, **and**
4. no partial/incomplete download markers exist anywhere in the tree.

Rule 2 says *top level* because these models ship a second weights file for
the speech tokenizer in a subfolder. "Any weights file anywhere" counted that
one, so a download interrupted before the model's own weights arrived left a
folder that looked complete: the app skipped the download and failed at load,
pointing at nothing.

Rule 3 exists because one shard of three satisfies every other rule.

## Self-host `manifest.txt`

Optional file at `<base>/manifest.txt`. Newline-separated **relative**
paths; blank lines and `#` comments ignored. Example:

```
config.json
model.safetensors
tokenizer.json
speech_tokenizer/config.json
speech_tokenizer/model.safetensors
```

If absent, the app uses a built-in default file list appropriate to its
runtime. **Required** (404 = hard error): `config.json` and the primary
weights file. Everything else is best-effort (single-shard repos lack a
`.index.json`; a missing `tokenizer.json` is auto-fetched).

### Path rules

Paths are relative to `<base>`, and a client **must reject** an entry that
could escape the model's folder. The entry is skipped and the rejection
logged; the download continues. An entry is unsafe if it:

- is empty, or begins with `/` or `~`;
- contains a `\` — legal in a POSIX filename, a separator on Windows, so an
  entry that traverses on one platform must not look inert on the other;
- has any component that is empty, `.`, or `..` (which also rejects `a//b`
  and a trailing `/`, neither of which names a file).

These paths are used to build write destinations, and `<base>` is whatever
the user typed into Settings — not necessarily a server they audited.

## `tokenizer.json` auto-fetch

Some Qwen3-TTS conversions omit `tokenizer.json` (mlx-community ships
`vocab.json` + `merges.txt` only), but tokenizer loaders require it. All
Qwen3-TTS variants share one 151,643-token text tokenizer. When missing:
1. try `<self-host base>/tokenizer.json` (if self-hosting), then
2. a known-good fallback URL.

## `configs.json` (saved model configurations)

Stored in a `ModelConfigs` subfolder of app data. Each entry is one named set
of the three per-mode sources; an empty string means that mode uses its
built-in default, which is the same meaning a blank field has in Settings.

```json
[
  {
    "id": "<UUID>",
    "name": "Self-hosted",
    "presetVoice": "https://models.example.com/customvoice",
    "voiceDesign": "https://models.example.com/voicedesign",
    "voiceClone":  "https://models.example.com/voiceclone",
    "savedAt": "2026-08-10T05:12:44Z"
  }
]
```

Names are unique case-insensitively — saving over one replaces it. Dates are
ISO 8601. This lives with the app's own data rather than in the models folder:
it describes *where models come from*, so it must survive relocating or
deleting that folder.

## `voices.json` (saved voices library)

Stored in a `Voices` subfolder of app data, alongside copied audio clips.

```json
[
  {
    "id": "UUID",
    "name": "Eric",
    "fileName": "<UUID>.wav",     // copied clip, sibling of voices.json
    "transcript": "He shoots, he scores…",
    "createdAt": "ISO-8601"
  }
]
```

- The clip is **copied in** (24 kHz mono preferred) so it survives without
  a security-scoped bookmark to the user's original file.
- Entries whose `fileName` is missing on disk are pruned on load.

## Backup archive

- A single `.zip` of the **models folder**, created **stored (no
  compression)** — weights are incompressible, so storing is ~4× faster and
  the archive size tracks the source (drives a determinate progress bar).
- Restore accepts an archive whose tree contains a `models/` directory at
  or near the root; merges per `<org>/<repo>` (or `self-hosted/<slug>`),
  **skipping repos already present**.

## Output WAV

- **Embedded metadata**: a RIFF `LIST`/`INFO` chunk appended to the file,
  carrying what produced it. Standard fields so ordinary tools show something
  useful — `INAM` (the text, truncated), `IART` (speaker or voice
  description), `ISFT` (`Bunyi <version>`), `ICRD` (ISO 8601), `IGNR`
  (`Speech`) — plus the whole record as JSON in `ICMT`:
  `mode`, `text`, `language`, `modelRepo`, `appVersion`, `created`, plus
  exactly one voice field for the mode that produced it: `speaker` and
  optional `style` (preset voice), `voiceDescription` (voice design), or
  `referenceTranscript` (voice clone). They are separate keys on purpose —
  the macOS UI reuses one text field for the preset-voice *style* and the
  voice-design *description*, so a single key would leave a reader unable to
  tell a delivery instruction from a voice. Empty values are omitted rather
  than stored blank.
  There is no standard four-character code for "the prompt", and inventing
  private ones would be readable by nothing, so one comment field carries it.
  The chunk is **appended**, leaving the audio bytes untouched, and tagging is
  best-effort: a file that plays without its metadata beats losing the audio
  to a failed tag write. Timestamps are ISO 8601 with milliseconds.
- 24 kHz, mono, PCM WAV. Saved to an `Outputs` folder under the app's
  per-user data directory (macOS: Application Support inside the sandbox
  container).
- Filename: `<Mode>-<ISO8601-basic timestamp>.wav`
  (e.g. `Voice-clone-20260725T2312.wav`).
