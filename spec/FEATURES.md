# Bunyi — Feature Specification

**This document is the source of truth for feature parity across platforms.**
Every app (macOS Swift/MLX, and the .NET/Avalonia app for Windows + Linux)
must implement the behavior described here. When a feature changes, update
this spec *and* every app. Platform-specific mechanics (which ML runtime,
which audio library) are noted but never change the observable behavior.

The right-hand "macOS source" references point at the reference
implementation in `apps/macos/` so a second implementation has something
concrete to match.

Audience: non-technical end users. No terminal required; models
auto-download with a progress bar; outputs are playable WAVs.

---

## 1. Three generation modes

A segmented picker selects one of three modes. macOS source:
`ContentView.swift`, `TTSEngine.generate(...)`.

| Mode | Model type | Inputs | Emotion/style |
|------|-----------|--------|---------------|
| **Preset voice** | CustomVoice | text, speaker (from model's list), optional style instruction | yes (`instruct`) |
| **Voice design** | VoiceDesign | text, voice description | yes (`instruct`) |
| **Voice clone** | Base | text, reference audio clip, reference transcript | **no** — Base model ignores `instruct` |

- Speaker list for preset voice comes from the loaded model
  (`supportedSpeakers`); a fallback list is shown until a model loads.
- Language selector: auto + english, chinese, japanese, korean, german,
  french, russian, portuguese, spanish, italian.
- **Generate is unavailable until the mode has what it needs**, and says why
  on hover: text in every mode, plus a voice description for voice design and
  a reference clip for voice clone. Not a validation message after the fact —
  the engine rejects a clone with no clip only *after* preparing the model,
  which on a first run means waiting out a multi-gigabyte download to be told
  a file is missing. Voice design had no check at all and would generate an
  arbitrary voice from an empty description.
- **Emotion for clones is not supported** by the 12 Hz Base model. Do not
  add an emotion field to clone mode. Emotion for a cloned voice must come
  from the reference clip's own delivery. (Tracked upstream: 25 Hz models.)

## 2. Generation output

- Sample rate **24 kHz**, mono, WAV.
- Saved to the app's per-user data folder: macOS
  `~/Library/Application Support/Bunyi/Outputs` (inside the
  sandbox container — no extra file-access entitlement needed). Other
  platforms: an equivalent per-user app-data subfolder named `Outputs`.
  One click away via the in-app reveal-in-file-manager button.
  Filename: `<Mode>-<ISO8601 timestamp>.wav`.
- **The file carries what produced it** — text, mode, language, speaker,
  style, reference transcript, model repo, app version, timestamp — embedded
  in the audio file itself (macOS: a RIFF `LIST`/`INFO` chunk; see
  `DATA-FORMATS.md`). The filename records only mode and time, so without
  this a WAV that leaves the app loses every setting that produced it.
  History reads it back to label each row, and ordinary audio tools show the
  standard fields.
- After generation the app auto-plays the result and offers Play + reveal
  in file manager. **Only this run's result.** Starting a run clears the
  previous one: the playback controls disappear for the duration, so nothing
  offers to play the old audio while new audio is being made, and a cancelled
  run leaves nothing to play rather than falling back to the file from
  before. The old file is untouched on disk — it is still in `Outputs`.
- **While work is in progress the inputs are disabled** — text, language,
  speaker, style, reference clip, saved voice, and the mode picker. Their
  values were already handed to the engine when the run started, so leaving
  them editable invited changes that silently did not apply to the audio
  being produced. Help stays reachable: a long download is exactly when
  someone reads it.
- Live progress: a token counter during generation. macOS uses
  `generateStream` (preset/design) and an `onToken` callback bridged over a
  stream (clone). Any backend must surface incremental progress.
- **The UI thread never does inference work, and never writes the output.**
  Includes the step that forces a lazily-evaluated tensor to be materialized:
  on macOS the generator yields an unevaluated MLX graph and
  `saveAudioArray` is what evaluates it, so writing the WAV on the main actor
  froze the app at the end of every generation. Any runtime with deferred
  evaluation has the same trap in a different place — the rule is that the
  window stays responsive for the whole run, not that one named call is moved.
- **Stop**: while any work is in progress — model download, transcription,
  model load, or generation — the Generate button is **replaced** by a Stop
  button (Escape). Not merely disabled: a model download runs for minutes,
  and without Stop the only way out is closing the window and confirming
  (§9).
- **Cancellation is cooperative, and the app stays busy until the engine has
  actually stopped.** Cancelling stops the consumer; the inference engine may
  run to completion regardless (macOS: the package generates on its own
  thread, and `generateVoiceClone` takes no cancellation at all), and its
  result is discarded. Until that work ends, the app shows a distinct
  *stopping* state and refuses to start another generation. Reporting ready
  early is not allowed: it invites a second job against the same model, and
  switching mode would then free that model out from under work still using
  it. So Stop does not promise the machine stops computing — only that the
  app stops waiting, and says so honestly while it does.

## 2a. History

macOS source: `HistoryView.swift`, `TTSEngine.generatedOutputs()`.

A fourth segment beside the three generation modes lists **everything
generated so far, newest first**: mode, date, size, with play/pause per row,
a **Download** button, and reveal-in-file-manager.

- **The folder is the record**, not an in-app database. The list is read from
  the `Outputs` folder each time it is shown, so a file deleted outside the
  app disappears from History, and the list survives relaunches with no state
  to migrate. Deliberately named History, not Library — "library" already
  means the saved *voices* library (§5).
- **Download** opens a save panel so the user chooses the destination. On a
  sandboxed platform that choice is also what grants permission to write
  there, so a fixed destination would need an entitlement this does not
  otherwise require.
- History remains available while a generation is running — it only reads the
  folder. The generation modes do not: switching one evicts the model the
  running job is using (§2).
- Playback is **play/stop, with progress drawn as a ring around the button
  itself** rather than a separate bar — the control and its progress are the
  same object, which is what the row has space for. No pause: these are short
  clips, and a paused row is a third state to explain for something a user
  would nearly always just play again. A clip that reaches its end returns the
  row to Play on its own.
- Each row shows one line: the text it spoke, the mode, the voice, the date
  and the size. A prompt can be paragraphs long, so **the whole record is on
  hover** — text, mode, language, voice, style or reference transcript, and
  the model. A file with no embedded metadata says so on hover rather than
  showing a bare filename that reads like a fault.
- **Copy details** puts that same record on the clipboard as readable text.
  Hover is for looking; a tooltip cannot be pasted into a note, a bug report,
  or back into the app to reproduce a result. The button acknowledges the
  copy, because one that appears to do nothing gets pressed again.
- **Trash** moves a file to the system Trash after confirming, not an
  unrecoverable delete: the row label is truncated, so the wrong icon is easy
  to hit, and the audio may be the only copy.
- **No Generate button in History**: there is no text on screen to speak, so
  the button would either do nothing or silently act on a mode that is not
  visible. **Stop stays**, because a run can still be in progress while
  History is open and hiding it would strand the user. The single-file
  playback controls are also hidden here — History has its own per-row
  player, and two players on screen can play over each other.

## 3. Model management

macOS source: `TTSEngine.download*`, `ModelSettings.swift`.

### 3a. Per-mode model source
Each mode has a configurable source (Settings → Models). A value is either:
- a **Hugging Face repo ID** (e.g. `mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16`), downloaded via the Hub, **or**
- an **`http(s)://` base URL** the user self-hosts (files fetched directly).

Scheme decides: `http://`/`https://` ⇒ base URL, else repo ID.
Blank ⇒ the built-in default for that mode.

**Configurations.** The three sources are saved and restored as a set, under a
name — plus a reset that clears all three back to the defaults. They belong
together: switching between the Hub and a self-hosted mirror means changing
all three, the values are long and easy to mistype, and each must match its
mode (CustomVoice, VoiceDesign, Base) or the app loads a model that runs and
produces nonsense. Saving under an existing name replaces it rather than
accumulating near-duplicates. Stored per-user alongside the saved voices, not
in the models folder — see `DATA-FORMATS.md`.

**One built-in configuration: the project's own mirror.** It appears in the
list above any saved ones, has no Delete button — nothing about it is on disk —
and is overridden by saving a configuration of the same name. It exists because
Hugging Face is unreachable on some networks, blocked outright in mainland
China, which for a Qwen model is a substantial share of the likely audience.

It is **not** the default and must not become one. Upstream is where the
weights come from, and a default pointing at project-run infrastructure makes
that infrastructure a single point of failure for every install. Nor is it an
automatic fallback when the Hub is slow: the source is recorded in each
output's metadata, so it must be one the user chose.

A platform ships this only if its mirror publishes `manifest.sha256`
(`DATA-FORMATS.md`). Offering a source the app itself endorses is a higher bar
than documenting one a user picked, and unverified bytes do not clear it.

> Model **weights** differ per runtime: macOS uses MLX `.safetensors`
> conversions (mlx-community); the .NET app uses **ONNX** exports of the
> same Qwen3-TTS models. The *defaults differ per platform* but the
> source-selection UX, folder layout, and self-host behavior are identical.
> See `DATA-FORMATS.md`.

### 3b. Download behavior (identical across platforms)
- **Resumable & incremental**: already-present files are skipped — on every
  source, not only the Hub. Stopping a download and starting again must not
  re-fetch what is already on disk; for a self-hosted model that is gigabytes
  of pointless transfer. A file counts as present when its size matches the
  server's, not merely when it exists: an interrupted write leaves a file that
  exists and is wrong.
- **Manifest paths are untrusted input.** A self-hosted manifest names its own
  files, and those names become write destinations. Entries that could escape
  the model's folder are skipped and logged, per the path rules in
  `DATA-FORMATS.md` — the download continues rather than failing, since one bad
  line should not cost a multi-gigabyte refetch.
- **Checksums when the server publishes them.** A self-hosted server may serve
  `manifest.sha256` (`DATA-FORMATS.md`). Where a digest exists it replaces the
  size test above, both for verifying a fresh download before it counts as
  complete and for deciding whether a file already on disk can be reused —
  matching sizes are exactly what a truncated file has. A file that fails must
  be discarded rather than left for a retry to find and skip. Servers without
  the file keep working unchanged, and a client must not require it.
- **Offline reuse**: a complete model on disk is used without any network
  (`hasCompleteModel` rule in `DATA-FORMATS.md`).
- **Progress and stall detection must follow bytes received from the network**,
  not completed files and not the size of the destination folder. A model is
  one enormous file and a dozen small ones, so per-file progress sits still for
  minutes on the big one; and a transfer that buffers elsewhere before moving
  the finished file into place makes a folder-watching stall detector report a
  healthy download as dead. Both were real on the self-hosted path. Where the
  bytes are buffered is an implementation detail — what is required is that
  progress within a file counts toward the whole, and that "no new data"
  means no bytes arrived, not no growth on disk.
- **Progress + ETA**: a fraction-based bar plus a human line
  ("42% — about 3.1 MB/s, ~6 min left"). Because per-file fraction can look
  frozen during a multi-GB file, a **disk monitor** logs bytes-on-disk
  every 10 s and warns after 30 s of no new data ("connection may be
  stalled").
- **tokenizer.json auto-fetch**: if a downloaded model lacks the tokenizer
  the runtime requires, fetch a compatible one (from the self-host base
  first, then a known fallback URL). See `DATA-FORMATS.md`.

### 3c. Self-hosting
- The app fetches `<base>/manifest.txt` (newline-separated relative paths)
  and downloads each `<base>/<path>`. If absent, it uses a built-in
  default file list for that runtime.
- Required files fail the download on 404; all others are best-effort.
- Files land in `models/self-hosted/<slug>` and are reused offline.
- **https recommended**; plain http requires a platform-specific security
  opt-in (macOS: ATS exception).

### 3d. Custom models folder
- Default: per-user app-data dir (macOS: App Support container). User can
  point it at any folder (external drive, etc.), persisted across launches
  (macOS: security-scoped bookmark). "Show in Finder/Explorer" + reset.
- **Downloaded models are listed and deletable** in Settings → Storage: each
  model, its source (Hub or self-hosted), its size, and a Delete button that
  moves the folder to the Trash after confirming. Reclaiming several gigabytes
  must not require knowing where the app keeps its container — on a sandboxed
  platform that path is not somewhere a user can reasonably be sent.
  Deleting the model that is currently loaded **evicts it from memory first**;
  otherwise the app keeps generating from a model whose files are gone and
  silently re-downloads on next launch.
- Settings → Storage also shows copyable pre-download commands
  (`hf download <repo> --local-dir <folder>/models/<repo>`), one per mode,
  with the actual folder path filled in.

## 4. Reference audio (voice clone)

macOS source: `TTSEngine.loadReferenceAudio`, `ReferenceTranscriber.swift`.

- Reference audio **must be resampled to 24 kHz mono** before use — the
  model asserts that rate; feeding 44.1/48 kHz produces distorted,
  wrong-pitch clones. Downmix stereo → mono.
- Voice clone is **ICL**: it requires the reference **transcript** to align
  audio to words. An empty transcript yields gibberish that ignores the
  target text — so the transcript is effectively mandatory.
- **Auto-transcription**: if the transcript field is blank, transcribe the
  clip on-device and use the result (also shown to the user, editable).
  - macOS: Speech framework (`SFSpeechRecognizer`), fed PCM buffers (not a
    file URL — the recognition daemon can't read a sandboxed file).
  - .NET (Win+Linux): Whisper (whisper.cpp or Whisper-ONNX), bundled so it
    works offline and identically on both OSes.
  - A typed transcript always overrides auto-detection.

## 5. Saved voices library

macOS source: `VoiceLibrary.swift`.

- Save a clone recipe: **name + reference clip + transcript**. The clip is
  **copied into app storage** (not referenced by path) so it survives
  relaunch. Appears in a picker in clone mode; selecting it fills reference
  + transcript. Delete removes the entry and its copied clip.
- Persisted as `voices.json` + copied audio (schema in `DATA-FORMATS.md`).
- On load, entries whose audio is missing are pruned.
- Not a real model "preset" — presets are trained speaker tokens; this just
  re-runs the clone path with saved inputs.

## 6. Backup & restore

macOS source: `BackupManager.swift`.

- **Backup**: archive the whole models folder to one **.zip**, *stored (no
  compression)* — model weights don't compress, so storing is far faster
  and lets a determinate progress bar track the growing archive.
- **Restore**: unpack a backup and merge per-repo into the models folder,
  **skipping repos already present** (never clobber). Validate the archive
  actually contains a `models/` tree first.
- **Progress + Stop**: both show progress and a Stop button that truly
  cancels (terminating any child archiver process).
- **Volume-aware save**: writing the finished zip to the destination is an
  instant move on the same volume, a streamed copy with its own progress on
  a different volume (network/external drive). Never block the UI thread.

## 7. Settings

Tabbed window (macOS: ⌘,). Tabs — the window title reflects the selected
tab (platform convention). macOS source: `SettingsView.swift`.
- **Models**: the three per-mode source fields (repo ID or base URL) + help.
- **Storage**: models-folder location controls + pre-download commands.
- **Backup**: back up / restore / stop + status.

## 8. Logs

macOS source: `LogStore.swift`, `LogsView.swift`.
- A separate Logs window (macOS: Window → Logs, ⌘L) with timestamped,
  selectable, monospaced lines, autoscroll, Copy + Clear.
- Mirror to the platform's system log where available (macOS: OSLog).
- Everything notable is logged: model prepare/download progress, per-file
  self-host downloads, tokenizer step, transcription result, generation
  token milestones, saved output path + timing, backup/restore steps, and
  full error text.

## 9. Busy-close confirmation

macOS source: `WindowCloseGuard.swift`.
- If the main window is closed (red button / OS close) **while an operation
  is in progress** (download, transcription, generation, model load),
  confirm first: "Stop the current operation?" with *Keep Working* (safe
  default) and *Stop and Close* (destructive).
- Confirming **stops the work first, and closes once it has actually
  stopped** — not both at once. Cancellation is cooperative (§2), so a window
  that closes on confirmation disappears while the engine is still generating
  and still holding the model; the app then has no window and visible work.
  The window stays up, showing its *stopping* state, until the engine reports
  idle. A timeout closes anyway rather than trapping the user in a window
  that will not shut, and the confirmation says so — a prompt promising to
  close "once it has stopped" would be a lie in exactly the case the timeout
  exists for. Pressing close again during the wait does nothing: it must not
  ask twice or start a second stop. Not busy ⇒ close immediately.

## 10. Error handling & copy tone

- Plain verbs, sentence case, no jargon ("Generate", not "Synthesize").
- User-facing errors are actionable (e.g. "Your server is missing a
  required model file: config.json (HTTP 404). Check the URL…").
- Full technical error text goes to the Logs, not the status line.

---

## Feature → macOS source map (parity checklist)

- Modes / generation / playback → `ContentView.swift`, `TTSEngine.generate`
- Model download, resume, offline, progress/ETA, disk monitor → `TTSEngine.download*`, `noteDownloadProgress`, `startDiskMonitor`
- Self-host base URL + manifest → `TTSEngine.downloadFromBaseURL`, `fileList`
- Per-mode source parsing → `ModelSettings.effectiveSource`
- tokenizer.json auto-fetch → `TTSEngine.ensureTokenizerJSON`
- Reference resample 24 kHz mono → `TTSEngine.loadReferenceAudio`
- Auto-transcription → `ReferenceTranscriber.swift`
- Saved voices → `VoiceLibrary.swift`
- Backup/restore → `BackupManager.swift`
- Settings tabs → `SettingsView.swift`
- Logs → `LogStore.swift`, `LogsView.swift`
- Busy-close → `WindowCloseGuard.swift`
