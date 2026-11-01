# SRT timed speech — feature plan

Status: planned; not implemented. This document records the agreed product
decisions and the implementation sequence. Before implementation, promote the
observable contract into [FEATURES.md](FEATURES.md), the storage contract into
[DATA-FORMATS.md](DATA-FORMATS.md), and the automation contract into
[CLI.md](CLI.md). Both native apps must implement the same behavior.

## Outcome and scope

Import an `.srt` file as a timed script and produce one 24 kHz mono WAV with
each included cue spoken at its start timestamp. The recording begins at
00:00:00; silence fills the time before the first cue and between clips.
The user can align the WAV with a video without moving individual lines.

SRT is an input type alongside ordinary text, available for Preset voice
and Voice clone only. Voice design is outside SRT support. Use one selected
voice and one set of voice settings for the entire file. Multiple speakers, video import, automatic
rewriting, automatic speed changes, and truncated speech are outside v1.

## Confirmed timing rules

For each included cue, measure the complete generated audio and calculate its
finish time from its start timestamp. The next cue means the next **included**
cue; excluded cues do not create speech deadlines.

| Result | Condition | Export |
|---|---|---|
| Pass | Speech finishes at or before its own end and does not overlap the next cue | Allowed |
| Warning | Speech finishes after its own end, but at or before the next cue starts | Allowed |
| Error | Speech finishes after the next cue starts | Blocked |

An overlap error takes precedence even if speech fits inside its own subtitle
interval. Finishing exactly at the next start is allowed. Overlapping subtitle
display intervals alone do not prove a speech overlap: report that conflict as
an import warning, then use actual audio to decide whether export is possible.

The final included cue has no next-cue deadline. Exceeding its own end is a
warning. Output duration is the later of the last included cue's end timestamp
and the final speech finish. Excluded cues add no trailing duration. A track
matching an entire video's duration is outside v1.

Example: a cue starts at 10.000 s, ends at 12.000 s, and the next included cue
starts at 13.000 s. A 1.8 s clip passes; a 2.5 s clip warns; a 3.4 s clip
fails with “Speech overlaps cue 13 by 0.4 seconds.”

Pre-generation duration estimates are advisory. Bunyi's existing speech
estimator can flag likely overruns, but estimates must never be presented as
verified fit or block generation solely because they predict an overlap.
Timing validation checks duration, not whether the model spoke every word
correctly. Preview remains necessary for pronunciation and content quality.

## Import and editable cue table

1. Choose an SRT file using the native file picker. Read it locally; preserve
   the original file unchanged.
2. Parse cue numbers, start/end timestamps, and multiline text. Support UTF-8
   with or without BOM and LF/CRLF/CR line endings. Unsupported encoding gets
   an actionable error rather than silently corrupted text.
3. Show an editable table: Include, original cue number, Start, End, Text,
   timing status, measured duration, and overrun/overlap amount. Provide cue
   preview once audio exists. Preserve source order and original numbers;
   use an internal stable identity so duplicate numbers cannot mix up clips.
4. Join display line breaks within a cue with spaces for speech. Remove a
   documented allowlist of presentation tags (`<i>`, `<b>`, `<u>` and their
   closing tags) while preserving the original imported text separately.
   Show the normalized spoken text beside the editable cue text so users can
   review what will be generated. Flag unknown markup for review rather than
   speaking or deleting it silently. Define the exact normalization fixtures
   in the feature spec before parser implementation.
5. Flag likely non-speech captions such as `[music]` and `[door closes]`.
   These are suggestions, not reliable semantic classifications: keep them
   included until the user excludes them. Allow any cue to be excluded or
   re-included. Do not automatically remove bracketed text or speaker labels.
   Distinguish caption flags from duration warnings and generation errors.
6. Permit text and timestamp editing directly in the table. Validate edits
   immediately, with a reason attached to the affected cell/row. All included
   cues need non-empty spoken text, nonnegative starts, ends after starts,
   and strictly increasing starts in source order. Do not silently reorder
   cues. Identify malformed import blocks by source location and preserve
   enough information to correct them; never silently skip malformed input.

Allow export only with at least one included cue, valid included rows, complete
current audio for every included cue, and no measured overlap errors. An
excluded, parseable row does not block export. Structural parse failures that
prevent reliable cue boundaries must be corrected or re-imported.

Keep the cue table keyboard accessible, with labeled editors, status text that
does not depend on color, and navigation from an error summary to its row.
Explain that excluding a cue means its words will not be spoken.

## Generation, correction, and assembly

- Freeze the current cue snapshot and voice configuration for each job. Disable
  inputs while generating; keep Stop, Help, and logs available. Show cue-level
  progress alongside the existing frame/audio progress.
- Generate cues sequentially while retaining the model. Never use timing
  deadlines as inference cutoffs or accept truncated output as a fitting clip.
  Existing long-text recovery may run inside a cue, but any internal pauses
  count toward that cue's measured duration. Ordinary inter-section gaps must
  never be inserted between SRT cues or shift their start timestamps.
- Preset retains the selected speaker and style; Clone reuses the selected
  reference recording and exact transcript throughout. SRT does not establish
  or depend on a Voice design continuation reference.
- Retain completed clips in a temporary run workspace on Stop or a timing
  error so users can correct individual cues without regenerating everything.
  Allow regeneration of one cue; a new take may have a different duration.
  Continue checking remaining cues after timing errors to provide a full
  report. Runtime failures remain visible and resumable where practical.
- Text edits invalidate only that cue's audio. Timestamp edits and inclusion
  changes retain reusable audio and recalculate all affected timing statuses.
  Changes to the voice, style, language, model, or clone reference invalidate
  dependent clips. Never combine clips with stale generation settings.
- Measure and place audio using integer sample positions, with a single
  documented timestamp-to-sample conversion. SRT milliseconds map exactly to
  24 kHz samples. Use sample counts for comparisons; rounded display values
  must not hide a small overlap. Never accumulate rounded cue durations.
- Place the complete clip at the timestamp without heuristic silence trimming
  that could cut quiet speech, and explain that audible onset can be later
  (about half a second in the initial Windows Preset trials). Do not promise
  word-accurate synchronization. Validate real-model onset behavior and revisit only with a safe,
  explicitly specified approach. Count trailing silence in measured duration
  too; this may conservatively report an overlap after the words have ended.
  A previewable manual trim operation is a possible future improvement, not
  part of v1.
- Assemble only a valid track. Use bounded buffers or streaming writes so a
  long sparse timeline does not require allocating its entire silence in RAM.
  Apply existing edge-fade and uniform clipping-protection rules without
  changing placement or duration. Reject non-finite samples.
- Save atomically, then create one History entry and offer playback/reveal.
  Never publish a partial or overlapping track as a successful output. Working
  cue clips are not individual History results. Clean temporary files when
  replacing/closing the session, with a guard before discarding reusable work.
  Cross-restart project recovery is outside v1.

## Persistence and automation contracts

Define versioned metadata for timed output before implementation: source kind,
original cue identity, edited timestamps/text, inclusion, normalization policy,
voice settings, model, and actual durations. Preserve enough
information to explain the generated timeline without requiring the original
SRT file. Keep existing standard metadata embedded in the WAV and store the
full timeline in a versioned JSON sidecar beside it. History and backup/restore
treat the pair as one output; the WAV remains independently playable without
the sidecar. Define naming, schema, size limits, atomic publication, missing or
invalid sidecar handling, and pair deletion in DATA-FORMATS.md. Avoid unbounded
RIFF text fields. Reopening an editable project remains outside v1.

Include CLI/server support in the initial implementation. Specify SRT input,
structured per-cue diagnostics (identity, actual duration, status, overrun and
overlap), cancellation, and successful output metadata in CLI.md. Warnings
permit success; overlaps return failure and publish no final WAV. Callers can
submit corrected SRT files without the desktop table.
The automation path must use the same timing and normalization rules. Desktop
cue editing need not imply a persistent project format in v1.

## Implementation sequence and acceptance

Pre-implementation research is recorded in [SRT-EXPERIMENT.md](SRT-EXPERIMENT.md).

1. Translate the accepted normalization, metadata, and CLI decisions into
   exact shared specifications before app changes. Record the short-cue and
   leading/trailing-silence experiment; Voice design measurements are research
   only and do not gate or expand SRT's two-mode scope.
2. Implement independent UI-free parsers, validation, timing arithmetic, and
   track assemblers in Swift and C#. Use identical fixture inputs and expected
   outcomes to enforce parity without sharing runtime code.
3. Add cue generation orchestration, reusable clip state, cancellation, and
   dependency invalidation in both engines.
4. Add import, editable cue tables, caption flags, cue preview/regeneration,
   error navigation, History integration, and user help in both apps.
5. Implement CLI/server support and durable output metadata. Run platform CI
   and real-model acceptance before declaring parity complete. Land through
   pull requests; any staggered platform delivery needs a tracked follow-up.

Required checks:

- Parser fixtures: BOM/newlines, multiline text, Unicode, punctuation, tags,
  caption flags, duplicate numbers, malformed timestamps/blocks, empty input,
  negative/reversed times, and unsorted/equal starts.
- Exact sample fixtures: initial silence, gaps, pass/warning/error thresholds,
  equality at the next start, overlapping subtitle intervals, excluded cues,
  final-cue extension, and long sparse timelines.
- Editing fixtures: text versus timestamp invalidation, exclusion/re-inclusion,
  voice changes, regeneration, stale completion rejection, Stop/resume, model
  failure, cleanup, and prevention of partial successful export.
- UI checks: editable table validation, keyboard access, error navigation,
  non-color statuses, disabled inputs during work, and reachable Stop/Help/logs.
  Verify actual screen-reader behavior separately from toolkit peer tests.
- Real-model listening on macOS and Windows/Linux: Preset and Clone, short
  cues, multilingual text, voice continuity, audible onset, complete words,
  warning-only export, overlap correction, and alignment against a known
  timeline. Record any model limitations rather than treating duration checks
  as proof of speech accuracy.
