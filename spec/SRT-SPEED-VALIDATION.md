# SRT speed fitting — Windows validation

Windows is the proving implementation; native macOS parity remains tracked in
[#310](https://github.com/shaztechio/bunyi-app/issues/310). Preset and Clone SRT
only. No TTS model, new dependency, or external audio executable is needed to
adjust a completed take.

## Behavior

**Speed up to fit** processes the original cached take with pitch-preserving
offline waveform similarity overlap-add. It keeps sample rate at 24 kHz and
anchors the first and last windows to the actual endpoints. Input windows
overlap throughout; processing does not just cut the file at its deadline.
The result has exactly the target sample count. Processing buffers remain
bounded independently of cue length. No silence detector, resampling, or
additional speech generation is used.

The target is the next included start, or the final cue's own end. Required
speed above 1.25x is refused before replacement. Repeated fitting always starts
from the original. Reset restores the original preview/audio. Timestamp edits
recheck the current adjusted take without silently refitting it. Cancellation
or failure leaves the prior accepted take in use. Preview and final assembly
use the adjusted audio, and original/current sample counts and speed appear
in the output sidecar. Changing text/voice or regenerating removes the affected
original and adjustment together.

Save/Open retains both raw versions and their checksums. Projects with an
adjustment use schema 2 and `adjusted/<cue-id>.f32`, so earlier readers reject
them rather than silently losing the original or speed. Without adjustments,
Save continues to use schema 1; Reset can return a project to that format.

## Checks

Core tests cover:

- A 440 Hz test tone compressed at 1.2x keeps a measured frequency within
  435–445 Hz, rather than the 528 Hz that plain resampling would produce.
- Exact target length, finite samples, preserved first/last samples, and a
  final sound shorter than the processing window remaining in the result.
- Resolving an overlap while retaining an own-end warning, exporting without
  any further synthesizer call, and exact restoration of the original preview.
- Refitting from the original, deadline edits, exclusion/last-cue deadlines,
  text invalidation, exact 1.25x acceptance, above-limit refusal and Stop.
- Sidecar provenance, version 2 project round-trip, Reset/version 1 output,
  and rejection of invalid speed, length, checksum or downgraded version.

Headless cue-table tests load cached audio, invoke the actual speed and reset
commands, verify the overlap/error state and export label, and verify preview
uses the adjusted path. The fake TTS engine receives no generation request.
These tests prove control behavior, not Narrator/Orca announcements.

## Existing speech sample

Processed the earlier synthetic Preset sentence (“Hello! We'll begin in just a
few minutes.”) from [the original experiment](SRT-EXPERIMENT.md), entirely
offline. The 3.76 s WAV became exactly 3.20 s at 1.175x; output was finite and
PCM16 mono/24 kHz. Source WAV SHA-256:
`774293102ba8a44dc740652c38eec1e5eb4f9c342c729e3ca4faf3e3320a89c5`.
Adjusted WAV SHA-256:
`866d37dc4b481f82499f2a1c99aa99746a2671d8125961438d5d33a11a4fc2b9`.
The sample WAVs, a small probe and measurements remain under `tmp/srt-speed/`.
No user project was read or changed, and no inference was rerun.

This is numerical and workflow validation, not a listening or transcription
acceptance pass. Speech can acquire artifacts when compressed; the UI/help
explicitly asks users to preview it. No claim of guaranteed word accuracy or
universal voice-quality preservation is made.

The self-contained Windows app is published under `tmp/srt-speed/win-x64/`.
Existing running builds were left untouched.

The Windows regression run passed 960 core, 407 headless UI and 98 CLI tests
(1,465 total), with eight existing environment-gated model tests skipped.
Release build and self-contained publication succeeded. Targeted speed/UI
checks also cover disabling Fit when its current duration already matches the
deadline, and enabling refit after a timestamp change.
