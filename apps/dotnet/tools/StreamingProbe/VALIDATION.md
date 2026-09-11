# Windows streaming demonstration — 12 September 2026

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
