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
2. at least one weights file exists (`*.safetensors` for MLX, `*.onnx` for
   the ONNX app), **and**
3. no partial/incomplete download markers exist anywhere in the tree.

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

## `tokenizer.json` auto-fetch

Some Qwen3-TTS conversions omit `tokenizer.json` (mlx-community ships
`vocab.json` + `merges.txt` only), but tokenizer loaders require it. All
Qwen3-TTS variants share one 151,643-token text tokenizer. When missing:
1. try `<self-host base>/tokenizer.json` (if self-hosting), then
2. a known-good fallback URL.

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

- 24 kHz, mono, PCM WAV. Saved to a user-visible `Qwen3 TTS` folder under
  the platform's Music (or Documents) location.
- Filename: `<Mode>-<ISO8601-basic timestamp>.wav`
  (e.g. `Voice-clone-20260725T2312.wav`).
