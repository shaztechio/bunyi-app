# Speech streaming — intent brief

Started 2026-09-11; updated 2026-09-12 after authorization to build a Windows demo.
The executable milestones and limitations are in [STREAMING-PLAN.md](../STREAMING-PLAN.md).

- Hear the beginning of long generations while the rest is generated, allowing
  users to judge delivery and stop an unwanted take earlier.
- Scope is Preset Voice, Voice Design and Voice Clone on both native apps.
  A Windows demo is the first milestone; it does not establish shipping parity.
- Choose streaming once at Generate when the unrounded upper output-duration
  estimate is strictly greater than 20 seconds. Preserve ordinary generation
  and final autoplay at or below 20 seconds. Processing time is not the trigger.
- Preserve one continuous inference run, full target text, mode conditioning,
  local model family, one model job, original metadata and cancellation guarantees.
  Independent sentence generations do not satisfy this feature.
- Demo: rolling-context PCM preview during inference, then the existing full
  decoder over the same codec sequence for one authoritative WAV. Extra final
  decoder work and full-clip memory are accepted for this intermediate milestone.
- Production: validated contextual decoding and bounded float disk spooling,
  followed by one whole-recording gain pass. Final gain may differ from preview;
  preview/final PCM equality is not required. Do not normalize final chunks
  independently or amplify quiet audio.
- A cap, cancellation, invalid samples or inference failure stops preview and
  discards the take. Earlier preview may have been heard, but no failed take is
  saved. Current .NET cap behavior supersedes the original save-with-warning audit.
- Success requires early actual audio, ordered coverage with no duplicated or
  missing boundary samples, no clone reference leakage, one final recording,
  no second autoplay, and consistent threshold semantics across platforms.
- Swift playable chunk events, reliable termination reporting, model quality,
  language calibration and hardware validation remain prerequisites to completion.
  Streaming does not promise realtime throughput or reduced model KV memory.
- The earlier 30-second warning proposal stays separate; streaming itself adds
  no confirmation dialog at 20 seconds.
