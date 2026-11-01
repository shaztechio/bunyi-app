# Short-cue generation and silence experiment

Run on Windows on 2026-11-01 (Asia/Singapore), before SRT implementation.
Voice design is research only: SRT supports Preset voice and Voice clone.

## Method

Used the existing local Bunyi CLI 1.3.3, one-shot generation, English, and
installed ONNX exports from `https://models.bunyi.app/onnx/`:
`customvoice`, `voicedesign`, and `voiceclone`. No model or app settings were
changed. Experiment configuration and outputs were isolated in
`tmp/srt-probe/`; normal model-folder lease files were used.

Generated one take per mode for each of two inputs:

- `Hello.`
- `Hello! We'll begin in just a few minutes.`

Preset speaker: `ryan`, with no style instruction. Design description:
`A calm male narrator with a warm, clear voice.` Clone used the newly generated
Preset sentence as reference, with that exact sentence as transcript. No
private voice recording or transcription service was used. All six calls
returned successful completion and PCM16 mono 24 kHz WAVs.

Analyzed unmodified output with [the measurement script](../tools/srt-silence-probe.py).
It computes RMS over non-overlapping 10 ms windows and finds the first and last
windows at or above -50 and -40 dBFS. Leading/trailing values describe the
outside low-energy regions, not proven silence or phonetic boundaries. A quiet
consonant, breath, background noise, or model artifact may affect the result.
No samples were trimmed and no speech recognition was used.

## Results

| Mode | Input | Complete duration | Leading, -50 / -40 dBFS | Trailing, -50 / -40 dBFS |
|---|---|---:|---:|---:|
| Preset | Hello | 2.00 s | 430 / 460 ms | 490 / 580 ms |
| Preset | Sentence | 3.76 s | 430 / 500 ms | 450 / 460 ms |
| Clone | Hello | 0.56 s | 150 / 180 ms | 10 / 20 ms |
| Clone | Sentence | 3.60 s | 30 / 210 ms | 50 / 70 ms |
| Design (research) | Hello | 0.80 s | 50 / 60 ms | 160 / 180 ms |
| Design (research) | Sentence | 3.44 s | 50 / 80 ms | 40 / 50 ms |

[Machine-readable results](SRT-EXPERIMENT.json) record WAV names, SHA-256 hashes,
model sources, app version, generation times, and measured boundaries. Audio
and original CLI logs remain local under `tmp/srt-probe/`; binaries are not
committed. These hashes identify measured outputs, not pinned model revisions.

## Interpretation and decision

Complete-clip placement is deterministic, but audible words may start
substantially later than the cue timestamp: the tested Preset outputs have
approximately half a second of leading low energy. Trailing low energy can
also cause conservative overlap errors after the words have ended. This is
material for tightly timed subtitles and must be explained in the SRT UI/help.

Keep the accepted v1 policy: place complete clips at timestamps and count their
complete duration. Do not infer a safe trim from these thresholds. The Clone
sentence's detected onset moves by 180 ms between thresholds, illustrating
threshold sensitivity. Users can edit timestamps or regenerate; transparent,
previewable manual trimming can be considered separately if needed. Do not
claim word-accurate synchronization.

Both short Design requests completed, but this experiment did not evaluate
design-to-clone continuity or establish that a one-word design reference is
adequate. That question no longer gates SRT because Voice design is excluded.

Limitations: one stochastic take per input/mode, one language, one preset
speaker, one synthetic clone reference, existing local CLI binary rather than
a fresh build of the branch, and Windows/ONNX only. This is an energy
measurement experiment, not a listening/content-accuracy acceptance pass. It
does not establish macOS/MLX behavior, universal silence bounds, or voice quality.
Cross-platform real-model listening remains an implementation acceptance task.

## Reproduction

Use an isolated `BUNYI_DATA_DIR` and `--config` JSON selecting an existing models
folder and the model sources above. Invoke `bunyi generate design|preset|clone`
with the inputs/settings above, `--language english --one-shot --json` and
capture stdout/stderr into `MODE-hello.log` / `MODE-sentence.log`. Run Preset's
sentence before Clone and pass its `outputPath` to Clone's `--reference`, with
the exact sentence in `--transcript`. Run trials sequentially. Native warnings
may precede the terminal JSON result; the analyzer ignores those lines.

Then run from the repository root:

```text
python tools/srt-silence-probe.py tmp/srt-probe
```

Results vary with sampling; no fixed random seed is exposed by these CLI calls.
