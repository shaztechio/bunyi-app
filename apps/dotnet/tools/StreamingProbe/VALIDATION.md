# Windows streaming demonstration — 12 September 2026

The playback panel now includes a persistent seconds-played counter during the
streaming session. All 398 App tests pass, including assertions that the counter
uses consumed PCM samples, excludes buffering silence, freezes on Stop, resumes
from the same position and stays visible during refills and final playback drain.

## Corrected streaming startup

First playback now starts automatically once **10 seconds of playable PCM**
are ready, regardless of estimated recording length. Later refills adapt between
10 and 20 seconds. The UI labels the playback target separately and offers
**Play now** during refills once 10 seconds are available, with a pause warning.
The earlier whole-recording deficit policy below is historical and superseded.

The corrected real CPU clone probe started device consumption at **40.78 s**,
while synthesis finished at **73.47 s**. It generated 19.44 seconds of audio and
drained at 79.19 s with no device error. It did pause to refill, as expected for
generation slower than playback. See `results/clone-early-metrics.json`.
All 398 App tests passed, with the subsequently extended 12 streaming tests and
nine Core buffer tests also passing. Tests pin first startup to 10 seconds even
with a 97-second estimate, cap refills at 20, and verify Play now affects one
refill only and never bypasses the minimum.

## Earlier adaptive-buffer update (superseded)

The latest demo uses generated speech progress and conservative measured PCM
production rates to choose a buffer target, with a 10-second minimum. Slow
runs may wait for most or all speech. A private disposable PCM cache feeds a
20-second device ring, so a larger target does not block generation or require
unbounded audio memory. The forecast target is bounded by the expected unplayed
remainder and extends when generation outlasts the original estimate.

Validation includes steady-rate simulations at 0.2, 0.5 and 1.5 seconds of audio
per wall-clock second without underruns, slowdown/stall adjustments, duration
underestimation, a 45-second disk-cache round trip through the actual feeder and
device callback without a native device, and cancellation/cache cleanup.
All nine adaptive tests pass, alongside 398 App tests and 95 CLI tests. The
remaining 818 Core tests pass; eight model-dependent tests are skipped.

A real CPU clone probe through the disk-backed player produced 19.44 seconds of
audio, completed synthesis at 72.43 seconds, began device consumption at 72.45
seconds and finished draining at 92.08 seconds, with no device error. It waited
for the complete recording, demonstrating the conservative slow-run behavior.
The subsequent forecast-display cap does not change that run's start decision.
The report is `results/clone-adaptive-metrics.json`. These timings are a single
run, not a guarantee against future stalls or inaccurate duration predictions.

## Earlier fixed-buffer demonstrations

The updated demo buffers at least **10 seconds of playable PCM** before starting
or resuming, with a bounded 20-second queue. Completed shorter remainders drain
immediately. The timing measurements below describe the earlier two-second
buffer build and do not measure startup latency with the new buffer policy.
The updated App test suite passes all 396 tests, including exact startup and
resume thresholds and playback of a completed remainder below the threshold.
The subsequent playback-status panel adds explicit wait/resume messages and
playable-buffer progress. Its UI tests exercise pause, resume, finalization and
deferred accessibility announcements; these are not screen-reader audio tests.

Validated on Windows using the installed default self-hosted ONNX exports,
CPU execution, the same 54-word English passage in each mode, and one continuous
generation per mode. Each result below is one run, including model load in the
wall times; these are not repeat-run performance guarantees. The estimate was
18.7–29 seconds, which selects streaming because its upper bound exceeds 20.

| Mode | First PCM callback | Generation complete | Output audio | Preview chunks | Matching sample counts |
|---|---:|---:|---:|---:|---:|
| Preset Voice | 19.89 s | 146.66 s | 22.16 s | 12 | 531,840 |
| Voice Design | 11.16 s | 93.79 s | 24.56 s | 13 | 589,440 |
| Voice Clone | 11.47 s | 81.97 s | 19.44 s | 10 | 466,560 |

All three probes used the actual SoundFlow playback device and reported no
device failure. The final clone run additionally measured **11.53 seconds to
the first audio-device read**; this is not a microphone measurement of acoustic
latency. Preset/design first callback times must not be presented as measured
first-sound times. A later queue correction accounts for the held 256-sample
gain-smoothing tail so the first two-second block can start immediately;
the final clone run includes that correction.

The clone reference was a separately generated 4.4-second synthetic preset clip
with its exact transcript. Private user voice recordings were not used. Actual
clone output finished below 20 seconds, which is consistent with routing based
on the frozen estimate rather than actual duration.

For every mode, preview samples were contiguous and the final sample counts
matched the full vocoder output of the **same generated codes**. Relative RMS
differences between rolling preview and full float decoding were 4.14% (preset),
13.91% (design), and 4.15% (clone). This demonstrates coverage and working
streaming, not identical waveforms or a completed listening-quality evaluation.
The preview uses 25-frame chunks, 50-frame left context and five-frame lookahead;
production context tuning and listening acceptance remain tracked in the plan.
The final WAV retains the existing full decoder and one uniform gain adjustment.

The CPU generated slower than playback, so the preview buffered between parts.
Waiting silence is not included in either saved comparison WAV. The extra
preview decoding costs CPU time; the demo does not claim faster total generation.

Validation completed:

- Release solution tests after integrating main `d04e748`: **818 Core passed,
  8 model-dependent tests skipped; 394 App and 95 CLI passed** (1,307 passed).
  The real-model probes above were run before this integration and are separate
  from those skips.
- Tests cover the strict 20-second boundary, all-mode callback wiring, held-tail
  startup, sample offsets, clone context exclusion, cancellation, invalid samples,
  decoder/device failure, History arbitration, no repeated final autoplay,
  frame-limit rejection, output commit and accessibility semantics.
- Self-contained `win-x64` publish with version suffix `streaming-demo` succeeded.
- Independent code review completed and its cancellation, invalid-audio,
  playback-arbitration, gain-continuity and file-commit findings were addressed.

Raw timing/error reports are in `results/`. Generated WAVs are local artifacts,
not repository assets. Linux runtime audio and macOS implementation/validation
are pending; see [STREAMING-PLAN.md](../../../../spec/STREAMING-PLAN.md).
