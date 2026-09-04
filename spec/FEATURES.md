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
- **The run needs**: text in every mode, plus a voice description for voice
  design, and a reference clip *and its transcript* for voice clone. Checked
  before the button is pressed, not by the engine — the engine rejects a clone
  with no clip only *after* preparing the model, which on a first run means
  waiting out a multi-gigabyte download to be told a field is empty. Voice
  design had no check at all and would generate an arbitrary voice from an
  empty description.
- **Generate stays pressable when something is missing**, and says what when
  pressed: the field is marked, focus moves into it, and the reason appears
  beside it and in the status line. The mark clears as soon as the field is
  filled, and nothing is marked before Generate is pressed.
  - **Not disabled.** A disabled button cannot be hovered for the tooltip that
    would explain it, is skipped by screen readers, and — as shipped — did not
    even look disabled, so it read as an action that silently did nothing. An
    action that cannot be taken must still be able to say why.
- **An unused window suggests something to click.** The first frame is
  otherwise an empty box, a "ready" line and a button that does not work —
  which for a non-technical audience is a dead end rather than a starting
  point. So while the script is empty and no result is on screen, the mode
  offers two or three example prompts, one click each:
  - **Preset voice** offers short sentences and fills the script with the one
    clicked.
  - **Voice design** offers voice descriptions and fills the **voice
    description** field, not the script. That field is what the mode adds and
    is the one input whose shape nobody guesses; the script is a sentence
    anyone can write.
  - **Voice clone** gets none. What it lacks on a first run is a reference
    recording, which the app cannot supply, so filling in the one input it
    already has would leave Generate exactly as unavailable — an example that
    does not unblock anything teaches the wrong thing about why the button is
    off.

  An example is ordinary prefilled text, editable afterwards: not a preset, not
  a mode, and nothing is recorded about which one was used. They disappear as
  soon as the script has anything in it, and **do not return over a generated
  result** — an invitation to try something belongs to a window that has not
  been used yet, not beside audio the user just made. They are inputs, so §2's
  rule covers them: disabled while work is in progress.

- **Emotion for clones is not supported** by the 12 Hz Base model. Do not
  add an emotion field to clone mode. Emotion for a cloned voice must come
  from the reference clip's own delivery. (Tracked upstream: 25 Hz models.)

- **The clone model must be an in-context-learning (ICL) export.** §4 makes the
  reference transcript effectively mandatory because cloning works by aligning
  the recording to its words — which requires a model that takes the reference
  *audio codes and text* as context. Some published exports instead reduce the
  clip to a fixed-size speaker embedding and take no transcript at all. Such a
  model must not be used to implement this mode: it loads, it runs, it returns
  audio in a plausibly similar voice, and it silently ignores the transcript —
  so the field the UI presents as required does nothing, and the failure is
  invisible to everyone except someone comparing it against a real clone. A
  runtime family that has no ICL export available ships without clone mode and
  records the gap, rather than shipping a different feature under its name.

## 2. Generation output

- Sample rate **24 kHz**, mono, WAV.
- Saved to an `Outputs` subfolder of the app's per-user data folder — macOS
  `~/Library/Application Support/Bunyi/Outputs` (inside the sandbox container
  — no extra file-access entitlement needed), Windows
  `%LOCALAPPDATA%\Bunyi\Outputs`, Linux `$XDG_DATA_HOME/Bunyi/Outputs`. The
  roots are pinned in `DATA-FORMATS.md`; the subfolder name is `Outputs`
  everywhere. One click away via the in-app reveal-in-file-manager button.
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
  being produced. **Help and the log stay reachable**: a long download is
  exactly when someone wants to read the help or watch the log, and neither
  touches the running job.
- **Help and the log are one click from the main window**, not only in menus —
  the audience does not go looking in a menu bar. *Where* they sit is a
  platform choice: macOS puts them in the window toolbar, which also places
  them outside the disabled scope by construction; a toolkit without a native
  window toolbar (Avalonia) keeps them in a header row beside the mode
  subtitle. Same two actions, same always-available guarantee, same tooltips.
- **Live progress in the status line during generation**, in every mode: how
  many codec frames have been produced **and the seconds of speech they amount
  to** — frames are 12.5 a second, and a count of frames means nothing to a
  person while "3.9s of speech so far" tells them whether the run is on its
  way to what they asked for or rambling. Both apps show the same two numbers,
  updated as frames arrive (macOS updates every five). macOS uses
  `generateStream` (preset/design) and an `onToken` callback bridged over a
  stream (clone); Avalonia reads the per-frame progress its own pipeline
  reports. Any backend must surface incremental progress, and a generation
  that shows only a spinner for a minute is a bug, not a slow model.
- **The UI thread never does inference work, and never writes the output.**
  Includes the step that forces a lazily-evaluated tensor to be materialized:
  on macOS the generator yields an unevaluated MLX graph and
  `saveAudioArray` is what evaluates it, so writing the WAV on the main actor
  froze the app at the end of every generation. Any runtime with deferred
  evaluation has the same trap in a different place — the rule is that the
  window stays responsive for the whole run, not that one named call is moved.
- **The runtime's working memory is released once the output is written.** ML
  runtimes keep freed buffers rather than returning them, which is right during
  a run and wrong after one: that cache is real RAM held against work that has
  finished. The model stays resident — only the cache goes. Every runtime has
  this behaviour under a different name and needs its own call (MLX: the GPU
  buffer cache; ONNX Runtime: per-run values and the allocator arena), so the
  requirement is the outcome, not a named function: after the WAV is written,
  the memory a finished run was using is back. It is released on **every** exit
  path — success, stop, and error — and never while abandoned work is still
  allocating, which would hand back buffers that work is about to ask for
  again. This matters most on the machines the app is aimed at: a multi-gigabyte
  model plus a long generation's leftovers is enough to push a 16 GB machine
  into swap, and a swapping machine stalls audio playback and freezes the
  window for seconds at a time.
- **Stop**: while any work is in progress — model download, transcription,
  model load, or generation — the Generate button is **replaced** by a Stop
  button (Escape). Not merely disabled: a model download runs for minutes,
  and without Stop the only way out is closing the window and confirming
  (§9).
  - **Both carry a glyph beside the label** — a waveform on Generate, a filled
    square on Stop — and both are the same width, so the swap changes what the
    button says without moving the row. Centred as a pair: with an icon, aligning
    the words alone would push the glyph off the edge to compensate.
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
generated so far, newest first**: mode, date, size, with play/stop per row
(see below — there is deliberately no pause), a **Download** button, and
reveal-in-file-manager.

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
  to hit, and the audio may be the only copy. Every platform has a recoverable
  delete and the app uses it — the macOS Trash, the Windows Recycle Bin, the
  freedesktop trash directory on Linux. Unlinking the file is not an
  implementation of this on any of them.
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
list above any saved ones and has no Delete button — nothing about it is on
disk. Saving a configuration of the same name (case-insensitively) **replaces
it in the list**: the user's entry stands in for the built-in entirely, keeps
its ordinary alphabetical place, and is deletable like any other. Deleting it
brings the built-in back, which was hidden rather than gone. It exists because
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
>
> | Mode | MLX default (macOS) | ONNX default (.NET) |
> |---|---|---|
> | Preset voice | `mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16` | `elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX` |
> | Voice design | `mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16` | `wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX` (`int4`) |
> | Voice clone | `mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16` | `wavekat/Qwen3-TTS-0.6B-Base-ONNX` (`int4`) |
>
> The clone defaults are **different sizes** across families, deliberately:
> macOS uses a 1.7B Base model, and no 1.7B ONNX export meeting the ICL
> requirement in §1 is available, so the ONNX family uses the 0.6B one. A
> smaller model of the right kind beats a larger one that cannot do the job.
> Both wavekat exports are used at their `int4` variant, because the `fp32`
> variant beside it is large enough that loading one is not a realistic ask of
> the machines this app targets — for voice design, 12.7 GB against 4.3 GB.

### 3b. Download behavior (identical across platforms)
- **Resumable & incremental**: already-present files are skipped — on every
  source, not only the Hub. Stopping a download and starting again must not
  re-fetch what is already on disk; for a self-hosted model that is gigabytes
  of pointless transfer. A file counts as present when its size matches the
  server's, not merely when it exists: an interrupted write leaves a file that
  exists and is wrong. **Where the server supports ranged requests, a partly
  transferred file resumes from where it stopped** rather than starting over.
  The unit that must not be re-fetched is the byte, not the file: one file is
  usually most of the model, so restarting it is close to restarting
  everything.
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
- **https recommended.** Plain http is blocked outright on platforms that
  block it (macOS: App Transport Security, liftable only by an Info.plist
  exception and a rebuild). Where the platform does not block it, the app
  allows it and **warns**: a line in the log naming the URL, and the same
  warning beside the Settings field. The user typed the address and may be on
  a LAN with no certificate, so this is not an error — but model weights
  fetched over http are unauthenticated bytes, and a client that says nothing
  implies otherwise.

### 3d. Custom models folder
- Default: a `Models` subfolder of the per-user app-data dir
  (`DATA-FORMATS.md`). User can point it at any folder (external drive, etc.),
  **persisted across launches** — by whatever means the platform requires a
  chosen folder to stay reachable (macOS: a security-scoped bookmark, because
  the sandbox does not otherwise re-grant access; elsewhere: the absolute
  path). A location that no longer resolves falls back to the default and says
  so in the log, rather than failing every download against a folder that is
  not there. "Show in the file manager" + reset.
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

### 3e. Model residency

Only one mode's model is ever loaded. It loads on the first generate in a mode
and stays resident, so a second run in the same mode does not pay the load
again.

- **Switching modes unloads the model of the mode being left**, at the moment
  of the switch. These are multi-gigabyte models and nothing else will ask for
  the old one back; several of them held for a mode nobody is looking at is the
  case this exists to end. The generation-mode tabs are disabled while a run is
  in progress (§2a), so a switch can never unload a model a running job is
  using.
- **A generate releases the previous mode's model before it needs the next
  one** — before the preflight, and before any download. Two models are
  never resident at once, whatever the setting below says, and Doctor's memory
  check (§11) measures the memory the run will actually have rather than a
  figure that is about to change. Generating twice in the same mode releases
  nothing: it is the same model, and it stays loaded.
- **Settings → General → "Free memory when switching modes"** turns
  this off. Default **on**, persisted under `unloadOnModeSwitch`. Off keeps the
  previous mode's model resident so returning to that mode is instant, at the
  cost of several gigabytes held for a mode nobody is looking at; the unload
  then happens at the next generate in another mode instead.
- Deleting a loaded model evicts it first (§3d) whatever this setting says,
  and quitting releases everything.

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
  - .NET (Win+Linux): Whisper (whisper.cpp), so the same words come out on
    both OSes and nothing leaves the machine.
    - The model is **fetched on first use** through the same downloader as
      everything else (§3b: progress, resume, checksums), not shipped in the
      app. It is a multilingual model, because §1 offers ten languages and an
      English-only one would turn the other nine into confident nonsense.
    - So the first clone on a new machine downloads it, with the usual
      progress; every clone after that is offline. Shipping it instead would
      add its weight to every download, including for the people who never
      open clone mode.
  - The transcriber is told **which language §1 has selected**, rather than
    detecting it — the user has already answered that question, and a model
    guessing differently transcribes the right sounds into the wrong words.
    "Auto" is the one case where it detects.
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

Tabbed window (macOS: ⌘,). Tabs — the window title reflects the selected tab,
**and names the window before it** where the platform's own convention is to
name the window: the .NET app titles it **"Settings — General"**, macOS titles
it "General" alone. macOS source: `SettingsView.swift`.

> The parenthetical here used to read "(platform convention)" and both apps
> titled the pane alone, which is macOS's convention applied everywhere. Windows
> does the opposite — Windows Settings and tabbed options dialogs across the
> platform name the dialog, with the section shown in the content.
>
> It surfaced as an accessibility defect rather than a cosmetic one. A window's
> title *is* its accessible name — Avalonia's `WindowAutomationPeer` is
> `GetNameCore() => Owner.Title`, overriding the usual lookup and ignoring
> `AutomationProperties.Name` — so pressing the chord with a screen reader on
> announced "General", which is no confirmation that a settings window opened at
> all, and there was nowhere else to put the word. See #196.
- **General**: appearance — **System / Light / Dark**. System follows the OS;
  Light and Dark pin the app regardless of it. Applies immediately, to
  **every** window the app owns, not only the one in front (macOS: the main
  window, Logs, and Settings itself). Persisted per-user under the key
  `appearance`, defaulting to System.
  Also **"Free memory when switching modes"**, a checkbox, **on** by default
  and persisted under `unloadOnModeSwitch` — see §3e for what it does
  and what turning it off costs.
- **Models**: the three per-mode source fields (repo ID or base URL) + help.
- **Storage**: models-folder location controls + pre-download commands.
- **Backup**: back up / restore / stop + status.
- **About** (.NET only): name, version, platform, licence and copyright —
  see §9a. macOS has no such tab; AppKit's About panel covers it there.

The appearance setting is why the app's visual design cannot be built on a
single fixed palette: any colour that only works against one background is a
bug in the other two states. Brand colour belongs in an accent, a gradient and
rule/badge treatments, which survive both — not in a window background.

## 8. Logs

macOS source: `LogStore.swift`, `LogsView.swift`.
- A separate Logs window (macOS: Window → Logs, ⌘L) with timestamped,
  selectable, monospaced lines, autoscroll, Copy + Clear.
- **The log is one text pane, not one control per line.** A selection must run
  across lines: copying a run of them into a bug report is what the window is
  for. A control per line cannot do it — a drag cannot cross from one view
  into the next, so each line selects alone however the line is built, and
  moving the boundary (a timestamp column beside a message) only moves the
  problem. macOS uses an `NSTextView`; .NET uses a single `SelectableTextBlock`.
  Both are read-only rather than disabled, because a disabled text view will not
  let you select either.
- **Lines wrap; the pane never scrolls sideways.** A log line can be a file path
  hundreds of characters long, and a horizontal scrollbar puts the end of every
  long line — which is where the detail of an error is — off-screen,
  reachable only one line at a time.
- An arriving line must not disturb someone reading: autoscroll only follows the
  tail when the view was already at the bottom, and a redraw must not clear a
  selection in progress. Clear is the exception, and empties the pane at once.
- Mirror to the platform's system log **where the app can write to one
  unprivileged**. macOS uses OSLog. Windows' Event Log needs an
  administrator-created source, so a per-user app does not qualify: mirror to
  standard error and a rolling file under the data root instead, which is what
  Linux does too. The in-app Logs window is the guarantee; the mirror is so a
  crashed run still left a record.
- Everything notable is logged: model prepare/download progress, per-file
  self-host downloads, tokenizer step, transcription result, generation
  token milestones, saved output path + timing, backup/restore steps, and
  full error text.
- **How long the app took to start, as one line, once the window is up.** "It
  is slow to launch" is otherwise a report with no number in it, and which
  phase was slow is the whole question — the runtime coming up, the UI
  framework, or the app's own work. The line carries the total and the phases
  that make it up.
  - Phase names are the platform's own. What differs between a Mac and a Linux
    box is which subsystem is slow, so a shared vocabulary here would describe
    neither honestly.
  - **Timed from process start, not from the app's first line of code.** Much
    of a slow launch happens before the app is running, and a total that begins
    when the app takes control measures only the part already known to be fast.
  - Where that pre-start span cannot be read, the total says **"at least"**
    rather than counting the missing phase as zero. A lower bound reported as
    an exact figure is a wrong number in a bug report.
  - Measured on a clock that cannot run backwards, so a clock adjustment during
    launch cannot produce a negative phase.

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

### 9a. Naming the build

- The app says **what it is, which version, and which platform** somewhere a
  user can reach without generating anything.
  - macOS gets this free: AppKit's About panel, filled from the bundle.
  - **.NET (Win+Linux)**: there is no equivalent and no menu bar to hang one
    on, so it is an **About tab in Settings** (§7) — where a Windows or Linux
    user looks for it.
- The platform is named, not just the version. Windows and Linux are one
  codebase and look identical, so a version alone does not say which build a
  bug report is about.
- **Credits** for the software the app is built on, and separately for the
  models it downloads — each with its licence and a link that opens in the
  user's browser.
  - The list lives in **`/spec/CREDITS.json`**, read by both apps, so the two
    cannot end up crediting different things. Entries are tagged with the app
    they belong to: the two share the models and almost nothing else.
  - Every licence stated must have been **read from that project's own licence
    file, package metadata, model card, or README** — never from memory. A
    credits list that guesses is a licence claim the project cannot support.
    Note that a project can state its licence only in its README, where the
    GitHub API and file listings both miss it; absence of a `LICENSE` file is
    not absence of a licence. Where a project genuinely states none, the file
    says so rather than leaving it out.

## 10. Error handling & copy tone

- Plain verbs, sentence case, no jargon ("Generate", not "Synthesize").
- User-facing errors are actionable (e.g. "Your server is missing a
  required model file: config.json (HTTP 404). Check the URL…").
- Full technical error text goes to the Logs, not the status line.

## 11. Doctor (preflight checks)

macOS source: `Doctor.swift`, surfaced from `ContentView.swift`.

Doctor answers one question: **can this machine finish a generation right
now?** It is not a settings panel and it does not fix anything — every finding
names what is wrong and what would resolve it.

Each check reports *ok*, *warning*, or *blocker*.

1. **Model present.** Is the selected mode's model completely downloaded, by
   §3b's definition of complete (config plus weights, no partial files)? A
   missing model is **not** itself a failure — a generation downloads it. It
   changes what the other checks are about: checks 2 and 4 then apply to that
   download rather than to a model already on disk.
2. **Disk space**, measured on the volume holding the models folder, which
   §3d allows to be a volume the user chose. When a download is required:
   *blocker* if free space is below the model's size, *warning* if it is
   below that plus 5 GB. When the model is already present, only the space a
   generation's own output needs.
3. **Memory.** Available RAM against the size of the model that must be
   resident — whatever "available" means on the platform, and counting memory
   the runtime is holding but could return. **Warning only, never a blocker** — it is a prediction about a
   run that has not started, the figure moves the moment another app quits,
   and a machine under pressure still finishes, only slowly. Blocking on it
   would refuse runs that would have worked.
4. **Model source reachable.** Only when a download is required: does the
   source configured for the mode (§3a) answer? *Blocker* if not, so a dead
   self-hosted server is reported as a dead server rather than as a missing
   model.
5. **Output folder writable.** *Blocker* if the folder generations are
   written to cannot be written to.
6. **Model files intact**, against the published `manifest.sha256` (§3c)
   where the server offers one. **On demand only** — hashing gigabytes before
   every generation would be a worse problem than the one it detects. This is
   the check that catches a truncated or half-synced model, the failure that
   otherwise loads and speaks nonsense.

7. **Acceleration.** Which execution provider the speech model will run on,
   and — where that is the CPU on a machine that has an NVIDIA graphics card —
   what is missing. **Never a warning**, however much faster the machine could
   be: Doctor runs before every generation, and a machine correctly using its
   CPU is healthy, so this is a row in the report someone opens rather than
   something raised at them. The step that turns the result into sound is
   always on the CPU and the finding says so, because "running on the GPU"
   would otherwise read as all of it.

   **In the words the user meets, not the model's.** The two halves are the
   *talker* and the *vocoder* in the exports, the code and the research notes,
   and neither word appears anywhere a user can see — not in `HELP.md` on
   either platform. A finding or a log line that used them would be the only
   place the product taught them, which is no place at all.

   **Windows and Linux only — a permitted divergence.** macOS has no
   equivalent: MLX always runs on the GPU there, so there is no choice to
   report and nothing that could be missing.

**Before every generation.** Doctor runs before any download begins, because
the point is not to discover after 3.4 GB that there was never room for it.
Blockers stop the run and are reported in a dialog. Warnings do not stop it
and are written to the Logs. When nothing is wrong, the run starts with no
dialog and no interruption — a preflight the user notices on a healthy machine
is a bug.

**On demand**, from a stethoscope button in the window toolbar. The buttons sit
in one group, ordered **Settings, Doctor, Logs, Help** — a gear, then the three
that read by how far the answer is from the app: whether it can run at all,
what it did, what it is. Settings leads because it is the one that changes
something rather than reporting on it. macOS also reaches Settings from the app
menu and ⌘,, which Windows and Linux have no equivalent of; the button is
duplicated there rather than dropped, so a user moving between the two apps
finds the same row in the same order. All are available
while work is in progress for the same reason (§8): a run behaving strangely is exactly when it is wanted. The on-demand run
reports **every** check in a dialog, passes included — "everything is fine" is
the most common useful answer — and writes the same findings to the Logs so
they can be copied into a bug report.

**Every report names the mode it is about.** The checks are per-mode — the
three modes use different models, of different sizes, from sources that are
configured separately — so "the model" is ambiguous unless it is named. It
matters most where there is no mode on screen at all: History is not a
generation mode, so a run started from it reports on the mode last generated
with, which is only sensible behaviour if the report says which one that was.

Findings follow §10: sizes are stated, and each says what to do.

## 12. Keyboard and assistive access

The audience §1 is written for includes people who do not use a pointer, and
people who cannot see the window. Everything below is observable behaviour and
therefore binding on both apps; the *mechanics* are the platform's, so the
chords differ and only the requirement is pinned.

- **Anything that can be done with a pointer can be done from the keyboard.**
  Every control is reachable and operable, including the per-row buttons in
  History that §2a enumerates as affordances. A control that exists only to a
  mouse is a control some users do not have.
- **Focus order follows the visual order.** Tabbing through a window should
  walk it the way a reader does, not the order the view tree happens to
  declare.
- **Lists of the user's own work are navigable.** History holds every clip a
  person has made; reaching the older ones must not require a trackpad.
- **Every window has a keyboard route.** Settings, Logs, Help and Doctor are
  each reachable without clicking a toolbar.
- **Every control announces what it is.** An icon-only button carries a name
  for assistive technology — the tooltip's words are usually right, but a name
  is a separate property and is not inherited from a tooltip on either
  platform. A control whose label sits beside it rather than inside it — a
  picker in a labelled row — points at that label rather than repeating its
  words, so what is read and what is shown cannot drift apart.
- **A control that changes says what it changed to.** Moving through a picker
  from the keyboard announces the new value, not silence. This is not automatic:
  a toolkit may update the control on screen and raise nothing, and the value
  being readable afterwards is not the same as it having been announced. The
  test is whether an event is emitted, not whether the property is correct.
- **What is announced is paced for speech, and is not the same thing as what is
  shown.** §2 has the status line count codec frames as they arrive, several
  times a second. Announcing at that rate says nothing at all: each sentence
  takes seconds to speak, and a reader that is interrupted before it finishes
  never finishes. So a change of *state* is announced at once — it started, it
  finished, it failed — and progress within a state is announced sparingly. The
  window keeps ticking; the voice does not.
- **A field's placeholder is read aloud, so it is written to be heard.** A
  screen reader announces an empty field's placeholder after its name, which
  makes that string the only guidance a person who cannot see the window gets
  about what belongs there. "Optional — how it should be said" earns its keep
  spoken more than written. Placeholders are therefore guidance rather than
  decoration, and a design that replaces them with something purely visual
  takes that away.
- **Anything meant to be announced can be reached from the keyboard, and the
  thing the keyboard lands on is the thing that carries the words.** With a
  screen reader following focus rather than its own cursor — which is the
  default on Windows — an element nothing can focus is never spoken, however
  well it is labelled. A focus stop that announces nothing is worse still.
- **Decorative imagery is not announced.** Glyphs that repeat what an adjacent
  label already says are hidden from assistive technology rather than read
  twice.
- **Nothing that matters is carried by colour alone.** Doctor's severities, a
  destructive action, an error state: each says what it is in words. Colour
  reinforces; it does not inform. The words have to travel *with* the thing they
  describe — a severity on a container the reader may never announce is not a
  severity the reader hears, so a finding is one announced element carrying its
  severity, its title and its detail together.
- **Dialogs behave predictably.** Escape dismisses without acting, Return takes
  the safe default. §9's busy-close prompt already pins *Keep Working* as that
  default.

> **Neither app satisfies all of this today, and this section is written as the
> target rather than a description.**
> [#157](https://github.com/shaztechio/bunyi-app/issues/157),
> [#158](https://github.com/shaztechio/bunyi-app/issues/158) and
> [#159](https://github.com/shaztechio/bunyi-app/issues/159) are the audits.
>
> It is recorded here first for the reason the parity rule exists: both apps
> reached the same gap independently — a list of user content with no
> selectable row — because nothing said they had to do otherwise. A fix in one
> app that is not written down here becomes a divergence in the other.
>
> **macOS** still cannot scroll History from the keyboard at all (#157), and its
> mode picker, Generate and toolbar cannot be reached from the keyboard (#164).
>
> **Windows and Linux** have the rest of this, verified on the real
> accessibility tree rather than on the toolkit's own objects
> ([#192](https://github.com/shaztechio/bunyi-app/issues/192);
> `apps/dotnet/tools/UiaProbe`) — and on **Windows, heard**. Under Narrator on
> 3 Sep 2026, by ear, which is the step no tool here can take: the pickers, the
> Doctor findings, the History rows, the running status, the script and style
> boxes, and the clone transcript. **Four of those were silent when a person
> first listened, while passing everything that had been automated** — which is
> the case against ever closing an accessibility item on a green run. There is
> **one gap that is the toolkit's and not the app's**:
>
> > *"A running generation is announceable"* holds on **Windows only.** The
> > status line is a live region, and Avalonia's Win32 bridge serves
> > `UIA_LiveSettingPropertyId` and raises `LiveRegionChanged` when the text
> > changes — both measured on a running window, and confirmed aloud. **On
> > Linux it announces nothing.** `Avalonia.FreeDesktop.AtSpi` 12.1.1 emits activation, bounds,
> > children, focus, property, selection and state signals and has no
> > live-region concept anywhere in it, so there is no signal for Orca to hear.
> > There is no app-side workaround: the app can only set the property the
> > toolkit does not carry. An Orca user learns a run has finished when the
> > result appears, not while it runs.
>
> **"A control that changes says what it changed to" is new here, and is
> unverified on macOS.** It was added because Windows failed it: the pickers
> moved through their values in silence, and nothing in the app was wrong —
> Avalonia's ComboBox peer raises no property change for a selection. **Windows
> now satisfies it, confirmed under Narrator by ear on 3 Sep 2026**, not only as
> a UIA event. Whether SwiftUI's `Picker` announces under VoiceOver has not been
> checked, and "probably, it usually does" is the assumption that produced this
> bullet in the first place. #158 is where that gets measured.
>
> **The pacing rule holds on Windows, confirmed aloud on 3 Sep 2026. It is
> unverified on macOS, which has the same ingredient.** `TTSEngine` publishes a token count per frame there as well, so
> if VoiceOver is given that as a live announcement it will have the same
> problem: not "too chatty" but *silent*, because nothing is ever allowed to
> finish. Checked on Windows, where it was found; #158 is where macOS gets the
> same look.
>
> Both apps' screen-reader behaviour proper — Narrator and Orca actually
> speaking, in a real desktop session — remains a manual pass under #159 and
> #158. Nothing automated substitutes for it.

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
- Appearance (System/Light/Dark) → `BunyiApp.swift` (`AppAppearance`),
  `SettingsView.generalTab`
- Model residency / free-on-mode-switch → `TTSEngine.unload`,
  `TTSEngine.prepare`, `ContentView` (`onChange(of: tab)`),
  `SettingsView.generalTab`
- Logs → `LogStore.swift`, `LogsView.swift`
- Startup timing → `StartupTimeline.swift`
- Busy-close → `WindowCloseGuard.swift`
- Doctor / preflight checks → `Doctor.swift`, `ContentView.generate`
- Keyboard & assistive access → every view; see §12
