# Data Formats & On-Disk Layout

These formats are **shared across platforms** so a models folder, a backup
zip, or a saved-voices library produced by one app is usable by another
app of the *same runtime family*. Behavior (folder shape, manifest,
voices.json, WAV) is identical everywhere; only the model **weight files**
differ (MLX `.safetensors` on macOS vs ONNX on the .NET app).

## Per-user app data

Everything the app keeps for a user lives under one root, with fixed subfolder
names so a folder is recognisable across platforms:

| | macOS | Windows | Linux |
|---|---|---|---|
| Data root | `~/Library/Application Support/Bunyi` (inside the sandbox container) | `%LOCALAPPDATA%\Bunyi` | `$XDG_DATA_HOME/Bunyi`, defaulting to `~/.local/share/Bunyi` |
| Settings | (`UserDefaults`) | `%APPDATA%\Bunyi\settings.json` | `$XDG_CONFIG_HOME/Bunyi/settings.json`, defaulting to `~/.config/Bunyi/settings.json` |

Subfolders of the data root, identical everywhere: `Models` (the default
models folder, §3d — relocatable), `Outputs` (§2), `Voices` (§5),
`ModelConfigs` (§3a).

**Settings and data are separated on the platforms that separate them.** On
Windows `%APPDATA%` roams and `%LOCALAPPDATA%` does not, so a multi-gigabyte
models folder under `%APPDATA%` would be handed to a domain roaming profile to
synchronise at every logon. Settings are a few hundred bytes and are worth
carrying between machines; models are not, and cannot be. The XDG split is the
same distinction under different names.

**macOS keeps settings in `UserDefaults`** rather than a file, which is the
platform's own answer to the same question and stays as it is; the keys
(`appearance`, `modelRepo.<Mode>`, `modelsFolder`, `unloadOnModeSwitch`) are
the contract, not the storage.

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

The ONNX family's file set is not the MLX one with the extensions swapped. An
ONNX export is **several named graphs plus a folder of embedding arrays**, and
any one of them missing is a model that loads and then fails:

```
    <org>/<repo>/
      talker_prefill.onnx    talker_prefill.onnx.data
      talker_decode.onnx     talker_decode.onnx.data
      code_predictor.onnx    [code_predictor.onnx.data]
      vocoder.onnx           vocoder.onnx.data
      embeddings/…           ← *.npy arrays, and on some exports config.json
      tokenizer/…            ← tokenizer.json, or vocab.json + merges.txt
      [speaker_encoder.onnx  tokenizer_encoder.onnx  (+ .data)]   ← clone only
      [int4/ | fp32/]        ← precision variants, when the export ships them
```

Two consequences the MLX layout never had:

- **A precision subfolder is part of the path.** Some exports ship the same
  graphs twice, at different quantizations. Only one variant is downloaded:
  fetching a whole such repo means gigabytes that will never be loaded — one
  published VoiceDesign export is 18.55 GB in total and 4.27 GB in its `int4`
  subtree.
- **`config.json` is not reliably at the top level.** Some exports keep it at
  `embeddings/config.json`. It is still required; where it lives is a property
  of the export.

Because of both, the required-file set is declared **per export** rather than
derived from a global pattern — see the completeness rule below and §3c's
manifest.

- `<slug>` = the base URL's host+path sanitized to filesystem-safe chars: take
  `host` followed by `path`, replace every character outside
  `[A-Za-z0-9._-]` with `-`, trim leading and trailing `-`, and use `server`
  if nothing is left. Pinned rather than described because two apps must
  produce the *same* folder name for the same URL, or a models folder stops
  being interchangeable.
- During Hub downloads, partial files live under `.cache/huggingface`.

### `hasCompleteModel` rule

The question is the same everywhere — *may this folder be loaded without going
to the network?* — but the test is per runtime family, because the two families
do not agree on what a model's files look like.

#### MLX family

A model folder is "complete" and used offline when:
1. `config.json` exists, **and**
2. a weights file exists **at the top level** (`*.safetensors`), **and**
3. every shard named by a weights index (`model.safetensors.index.json`'s
   `weight_map`) is present, if such an index exists, **and**
4. no partial/incomplete download markers exist anywhere in the tree.

Rule 2 says *top level* because these models ship a second weights file for
the speech tokenizer in a subfolder. "Any weights file anywhere" counted that
one, so a download interrupted before the model's own weights arrived left a
folder that looked complete: the app skipped the download and failed at load,
pointing at nothing.

Rule 3 exists because one shard of three satisfies every other rule.

#### ONNX family

Every clause above except the last one fails on real ONNX exports, so the rule
is restated rather than adapted. A model folder is complete when, **against the
required-file list for that export** — the app's built-in list for a Hub repo,
or the list the server published for a self-hosted one (§3c):

1. every entry marked required exists at its declared relative path, at
   non-zero length, **and**
2. every `<name>.onnx` that declares external data has its `<name>.onnx.data`
   sibling, also at non-zero length, **and**
3. no partial/incomplete download markers exist anywhere in the tree.

Why each MLX clause had to go:

- **"`config.json` exists"** — true of most exports, but some ship it as
  `embeddings/config.json`. A folder with everything it needs would be judged
  incomplete forever, and re-downloaded on every launch.
- **"a weights file at the top level"** — wrong in kind and in place. An export
  is four or more named graphs, not "a weights file", and an export with a
  precision subfolder has nothing weight-shaped at the top level at all. The
  per-export list names whichever graphs that export actually ships, which is
  what the clause was really trying to say.
- **"every shard named by a weights index"** — ONNX has no weights index.
  External data is not an enumerated set of shards; it is a single
  fixed-name sibling declared inside the graph itself. Clause 2 is the
  equivalent guarantee: it is exactly the "one shard of three" failure, in the
  form this family can have it, and it is the common one — the `.onnx` file is
  a few megabytes and its `.onnx.data` is gigabytes, so an interrupted download
  very often leaves the small half.

**Digests are deliberately not part of this test.** Where a server publishes
them they decide whether a *downloaded* file is accepted and whether one
already on disk may be reused (see `manifest.sha256`), and Doctor verifies them
on demand — but hashing gigabytes on every launch is a worse problem than the
one it detects, which is why FEATURES.md §11 keeps that check on demand.

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
- contains a `\` or a `:`;
- has any component that is empty, `.`, or `..` (which also rejects `a//b`
  and a trailing `/`, neither of which names a file).

`\` and `:` are rejected on **every** platform, not only Windows. Both are
legal in a POSIX filename and neither escapes anything on macOS or Linux —
but on Windows `\` is a path separator, `C:/Windows/System32` is
drive-rooted, and `C:foo` is drive-*relative*, landing wherever that drive's
working directory happens to be. An entry that looks inert on one
implementation and traverses on another is the failure this rule exists to
prevent, so both apps apply the same test rather than each guarding its own
platform. Windows forbids `:` in filenames anyway, so an entry containing
one is either an escape attempt or a file the .NET app could not create.

These paths are used to build write destinations, and `<base>` is whatever
the user typed into Settings — not necessarily a server they audited. The
rules apply to every manifest format below.

## Self-host `manifest.sha256`

Optional file at `<base>/manifest.sha256`, **preferred over `manifest.txt`
when both are served**. Same rules for blank lines and `#` comments. Each
line is the output format of `shasum -a 256` / `sha256sum`:

```
<64 lowercase hex digits><whitespace><relative path>
```

```
e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  config.json
5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03  model.safetensors
9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08  speech_tokenizer/config.json
```

The separator is **any whitespace**, not the two spaces `shasum` happens to
write — tools do emit tabs, and a client that splits on a literal space
reads such a line as a bare path and skips verification silently.

A line whose first token is not exactly 64 hex digits is read as a bare
path, so one parser handles both files and a digest-less line is legal.
A leading `*` on the path (how `shasum` marks a binary-mode read) is
stripped. Digests are compared case-insensitively.

The **path rules** above apply here unchanged — a digest does not make a
path safe, and an entry is rejected before its digest is ever considered.

### Required of a mirror an app endorses

Optional for a mirror someone sets up for themselves; **mandatory** for one an
app offers as a built-in configuration. `FEATURES.md` §3a gates the built-in on
it: a source the app itself suggests is a higher bar than one a user picked,
and unverified bytes do not clear it.

The manifest is also **the file list**, not a checksum appended to one. A file
the manifest omits is a file the client never asks for, so an incomplete
manifest produces an install that looks finished and fails at load — which is
the failure the completeness rule exists to prevent. Deriving it from the
client's own view of what each mode needs, rather than from a directory
listing, is what `apps/dotnet/tools/MirrorManifest` does and why.

One manifest per model, at that model's own prefix. Nothing is shared between
modes, and nothing about a manifest identifies which runtime family it belongs
to — the prefix does that.

**Two files rather than digests inside `manifest.txt`.** Every released
client parses each line of `manifest.txt` as a path; a `<digest>  <path>`
line would be requested verbatim, 404, and fail the download outright for a
required file. Old clients never request `manifest.sha256`, so a server may
publish it at any time without breaking anything already installed.

Where a digest is present the client **must**:

- verify the downloaded file against it before treating the file as
  complete, and discard the file if it does not match — a failed file must
  not be left where a retry would find it and skip it;
- use the digest, not the file size, to decide whether a file already on
  disk can be reused. Size equality is precisely the test a truncated or
  corrupt file passes.

Digests cover corruption, truncation and partial uploads. They are **not**
an authenticity guarantee: a server that can rewrite a model file can
rewrite the manifest beside it. Anchoring that would mean shipping expected
digests in the client or signing the manifest, neither of which this format
does.

## `tokenizer.json` auto-fetch

Some Qwen3-TTS conversions omit `tokenizer.json` (mlx-community ships
`vocab.json` + `merges.txt` only), but some tokenizer loaders require it. All
Qwen3-TTS variants share one 151,643-token text tokenizer, so any compatible
copy will do. When the model folder carries **neither** a `tokenizer.json`
**nor** a `vocab.json` + `merges.txt` pair that the app's tokenizer can be
built from:
1. try `<self-host base>/tokenizer.json` (if self-hosting), then
2. a known-good fallback URL.

The condition is "the app cannot build a tokenizer from what is here", not
"`tokenizer.json` is absent" — the two came apart once there was a second
runtime family. MLX's loader needs the file itself, so for that family the
condition is unchanged in practice. ONNX exports ship a `tokenizer/`
subfolder carrying `tokenizer.json`, or `vocab.json` + `merges.txt` from which
the BPE is built directly, so the fetch does not normally trigger at all — and
a client that insisted on a *top-level* `tokenizer.json` would re-download one
on every launch, next to the perfectly good tokenizer it was ignoring.

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
  `mode`, `text`, `language`, `modelRepo`, `appVersion`, `created`,
  optional `platform` (`Windows`, `macOS` or `Linux` — what produced the
  file, **stored** rather than worked out when it is read, because a clip is
  routinely opened on a machine other than the one that made it; absent in
  files written before it existed, which are not broken), plus
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
