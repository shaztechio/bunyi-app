<!--
Copyright 2026 Shazron Abdullah and Bunyi contributors

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
-->

# Voice Clone termination investigation — 11 September 2026

The reported failed run used an eight-second saved reference (101 codec frames) and a 368-character passage, producing 217 tokens of context. It ran from 19:40:16 to 19:48:56, then exhausted the old text-derived budget at roughly 110 seconds of generated audio. It never selected EOS before that cutoff. The take was discarded before vocoding or saving. Those logs did not contain the seed or EOS probabilities, so the original random trajectory cannot be replayed.

Controlled local CPU reproductions used the same saved recording/transcript and text captured from the app, without changing the input or uploading the recording:

| Sampling seed | Termination | Generated audio | Total inference |
| --- | --- | --- | --- |
| 42 | EOS at 312 frames; diagnostic cap 500 was not reached | 24.96 s | 80.1 s |
| 43 | EOS at 346 frames; no frame cap | 27.68 s | 90.1 s |

Both used the current 0.6B Base int4 export, automatic language and the configured sampling defaults. The first run had a 22.457% EOS sampling probability at termination; the second had approximately 99.97%. These are two successful reproductions, not evidence that the intermittent failure is fixed. Removing the budget did not cause either success: both completed well below the old cutoff.

The clone prompt construction, reference-code conditioning, two-frame minimum, control-token suppression with EOS exempted, repetition-penalty application and decode-cache updates were compared with the published [ONNX reference](https://huggingface.co/wavekat/Qwen3-TTS-0.6B-Base-ONNX/blob/main/generate_clone_onnx.py). No new discrepancy was identified in that inspection. This is narrower than proving all model inputs and numerical results are equivalent. The earlier distinct-token repetition-penalty correction remains in place.

At the user's request, normal app/CLI generation now waits for EOS or cancellation; neither a text-derived budget nor the export's max_new_tokens value imposes an automatic cutoff. Explicit diagnostic/test caps remain opt-in. Stop and cleanup are unchanged. A missing EOS can therefore still produce a long-running operation; this policy change is not a stopping-algorithm fix.

The loop now records EOS probability after filtering, its maximum over the run, the number of steps where EOS was eligible, and periodic frame/elapsed counts. Natural completion explicitly records EOS selection. This distinguishes a future run where EOS is effectively absent from one where sampling keeps passing over a viable EOS. It does not log input text or reference audio.

After launching the updated app, the user's Voice Clone run also ended on EOS at 292 frames (23.36 seconds), saved normally, and auto-played. Total app generation time was 92.8 seconds. This confirms normal completion in the desktop flow as well as the isolated probe; it still does not explain the earlier intermittent missing-EOS run.

Next investigation if a run stalls again: capture its EOS diagnostics and compare equivalent seeded execution against the published reference. Do not force EOS or trim a take at an estimated duration, since that can conceal a prompt/conditioning problem or cut valid speech.
