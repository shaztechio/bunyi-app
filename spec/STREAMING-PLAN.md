# Stream long speech in all three modes

Updated 2026-09-12. The Windows demonstration is authorized implementation
work. Production streaming and macOS parity remain follow-ups. Windows real-model
and audio-device results are recorded in the [demo validation](../apps/dotnet/tools/StreamingProbe/VALIDATION.md);
production listening-quality acceptance remains pending.

Observable behavior is defined in [FEATURES.md §2b](FEATURES.md#2b-streaming-long-speech)
and persistence in [DATA-FORMATS.md](DATA-FORMATS.md#streaming-temporary-output).
The [intent brief](plans/2026-09-11-speech-streaming-brief.md) and
[research snapshot](plans/2026-09-11-speech-streaming-research-dossier.md)
record scope and evidence. Source anchors in the dossier describe `3eec46e`;
implementation can move their line numbers. Integration also includes `d04e748`:
the newer CLI runtime, script layout and removal of automatic generation caps.

## Scope and routing

- Preset Voice, Voice Design and Voice Clone all use one continuous inference
  run. Preserve full text, speaker/style, description and reference conditioning.
  Do not restart inference at sentence boundaries or change model families.
- Predict output speech duration from target text; exclude style, description,
  reference audio and transcript. It is an approximate range, not processing ETA.
- Snapshot the request and estimate at Generate, before download, loading or
  clone transcription. Stream only when the unrounded upper estimate is
  **strictly greater than 20 seconds**. Exactly 20 stays on the existing path.
  Freeze this decision even if actual duration or controls later change.
- The threshold adds no confirmation. The earlier 30-second warning proposal
  remains separate. The estimate never imposes a generation cap: normal .NET
  generation ends at natural EOS or Stop, as required by §2.
- The demo estimator uses word/script counts with pause allowances. Its rates
  are provisional; language selection currently does not alter those rates.
  Production requires versioned, calibrated rules and shared cross-app fixtures,
  including auto language, mixed scripts, numbers and abbreviations.

## Windows demonstration milestone

Use bounded rolling-context decoding during a single talker run to feed a live
preview. Keep the existing final full decode of the **same generated codec
sequence** as the authoritative recording. This is a deliberate intermediate
architecture: extra final decoder work and whole-clip memory remain in the demo.

```text
frozen request + estimate
  upper <=20 → existing full generation → validated WAV → existing autoplay
  upper >20  → one continuous talker run → complete codec frames
                 ├→ bounded contextual decode → preview gain → PCM queue
                 └→ natural EOS → final full decode of those same frames
                                  → uniform output gain → one final WAV
```

The queue overlaps audio playback with inference. Decoder/model calls initially
stay serialized on their worker. Never run model work, disk writes or blocking
backpressure inside the UI or device callback.

Demo integration points:

- [TalkerLoop.cs](../apps/dotnet/src/Core/Qwen/TalkerLoop.cs) and
  [RollingAudioPreview.cs](../apps/dotnet/src/Core/Qwen/RollingAudioPreview.cs):
  publish owned float PCM after complete codebooks, keeping KV state and sampling
  continuous. Initial experiment: 25-frame groups, 50-frame left context and
  five-frame lookahead at 12.5 frames/s; these settings require model validation.
  Check actual output/frame layout, exact offsets and a final short group.
- Clone carries reference codec context into the first decoding windows, then
  excludes its samples. Context limits must not leak reference speech, repeat
  samples or erase the opening. Validate rather than infer sufficient context.
- `PresetPipeline.cs`, `DesignPipeline.cs`, `ClonePipeline.cs` and their speech
  synthesizers carry preview chunks through the common engine contract. Short
  requests retain complete-file behavior and sampling parameters.
- [ITtsEngine.cs](../apps/dotnet/src/Core/Engine/ITtsEngine.cs) and
  [OnnxTtsEngine.cs](../apps/dotnet/src/Core/Engine/OnnxTtsEngine.cs) preserve
  Checking, setup, transcription, Generating, Finalizing and Stopping. Keep the
  effective clone transcript in metadata after automatic transcription.
- [SpeechDurationEstimate.cs](../apps/dotnet/src/Core/Engine/SpeechDurationEstimate.cs),
  [StreamingAudioPlayer.cs](../apps/dotnet/src/Core/Audio/StreamingAudioPlayer.cs),
  `MainViewModel.cs` and `MainWindow.axaml` provide estimate, queue and controls.
  Document demo limitations in Windows/Linux Help and the validation record.

Preview level protection and final gain are separate. Preview may adjust level
as chunks arrive; prevent clipping and audible gain discontinuities without
amplifying quiet audio. Reject non-finite samples. The authoritative full float
recording uses the existing one uniform gain: unchanged when peak <=1, otherwise
`0.98 / peak`. Never substitute independently normalized chunks for final audio.
Preview and final PCM equality is **not** an acceptance criterion: rolling
decoder context and the final peak can both produce differences.

## Playback, failure and commit contract

Use one persistent queued audio device, with a small startup/resume buffer
(initial target about two seconds) and bounded outstanding PCM. Backpressure
observes cancellation off device/UI threads. Starvation means Buffering until
more data or explicit completion arrives; it never means EOS. Do not append
buffering silence to the output or restart playback from sample zero at save.

Stop existing result/History audio before preview; prevent competing playback
during its session. Keep inputs disabled through initial queue drain, while
Help, Logs and Stop remain available. File generation and playback completion
are separate: release model working memory when workers finish, allow queued
audio to drain, then expose ordinary Replay/reveal. PCM must not retain tensors.

Distinguish natural EOS, explicit diagnostic-limit failure, cancellation and
inference error. .NET has no automatic frame cap; exhaustion of an explicitly
requested diagnostic cap **throws and discards** the take. Flush the tail
only for natural EOS. Any failure immediately silences/discards pending preview
and publishes no recording; users may already have heard a provisional prefix.
Do not infer successful completion merely from queue-empty or producer exit.

Stop during setup/generation cancels the whole operation and immediately clears
audio; stay Stopping until actual model work ends. Reject stale chunks by job
identity. Serialize cancellation with the final file commit: before commit,
discard temporary output; after commit, stop playback but keep the successful
file. Close follows the existing busy-close policy. Disk failure is fatal;
metadata tagging remains best-effort. A device failure disables preview and
reports it while generation continues; release its queue waits to avoid deadlock.

Only commit one valid 24 kHz mono WAV and one History entry, retaining original
text and mode-specific metadata. Use a private temporary file and atomic rename
within Outputs. Cleanup owns only documented app temporary names.

## Production follow-ups and parity gate

1. **Replace the extra full decode.** Prove contextual PCM stability on captured
   codec sequences. Spool unattenuated float chunks to disk with bounded buffers,
   validate sample ordering and track peak, then make one final pass applying
   uniform gain into the final WAV. Cover cancellation, non-finite input, disk
   failure and tail handling through both passes. Remove the final full vocoder
   pass only after quality checks pass; do not silently trade final audio quality
   for throughput. KV-cache growth remains a separate limitation.
2. **Extend the Swift dependency.** The pinned library currently provides token
   progress and final audio; clone runs synchronously. Add materialized owned PCM
   or complete codebooks for all three modes and reliable EOS/cap/cancel reporting
   (including the follow-up in issue #224). Keep MLX evaluation on the worker and
   track non-interruptible producer work until it ends. Pin the validated change
   through `apps/macos/project.yml`, regenerate Xcode output, update the lockfile.
3. **Implement macOS and Linux validation.** Wire the Swift estimator, float
   spool, output gain and AVAudioEngine/AVAudioPlayerNode queue into `TTSEngine.swift`
   and `ContentView.swift`. Run Linux audio/device checks for the .NET path.
   Share estimator fixtures and observable behavior, not runtime code. Deliver
   both native implementations or keep their parity work explicitly tracked.

## Validation and release evidence

- Headless tests: upper estimates 19.999/20/20.001, frozen routing, mixed scripts,
  punctuation/numbers, all-mode adapter plumbing, ordered offsets, partial tail,
  bounded queue, starvation/resume, late callbacks and no duplicate autoplay.
- Lifecycle tests: cancellation during setup/transcription/decode/backpressure/
  finalization/drain, device failure, disk error, cap exhaustion and non-finite
  output. Failures create no History entry; retry waits for actual worker exit.
- Gain tests: quiet audio unchanged; a late peak attenuates the entire saved
  clip uniformly. Test preview protection separately from saved-wave normalization.
- Real models: all three modes, eligible long and ineligible short requests.
  Compare preview decoding with full decoding of the same codec sequence;
  require exact sample coverage/offsets, inspect numeric differences, and listen
  for seams, reference bleed, missing tails and voice changes. Record measured
  context settings and limitations, not just a successful file write.
- Record first audible audio before talker EOS, cold/warm first-audio latency,
  inference/finalization/playback times, underruns and peak memory. The demo's
  final decode overhead is reported explicitly; no realtime guarantee is made.
- Run documented .NET build/test/self-contained publish and Windows/Linux CI;
  build macOS with XcodeGen/Xcode plus its existing standalone Swift checks.
  Actual device/audio tests are required separately from headless CI.

Land through Conventional Commit PRs with spec updates and explicit unfinished
milestones. A demonstration build does not mark §2b complete; production rollout
requires earlier first audio, acceptable measured decoder quality and overhead,
bounded audio storage, correct failures, and all-mode platform parity.
