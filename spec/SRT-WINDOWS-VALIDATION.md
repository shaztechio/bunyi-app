# SRT Windows proof

Validated 2026-11-01 (Asia/Singapore). Scope: Windows/.NET, Preset voice and
Voice clone. Voice design is intentionally excluded. Native macOS parity is
tracked in [#310](https://github.com/shaztechio/bunyi-app/issues/310); Linux has
the same .NET implementation but was not exercised in this Windows session.

## Implemented

- UTF-8 import with editable timestamps/text, preserved original text and cue
  identities, spoken-text preview, caption/markup flags, inclusion/exclusion,
  estimates, and row-level diagnostics/error navigation.
- Sequential complete-cue generation with a disk cache outside Outputs,
  cue preview, regeneration of a selected cue, Stop/resume, configuration and
  text invalidation, and stale-completion rejection.
- Complete sample-count checks: own-end overruns warn, next-included-start
  overlaps block export, equality fits, and the final cue can extend the track.
- Streamed timeline silence, fixed placement, cue edge fades, one uniform
  clipping-protection gain, atomic WAV-last publication, and a versioned JSON
  sidecar. History discovers one track and copy/Trash includes its companion.
- `--srt-file` through disposable CLI and resident server, structured success
  and timing-error diagnostics, and pre-model input validation.
- Help and shared specification updates. Model-only backup remains model-only;
  output-folder backups should retain both WAV and sidecar.

## Automated validation

The full Windows core, headless Avalonia, and CLI suites pass. The initial full
pass had 934 core, 405 UI, and 98 CLI passes; two additional core tests for stale
completion and single-cue regeneration were added afterwards. The final full
pass has 936 core, 405 UI, and 98 CLI passes. Eight existing environment-gated
Preset pipeline tests are skipped; actual model generation was run separately.
Release builds have zero warnings/errors and self-contained `win-x64` desktop
publication succeeds.

Targeted coverage includes BOM/newline/Unicode/markup normalization, malformed
blocks/encoding/times, duplicate numbering and stable IDs, exclusion, display
versus speech overlap, exact sample equality and a one-sample overlap, cache
invalidation/reuse, cancellation, stale completion, non-finite output, sparse
size-limit refusal, uniform gain, exact silence/placement, sidecar copy,
cleanup, engine publication, and CLI preflight.

History metadata reading now seeks past WAV audio rather than reading an entire
possibly long sparse track into RAM. Existing metadata reader tests pass.
Headless UI checks prove control/binding behavior, not what Narrator speaks.

## Real-model runs

Used the branch's built CLI (app version 1.3.4), English, CPU ONNX exports from
`https://models.bunyi.app/onnx/customvoice` and `voiceclone`. Preset used `ryan`
without style. Clone used the synthetic Preset sentence and exact transcript
from [the earlier experiment](SRT-EXPERIMENT.md). No private voice recording,
transcription service, model download, or personal output folder was used.

Each SRT contains two `Hello.` cues. The first cue in the warning examples uses
`<i>Hello.</i>` to exercise normalization. Cue 1 starts at 1.000 s and ends
at 1.200 s. Cue 2 starts at 4.000 s (or 30.000 s for the wider Preset retry) and
ends 0.200 s later. The overlap fixture instead starts cue 2 at 1.100 s and ends
it at 2.000 s; cue 1 ends at 1.050 s.

| Trial | Outcome |
|---|---|
| Preset, 4 s next start | Correctly blocked: first 3.84 s take overlaps by 0.84 s; both takes retained for diagnostics |
| Clone, 4 s next start | Exported 4.56 s WAV; cue durations 0.64 / 0.56 s, both own-end warnings |
| Preset, 1.1 s next start | Correctly blocked: 2.4 s first take overlaps by 2.3 s; exit 7 |
| Preset, 30 s next start | Exported 50.8 s WAV; complete takes 2.24 / 20.8 s, both own-end warnings |
| Clone via resident server | Exported 4.8 s WAV; complete takes 0.64 / 0.8 s, both own-end warnings; test server stopped afterwards |

[Recorded results](SRT-WINDOWS-RESULTS.json) include diagnostics and successful
WAV hashes. Independently read all three successful WAVs with Python's `wave`
module, verified PCM16/24 kHz/mono, exact sample lengths from sidecar timing and
clip counts, and zero-valued initial/inter-cue gaps. The two blocked calls added
no WAVs or partial files. Logs/audio are local under `tmp/srt-acceptance/` and
are not committed. Generation is stochastic; these timings are observations,
not guaranteed durations for these words.

## Native UI inspection and remaining limits

Opened the Windows app, imported the fixture through the native picker,
inspected the rendered cue editor and actual UIA names for timestamps and text,
edited cue text to `[music]`, observed the caption flag, and unchecked inclusion.
The table scrolls inside the form; voice options remain reachable below it.
The app was closed after inspection. This was not a Narrator listening pass.

The 20.8 s Preset take for `Hello.` is a material model-quality concern. The
model ended the take normally, so timing validation uses its entire duration;
it does not establish whether only the requested word was spoken. No automatic
cutoff, trim, or guessed speech boundary was introduced. These runs establish
generation/timing/publication behavior, not content accuracy or release-quality
listening. Listen to generated cues before using the track. Multilingual,
screen-reader listening, macOS/MLX, and Linux acceptance remain unverified here.

The locally published desktop build is in `tmp/srt-acceptance/win-x64/`. Keep the
whole folder together; `Bunyi.App.exe` is the entry point. It is an unsigned
development build, not an installer or release.
