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

# ONNX research (M0) — can the .NET app actually run Qwen3-TTS?

**Verdict: GO for preset voice. Voice design and voice clone need their own
inference code.** Measured on Windows 11, RTX 4090, .NET 10.0.400, 2026-08-18.
CUDA with the vocoder on CPU is 3.7x faster than CPU-only and is worth shipping
as an opt-in; DirectML is not. **Linux works**, at Windows-comparable speed and
memory.

This file exists so none of it has to be rediscovered. Where a number is
measured it says so; where something is assumed it says that too.

## The gate

A throwaway console spike downloaded a real export, loaded it, and synthesised
speech. It passed:

```
[spike] rate=24000 ch=1 bits=16        <- spec §2 exactly
[spike] samples=113280 duration=4.72s
[spike] frames=59
[spike] PASS
```

The WAV header decodes as `RIFF`/`WAVE`, `fmt ` PCM, one channel, 24 000 Hz,
16-bit. **59 frames for 4.72 s is 12.5 frames/s**, confirming the 12 Hz codec
frame rate — and confirming that a real frame counter is available for §2's
live progress (`TtsSynthesisMetrics.GeneratedFrames`), so it does not have to
be inferred from elapsed audio.

## What runs today

`ElBruno.QwenTTS` **1.7.2** (MIT, `net8.0` + `net10.0`, published 2026-08-02)
drives the preset-voice pipeline. Verified by reflection over the shipped
assembly (`ElBruno.QwenTTS.Core.dll`), not from its README:

- `TtsPipeline.CreateAsync(modelDir, downloadProgress, repoId, sessionOptionsFactory, vocoderSessionOptionsFactory, variant, maxConcurrency, ct)`
- `SynthesizeToPcmAsync(...)` returns `TtsAudioResult { Samples, SampleRate, Channels, BitsPerSample, Duration, SampleCount, Metrics }`
- `SynthesizeStreamingAsync(...)` returns `IAsyncEnumerable<TextToSpeechStreamingUpdate>`
- `TtsSynthesisMetrics { GeneratedFrames, FirstAudioLatency, TotalLatency, OutputSamples, QueueLatency }`
- `OrtSessionHelper.CreateCpuOptions() / CreateDirectMlOptions() / CreateCudaOptions()`
- `QwenModelVariant { Qwen06B, Qwen17B }` — **CustomVoice only**

Three facts from that surface matter to the plan:

1. **`CreateAsync` takes a model directory**, so our own `ModelDownloader`
   (spec §3b) fills the folder and the library's downloader is never used.
2. **`SynthesizeToPcmAsync` returns raw samples**, so we write the WAV
   ourselves and own the filename and the RIFF `LIST`/`INFO` chunk (§2). We do
   not let the library write the file.
3. **Two separate session-options factories** — talker and vocoder — let us
   choose an execution provider per graph. That turns out to matter (below).

`variant` has only `Qwen06B` and `Qwen17B`, both CustomVoice. **There is no
VoiceDesign or Base variant**: this library cannot do the other two modes.

## What does not run

| Mode | Status |
|---|---|
| Preset voice | **Works.** Measured end to end. |
| Voice design | No C# implementation. Export exists with a Python reference script. |
| Voice clone | No C# implementation, and see the ICL note below. |

The clone case has a trap worth stating plainly.
`elbruno/Qwen3-TTS-12Hz-0.6B-Base-ONNX` ships a `speaker_encoder` but **no
codec/speech-tokenizer encoder**: it clones from a fixed-size speaker embedding
and takes no reference transcript. Spec §4 requires in-context learning, where
the transcript aligns the clip to its words. A speaker-embedding model would
load, run, and return a plausible voice while silently ignoring the transcript
— so the UI would present a required field that does nothing.
`wavekat/Qwen3-TTS-0.6B-Base-ONNX` ships `tokenizer_encoder.onnx` (+ `.data`,
192 MB) alongside `speaker_encoder.onnx` and is the ICL export. Spec §1 now
states this requirement.

One encouraging detail, from a stack trace: the library's internal
`LanguageModel.GenerateInternal` already takes `int[] refTokenIds` and
`long[,,] refAudioCodes`. The ICL plumbing exists inside the package; it is
simply not reachable through a public variant. Worth reading before writing
M10 from scratch.

## Verified export layouts

All three are **Apache-2.0**. Sizes from the Hugging Face API, 2026-08-18.

### `elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX` — preset voice, 5.88 GB, 33 downloadable files

```
talker_prefill.onnx (4.62 MB)  + .data (1774.45 MB)
talker_decode.onnx  (4.72 MB)  + .data (1774.45 MB)
code_predictor.onnx (440.69 MB)
vocoder.onnx        (2.71 MB)  + .data (456.26 MB)
embeddings/text_embedding.npy (1244.66 MB)
embeddings/...                (22 more .npy, ~1.56 GB in total)
embeddings/config.json        embeddings/speaker_ids.json
tokenizer/vocab.json (2.78 MB)  tokenizer/merges.txt (1.67 MB)
```

**There is no top-level `config.json`** — it is `embeddings/config.json`. That
single fact breaks the MLX `hasCompleteModel` rule outright, and is why
`DATA-FORMATS.md` now carries a separate ONNX rule.

`embeddings/speaker_ids.json` lists exactly nine speakers — `ryan`, `aiden`,
`vivian`, `serena`, `uncle_fu`, `dylan`, `eric`, `ono_anna`, `sohee` — **the
same nine** as the macOS fallback list in `ContentView.swift`.
`embeddings/config.json` carries `language_ids` for all ten of §1's languages
(plus `beijing_dialect` and `sichuan_dialect`, which §1 does not offer and
which must not be added without a spec change). Preset-voice parity is
therefore reachable exactly.

### `wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX` — voice design, 18.55 GB in total

```
(root)      config.json, generate_onnx.py, requirements.txt
int4/       8 files,  4.27 GB   <- the variant we use
fp32/       8 files, 12.70 GB   <- never downloaded
embeddings/ 23 files, 1.56 GB
tokenizer/  tokenizer.json (11.42 MB), vocab.json, merges.txt, ...
validation/ onnx_e2e.wav, pytorch_e2e.wav, sample.wav
```

### `wavekat/Qwen3-TTS-0.6B-Base-ONNX` — voice clone (ICL), 8.77 GB in total

```
(root)      config.json, generate_clone_onnx.py,
            speaker_encoder.onnx   + .data ( 35.41 MB),
            tokenizer_encoder.onnx + .data (192.31 MB)   <- the ICL half
int4/       8 files, 2.20 GB     fp32/ 8 files, 4.91 GB
embeddings/ 21 files, 1.41 GB    tokenizer/   validation/
```

Three things follow, all now in the spec:

- **A precision subfolder is part of the path.** Downloading a whole repo
  because the layout was assumed flat would pull 18.55 GB to use 4.27 GB of
  it. The per-mode file list must be variant-scoped.
- **Every `.onnx` here has an `.onnx.data` sibling**, and the `.onnx` is a few
  MB while its `.data` is hundreds. An interrupted download very often leaves
  the small half, which is why the ONNX completeness rule checks for the
  sibling.
- **`validation/pytorch_e2e.wav` and `onnx_e2e.wav` are golden outputs.** When
  M8 and M10 hand-port these pipelines, those files are how we prove the port
  is correct rather than merely plausible.

### Rejected candidates

`apps/dotnet/AGENTS.md` originally named three exports. None is used:

- `xkos/Qwen3-TTS-12Hz-1.7B-ONNX` — decomposed into 60+ `onnx__MatMul_*.onnx`
  weight files plus a `model_cache.npz`. That is a bespoke runtime's layout,
  not a normal ORT graph set.
- `sivasub987/Qwen3-TTS-0.6B-ONNX-INT8` — its own README reports `ConvInteger`
  operator failures affecting the clone pipeline, and it wants a Rust DLL for
  audio preprocessing.
- `arubeh/qwen3-tts-12hz-1.7b-base-onnx` — same `*_q.onnx` shape as the above.
  Not verified, not used.

## Measurements

RTF = wall-clock / audio duration; **lower is better, 1.0 is realtime**.

### The benchmark texts

Written out so a run on another machine is the same run. Paste them verbatim —
punctuation included, since it changes the phrasing the model produces and
therefore the frame count.

**Short** (~4.4–5.1 s of audio). Also the first example chip in the macOS app,
which is where it came from:

```
Hello! We'll begin in just a few minutes.
```

**Long** (~20–25 s of audio):

```
The harbour was still that morning, and the boats had not yet gone out. A gull settled on the rail beside me and watched the water with the patience of something that had done this every day of its life. By the time the sun cleared the headland, the tide had turned and the whole bay was moving.
```

> **The long-text rows in the table below predate this paragraph.** They were
> measured against *a* three-sentence paragraph of about the same length, which
> was never written down — this file described it and did not quote it, and the
> text is gone. Those rows stay because their relative ordering is still
> informative, but a long-text run against the paragraph above is not
> comparable to them and should be recorded as a new row rather than filed
> beside one. Short-text rows are unaffected: that text was always recorded.

### Conditions for the MLX row

Recorded because the row was taken through the shipping app rather than a
benchmark harness, so the settings are the app's:

| | |
|---|---|
| Machine | Apple M3, 16 GB |
| App | Bunyi macOS, Release build of `af19b79` |
| Mode | Preset voice |
| Model | `Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16` |
| Source | the `models.bunyi.app` mirror — byte-identical to the Hugging Face repo, checksum-verified on download |
| Speaker | Ryan |
| Language | Auto |
| Wall clock | the app's own `(N s total)` log line, which spans prepare through file written |
| Audio duration | `afinfo` on the produced WAV |
| Date | 2026-08-24 |

Sampling is stochastic, so audio length varies between runs of the same text —
3.68 s to 6.00 s across the three warm runs. That is why three were taken and
the mean reported; a single run is not a figure.

| EP | Text | Frames | Wall | RTF | Peak working set |
|---|---|---|---|---|---|
| CPU | short | 64 | 29.1 s | 5.68 | 8.73 GB |
| CPU | long | 267 | 106.7 s | 5.00 | 17.68 GB |
| DirectML | short | 55 | 37.6 s | 8.54 | 6.31 GB |
| DirectML | long | 278 | — | — | **crashed** |
| DirectML talker + CPU vocoder | long | 280 | 151.7 s | 6.77 | 15.88 GB |
| CUDA | short / long | — | — | — | **crashed** |
| **CUDA talker + CPU vocoder** | short | 58 | 8.6 s | **1.86** | 5.32 GB |
| **CUDA talker + CPU vocoder** | long | 210 | 22.8 s | **1.36** | **11.87 GB** |
| Linux CPU (WSL2) | short | 45 | 29.9 s | 8.30 | 8.35 GB |
| Linux CPU (WSL2) | long | 224 | 99.4 s | **5.54** | 16.03 GB |
| Linux CPU, models on `/mnt/c` | short | 59 | 52.7 s | 11.16 | 8.66 GB |
| **MLX, Apple M3 16 GB** *(different machine)* | short | — | 3.9 s | **1.16** | not comparable — see below |

Frame counts vary between runs of the same text because sampling is stochastic;
RTF is the comparable figure, and the gaps here are far larger than that noise.

**The MLX row is a different machine** — an M3 Mac, not the Windows box the
rest of the table was taken on — and is included because it is the number the
Swift-versus-ONNX argument in the root `AGENTS.md` turns on. Its RTF is the
mean of three warm runs (0.96, 1.53, 1.01); a cold run including the 2.6 s
model load is 2.14. Its memory is left out rather than filled in: MLX reports
2.45 GB resident with 3.3–5.0 GB of buffer cache released per run, which is not
the same measurement as a Windows peak working set, and putting a number in
that column would invite a comparison it does not support.

**To re-run this on Windows**, use the short text above, the CPU execution
provider, and record wall clock and audio duration the same way. The point of
interest is whether the roughly fivefold gap survives being taken on one
machine.

**CUDA, with the vocoder on CPU, is the fastest configuration by a wide margin
— 3.7x faster than CPU on long text and 5.8 GB lighter.** It is the difference
between 107 s and 23 s for 17–22 s of speech.

Model download: 5.88 GB in 210 s (~28 MB/s). Subsequent runs load in **1.8 s**
from disk with no network — offline reuse works.

### The vocoder graph only works on the CPU execution provider

One defect explains every GPU failure below. The vocoder dies on the **same
node**, `node_pad_1`, under both GPU providers:

```
DirectML: Pad node 'node_pad_1' ... 80070057 The parameter is incorrect.
CUDA:     Pad node 'node_pad_1' ... Tensor shape.Size() must be >= 0
```

CUDA's message is the informative one: the pad is computing a **negative**
output dimension. DirectML tolerates that for short input and fails past some
length; CUDA rejects it always. In every case the talker had already produced
all of its frames correctly — 278 of them under DirectML — so this is the
vocoder alone, and it looks like a shape bug in the exported graph that happens
to be harmless under the CPU kernel's implementation.

**So the vocoder runs on CPU, always.** That is not a workaround to remove
later; it is the configuration, and it is why `TtsPipeline` taking a separate
`vocoderSessionOptionsFactory` matters so much. Worth reporting upstream.

### DirectML does not earn its place; CUDA does

With the vocoder on CPU either way, the two GPU providers separate sharply:

- **DirectML is slower than plain CPU** (RTF 6.77 against 5.00), and slower
  even on short input where it runs end to end (8.54 against 5.68). There is no
  configuration in which it wins.
- **CUDA is 3.7x faster than CPU** and uses 5.8 GB less memory.

**The CUDA audio was checked by ear against the CPU audio and sounds as good.**
That mattered enough to be worth a person listening: every other figure here
says the two are equivalent, but nothing in an RTF or a sample count would
catch a provider that quietly degraded quality, and quality is the whole point
of the product. Do not read the per-clip RMS as a quality signal — the short
CPU and CUDA clips measured RMS 3187 and 1380, which is sampling variation
between two renditions of the same sentence, not one of them being wrong.
Decoding is stochastic, so no two runs match even on one provider.

So the speedup is free, and CUDA is worth offering rather than merely
possible.

The likely reason DirectML loses is shape: decode is one small `Run` per 12 Hz
frame against a KV cache that grows every step, which is close to the worst
case for a provider with per-dispatch overhead and a preference for static
shapes. CUDA handles that pattern far better.

**Consequences.** DirectML should not ship as the Windows accelerator. That
removes the only reason to pin ONNX Runtime at 1.24.4 — the DirectML package
caps there while base ORT is already at 1.29.0 — so the .NET app should track
current ORT instead. `Microsoft.ML.OnnxRuntime` **1.24.2** is what
`ElBruno.QwenTTS` resolves to transitively; a deliberate pin is still wanted,
but it is now a free choice rather than a hostage to DirectML.

**CUDA works, and is worth shipping as an opt-in flavour.** It runs on this
machine's CUDA 13.3 toolkit with `Microsoft.ML.OnnxRuntime.Gpu` 1.29.0 — the
CUDA-12-only constraint belonged to ORT 1.24.x, which we are no longer pinned
to now that DirectML is out. Two things it needs beyond the driver:

- the CUDA runtime DLLs, which on CUDA 13 live in `bin/x64` (`cudart64_13.dll`,
  `cublas64_13.dll`) rather than `bin` as on 12.x, and must be on `PATH`;
- **cuDNN 9** (`cudnn64_9.dll`), which the CUDA toolkit does *not* install.
  There is no winget package; it is a manual NVIDIA download, or the
  `nvidia-cudnn-cu13` PyPI wheel, which is how it was obtained here.

Ironically cuDNN is not needed for the configuration we would actually ship:
it is required by the vocoder's `Conv`, and the vocoder runs on CPU regardless.
A CUDA build that never puts the vocoder on the GPU needs only the CUDA runtime.

This does not become the default — it cannot be asked of the audience in §'s
terms — but as a documented opt-in for a machine with an NVIDIA GPU it turns an
RTF of 5 into an RTF of 1.4, which is the difference between a feature people
use and one they abandon.

### Memory is the real constraint, not speed

**17.68 GB peak for 22 s of audio, on the smallest model.** Memory scales with
output length, because the KV cache grows per frame. The static part is
explained by the export itself: prefill and decode are separate ONNX sessions
holding the same ~1.77 GB of weights, plus a 1.24 GB embedding table, plus the
vocoder and code predictor.

That has three consequences:

1. §11's memory check is not a formality. It stays a warning and never a
   blocker — a prediction should not refuse a run that would have worked — but
   it will fire often, so its wording matters.
2. §2's "release the runtime's working memory once the output is written" is
   load-bearing here, not housekeeping.
3. A 16 GB machine is marginal for long text even at 0.6B. The 1.7B design
   model at `int4` has now been measured — see below — and costs less, not
   more.

### The 1.7B `int4` design model costs *less* than the 0.6B we already ship

Measured 2026-08-18, on the same machine and by the same method as the table
above: every session the reference pipeline creates, plus every embedding it
loads, held at once and nothing released. That is the floor a generation starts
from, not its peak — there is no C# design pipeline to run yet, which is M8.

| | 0.6B CustomVoice (shipping) | 1.7B VoiceDesign `int4` |
|---|---|---|
| Download | 5.88 GB (4.46 GB required) | **5.85 GB** |
| 4 ONNX sessions, resident | 4.56 GB | **2.25 GB** |
| Embeddings loaded as arrays | none — they are inside the graphs | 1.56 GB |
| **Static floor** | **4.56 GB** | **3.82 GB** |

**The bigger model is 0.74 GB cheaper.** `int4` more than pays for 2.8x the
parameters: the design export's four graphs carry 4.26 GB of external data but
sit at 2.25 GB resident, because ONNX external data is memory-mapped and only
what is touched becomes resident. The 0.6B export is not quantised, so its
weights cost what they weigh.

The download is the same size either way — 5.85 GB against 5.88 GB — so design
mode is no larger a first-run ask than the mode already shipping. Only the
`int4/` subfolder is fetched; `fp32/` is 12.70 GB and is never touched.

### Long text will cost the same in both modes

The static floor is only half the question, because §11's real problem is that
memory grows with output length. That growth is the KV cache, and its size per
token is set by `layers x kv_heads x head_dim` — **not** by hidden size:

| | 0.6B CustomVoice | 1.7B VoiceDesign |
|---|---|---|
| `num_hidden_layers` | 28 | 28 |
| `num_key_value_heads` | 8 | 8 |
| `head_dim` | 128 | 128 |
| `hidden_size` | 1024 | **2048** |

The three terms that drive the cache are identical; only `hidden_size` doubles,
and that lands in the weights, which is a one-off already counted above. So the
per-frame growth measured for preset voice should apply unchanged to design,
and the difference between the two modes is the static delta:

```
peak_design(n frames)  ~=  peak_preset(n frames) - 0.74 GB
```

Against the peaks measured earlier, that puts design mode at roughly **8.0 GB
for a short clip and 17 GB for 22 s of audio** — a little better than the mode
that ships today.

**So the answer to "is design mode usable on 16 GB" is: exactly as usable as
preset voice already is, which is to say fine for short text and marginal for
long.** That was the open question blocking M8; it is not a reason to change
scope. The risk that design mode would need a machine class of its own is
closed.

Two caveats worth keeping:

- **These are floors, not peaks.** Memory-mapped weights become resident as
  inference touches them, so the design floor will rise during a real run. The
  comparison holds because both exports were measured the same way, and both
  are mapped; the absolute figures are not generation peaks.
- **The growth term is assumed, not measured, for 1.7B.** It rests on the
  configs matching, which they do exactly. M8 should re-measure a real
  generation and correct this if it does not hold.

### Where the growth actually comes from, and an experiment for M8

Per-frame growth for the 0.6B works out at ~44 MB/frame on Windows (8.73 GB at
64 frames, 17.68 GB at 267) and ~43 MB/frame on Linux — reproducible, and far
too large to be the KV cache itself, which is 0.229 MB/token at this geometry.

The likely explanation is the arena. ONNX has no in-place KV cache: every
decode step takes the whole cache in and hands a new one back, one token
longer, so **every step allocates a block of a size no previous step used**. An
arena that grows rather than reuses would then hold the sum of every step's
allocation, which is quadratic in frames. That sum comes to 0.48 GB at 64
frames and 8.21 GB at 267 — the right shape, and the right order of magnitude,
though it leaves a residual of a couple of GB that varies by run, so it is a
hypothesis rather than a finding.

**This was tested, and it is wrong** — see "The arena is real, and far too
small to be the answer" below. The mechanism exists and the magnitude does not:
disabling the arena removes the decode loop's growth entirely, but that growth
is 0.51 GB at 267 frames, not the ~9 GB that separates the short and long
generations.

## Two experiments before M8

### The design graphs are the same graphs

Every input and output of `wavekat/…1.7B-VoiceDesign-ONNX` `int4/` matches
`elbruno/…0.6B-CustomVoice-ONNX`, name for name, in the same order, with the
same element types and the same symbolic dimensions. **The only difference is
`hidden_size`, 1024 against 2048.**

| Graph | Both exports |
|---|---|
| `talker_prefill` | in `inputs_embeds[b,seq,H]`, `attention_mask[b,seq]`, `position_ids[3,b,seq]` → `logits`, `hidden_states`, and 56 per-layer `present_key_N` / `present_value_N` |
| `talker_decode` | in `inputs_embeds[b,1,H]`, `attention_mask[b,total]`, `position_ids[3,b,1]`, `past_keys[28,b,8,past,128]`, `past_values` → `logits[1,1,3072]`, `hidden_states[1,1,H]`, `present_keys`, `present_values` |
| `code_predictor` | in `inputs_embeds[b,seq,H]`, `generation_steps`, `past_keys[5,b,8,past,128]`, `past_values` → `logits[b,seq,2048]`, `present_keys`, `present_values` |
| `vocoder` | in `codes[b,16,frames]` → `waveform[1,1,…]` |

Two consequences for M8:

- **The inference driver is shared.** Prefill, the decode loop, the 16-way code
  predictor and the vocoder are mechanically identical between the modes; only
  the width of the hidden state changes, and it is already a symbolic dimension
  in every graph. Whatever drives one drives the other, if it reads the width
  from config instead of assuming it.
- **What differs is input preparation, not inference.** Preset voice picks a
  speaker from `speaker_ids.json`; design has no speaker list at all, and
  instead ships `text_embedding.npy` (1.24 GB) with `text_projection_fc1/fc2`
  weights. The description is embedded and projected into the same
  `inputs_embeds` the graph already takes. That is the part M8 has to write.

One structural wrinkle both exports share: **prefill returns the cache as 56
separate per-layer tensors, and decode wants it as two stacked ones**
(`[28,…]`). A pipeline has to concatenate 28 pairs between the two calls. It is
the same shape of work in both modes, and easy to get subtly wrong — the layer
order is the obvious trap.

### The arena is real, and far too small to be the answer

The hypothesis recorded earlier: ONNX has no in-place KV cache, so every decode
step allocates a size no previous step used, and an arena that grows rather
than reuses would hold the sum of all of them.

Tested directly by driving the real `talker_decode` graph with zeroed inputs of
the correct shapes, carrying the cache forward as a real loop does, with the
CPU arena on and off:

| Frames | Arena | Peak | Growth over the loop | Wall |
|---|---|---|---|---|
| 64 | on | 1.97 GB | 0.12 GB | 5.6 s |
| 64 | off | 1.97 GB | −0.01 GB | 5.7 s |
| 267 | on | 2.36 GB | **0.51 GB** | 30.9 s |
| 267 | off | 2.36 GB | **0.01 GB** | 33.8 s |

**The mechanism is confirmed and the magnitude refutes the hypothesis.**
Turning the arena off removes essentially all of the loop's growth, at about
9% more wall-clock — but it is 0.51 GB at 267 frames, not the ~9 GB that
separates the measured short and long generations. The decode loop is, by
itself, well behaved.

### The vocoder grows with the clip, and is also too small

The next suspect: the vocoder is one `Run` over the **whole** sequence, and it
upsamples to 24 kHz, so its activations scale with output length rather than
with the model.

| Frames | Audio | Delta | Wall |
|---|---|---|---|
| 32 | 2.6 s | 0.23 GB | 0.5 s |
| 64 | 5.1 s | 0.11 GB | 0.9 s |
| 128 | 10.2 s | 0.37 GB | 2.1 s |
| 267 | 21.4 s | 0.64 GB | 4.5 s |

Peak 1.76 GB at 267 frames, against 0.9 GB of vocoder weights — so roughly
0.8 GB of activations for 21 s of audio, growing with length as expected. Real,
worth chunking, and still not the answer.

### So the 17.68 GB is not yet explained

Decode contributes ~2.4 GB including its session, the vocoder ~1.8 GB including
its own, and the four sessions together were measured at 4.56 GB static. The
production peak for the same 267 frames was **17.68 GB**. The two components
suspected here account for about a gigabyte of growth between them; the rest is
somewhere in how the shipping pipeline holds what it produces, and attributing
it means profiling that pipeline rather than reasoning about it.

That is a better position than it sounds, because **M8 writes our own
pipeline.** Three levers are now known to exist and to be ours to set:

1. `EnableCpuMemArena = false` on the decode session — 0.5 GB at 267 frames for
   ~9% wall-clock. Worth taking on a machine that is short of memory, which is
   the only machine it matters on.
2. **Chunk the vocoder.** It takes `codes[1,16,frames]`; nothing forces one
   call for the whole clip, and its activations are the part that scales.
3. **Release prefill before decoding.** Prefill and decode are separate sessions
   over the same weights and prefill is finished with after one call, which is
   ~1.8 GB held for the entire generation.

None of these should be built blind. The first thing M8's pipeline needs is the
same measurement against itself, where every allocation is attributable.

### The port is correct, and it cannot match the reference exactly

Voice design runs. Measured against the export's own `generate_onnx.py`, both
driven **greedily** — `top_k=1` leaves the softmax with all its mass on one
token, so nothing is sampled and both sides are reproducible. That makes the
**codes** comparable rather than the audio: sixteen integers a frame, against a
waveform that could be approximately right for the wrong reasons.

| | Reference | Ours |
|---|---|---|
| Frames | 12 | **12** |
| Audio samples | 23,040 | **23,040** |
| Prefill positions | 22 | **22** |
| Frame 0, codes 0-6 | — | **identical** |

The frame count is the model's own decision — generation ends when it emits its
stop token, not when a cap is hit — so matching it means the run took the same
shape.

**Then code 7 differs, and the difference is accounted for.** Instrumenting the
reference to record how close each of its fifteen code-predictor decisions was:

```
group  chosen  runner-up   margin
    5    1186        301    0.161
    6    1789       1241    0.024   <- we chose 1241
    7    1146       1562    0.195
```

The one group where we differ is the **closest decision of the whole run**, and
our choice is exactly its runner-up. Every group we agreed on was decided by
0.16 or more.

**The mechanism, measured rather than assumed.** Our prefill embeddings differ
from the reference's by **2 to 7 x 10^-9 per value** — about one ULP of float32.
A dot product over 2048 terms does not give the same last bits under two
different summation orders, and both are correct. Twenty-eight layers of
attention amplify that until it can cross a margin of two parts in a thousand.

So **bit-exact agreement is not achievable**, and chasing it would mean
reproducing NumPy's summation order and pinning us to it forever. Our own runs
are byte-identical to each other, so what is being compared is a stable
difference rather than noise.

Correctness is therefore asserted as: the same frame count, the same audio
length, agreement up to the first close decision, and — the part that separates
*unlucky* from *wrong* — that the **first** difference falls on the closest
decision of the run and picks its runner-up. The rule applies only to the first:
after one code differs, the predictor is fed a different embedding and makes its
later choices from a state the reference never had.

Anyone re-checking this should expect the same shape of result rather than an
exact match, and should treat a divergence at a **wide** margin as the signal
that something is genuinely wrong.

## Linux

Measured on WSL2, Ubuntu 24.04.2, 46 GB RAM, 32 cores — **not bare metal**, so
treat the timings as indicative rather than definitive.

**It works.** 24 kHz mono 16-bit, and the audio is real: the short clip is
4.72 s at RMS 3177 / peak 17183, against 4.72 s and RMS 3187 from the same text
on Windows. Two platforms, effectively the same output.

**`ElBruno.QwenTTS` does not fault through NAudio.** This was the open question
that could have cost us the dependency. `NAudio.Wasapi.dll` and
`NAudio.WinMM.dll` are copied to the Linux build output and the app runs
regardless: nothing on the inference path calls into them. They are inert
baggage, not a portability problem — which is the answer we needed, though it
stays worth watching, since it is a property of the code paths we happen to
call rather than a guarantee.

Speed and memory are Windows-comparable: **RTF 5.54 against 5.00**, and 16.03 GB
against 17.68 GB, on the long text. The ONNX Runtime package ships `linux-x64`
(and `linux-arm64`) natives, so nothing extra is required.

### A models folder on a slow volume slows *generation*, not just loading

The first Linux run read the model from `/mnt/c` — the Windows filesystem over
9p — and was **twice as slow at inference**, not merely slower to start:

| Models on | Pipeline ready | RTF (short) |
|---|---|---|
| `/mnt/c` (9p) | 6.8 s | 11.16 |
| native ext4 | 2.4 s | 8.30 |

That is not a WSL curiosity, and it is the reason this is worth writing down.
ONNX external-data weights are **memory-mapped**, so pages are faulted in
throughout the run rather than read once at load. §3d lets the user point the
models folder at "any folder (external drive, etc.)" — and on a USB disk, a
network share, or a spinning drive, that choice will make every generation
slower, with nothing on screen explaining why. Worth a warning next to the
folder picker, and worth a thought in Doctor.

## Dependency versions, verified on nuget.org 2026-08-18

| Package | Latest | Note |
|---|---|---|
| `ElBruno.QwenTTS` | 1.7.2 | Restores clean on `net10.0`. Pulls ORT **1.24.2**, `Microsoft.ML.Tokenizers` 2.0.0, `Microsoft.Extensions.AI.Abstractions` 10.7.0, `ElBruno.HuggingFace.Downloader` 0.5.0, **NAudio 2.2.1** |
| `Microsoft.ML.OnnxRuntime` | 1.29.0 | |
| `Microsoft.ML.OnnxRuntime.DirectML` | **1.24.4** | Caps here. No longer a constraint — see above |
| `Avalonia` | 12.1.1 | |
| `Whisper.net` | 1.9.1 | `Whisper.net.Runtime` is needed as well |
| `SoundFlow` | 1.4.1 | Linux natives **unverified** |
| `Microsoft.ML.Tokenizers` | 3.0.0-preview | 2.0.0 is the stable line, and what QwenTTS uses |
| `CommunityToolkit.Mvvm` | 8.4.2 | |

**NAudio arrives transitively**, including `NAudio.Wasapi` and `NAudio.WinMM`,
which are Windows-only. It restores on any platform; whether any code path we
reach calls into it on Linux is the open question below. `apps/dotnet/AGENTS.md`
bans NAudio as a *chosen* dependency; this one is inherited.

## The preset-voice pipeline refuses style instructions the model supports

Found while wiring the engine (M4). The library says so at generation time:

```
Warning: Instruction text ignored - Qwen06B model does not support
instruction control. Use 1.7B for style instructions.
```

**That message is right, and this file said for a while that it was wrong.**

The reasoning was: Qwen's own model card for
`Qwen3-TTS-12Hz-0.6B-CustomVoice` says the opposite —

> allows for fine-grained style control over target voices via natural language
> instructions

— with an `instruct=` argument in its usage example, while the restriction is a
hardcoded per-variant flag in `ElBruno.QwenTTS`:

```
Qwen06B    SupportsInstruct=False   repo=elbruno/...0.6B-CustomVoice-ONNX
Qwen17B    SupportsInstruct=True    repo=elbruno/...1.7B-CustomVoice-ONNX
```

A model card and a flag disagreeing looked like the flag being wrong. It was
not. The library's author closed
[#64](https://github.com/elbruno/ElBruno.QwenTTS/issues/64) with the answer:

> Authoritative upstream 0.6B inference discards instruction conditioning, so
> `SupportsInstruct=false` is correct despite the model-card wording.

**Then macOS was checked, and the picture changed again.** This section has now
been wrong in both directions, so what follows is the evidence rather than a
conclusion.

`AtomGradient/swift-qwen3-tts`, which the macOS app ships, **does** feed the
instruction to the 0.6B CustomVoice checkpoint. `Qwen3+Streaming.swift`
dispatches `custom_voice` to `generateCustomVoice(… instruct: instruct …)`, and
`Qwen3.swift` builds it into the sequence:

```swift
// 7. Instruct embedding (VoiceDesign/CustomVoice mode)
let instructText = "<|im_start|>user\n\(instruct)<|im_end|>\n"
instructEmbed = talker.textProjection(talker.embedText(instructIds))
…
inputEmbeds = MLX.concatenated([instructEmbed, roleEmbed, combinedEmbed], axis: 1)
```

That is the same construction wavekat's reference script uses, and the same one
`PrefillBuilder` here does for voice design. The package treats it as
deliberate: the `base` path passes `instruct: nil` with the comment "Base model
doesn't support instruct", while `custom_voice` passes it through.

So three implementations disagree about one checkpoint:

| | Feeds the instruction to 0.6B CustomVoice? |
|---|---|
| Qwen's model card | says yes |
| `swift-qwen3-tts` (macOS ships it) | **yes**, shown above |
| wavekat's ONNX reference construction | yes, for its own export |
| `ElBruno.QwenTTS` (this app uses it) | **no**, by a per-variant flag |

**This is a parity break rather than a spec question.** §1's style instruction
reaches the model on macOS and does not here, and the difference is the
dependency rather than the checkpoint. Whether the audio audibly changes is a
separate, unmeasured question — but "the app quietly drops the input on one
platform" does not need that answer to be a problem.

**The way out is already built.** Our own pipeline makes this exact prefill and
is validated against the reference (below), and the graphs are identical between
the two exports apart from hidden width. Driving preset voice through it would
close the gap, remove the `ElBruno.QwenTTS` dependency, and put both modes on
one driver. That is a real change and belongs in its own milestone, not in a
footnote here.

**What was done.** The engine refuses to record a style that was not applied,
and says why. That is a safety net whatever the reason, and it stays: an input
the UI presents as meaningful that changes nothing is the trap §1 refuses for
clone mode, and a file that claims a delivery it never had misleads anyone
trying to reproduce it. `ISpeechSynthesizer.SupportsInstruct` carries the
capability, and is documented as a property of the implementation rather than of
the model.

**What is not being done.** Switching the default to the 1.7B export — roughly
10 GB against 5.88 GB — to work around a boolean in a dependency would be a
poor trade for the audience this app is aimed at, and it would not fix the
smaller model.

**Reported upstream and answered:**
[elbruno/ElBruno.QwenTTS#64](https://github.com/elbruno/ElBruno.QwenTTS/issues/64),
closed as correct behaviour. Nothing will change there, so nothing changes here
either — but the seam earns its keep anyway. `SupportsInstruct` is asked of the
synthesizer rather than assumed, and voice design answers **true** through the
same seam, because there the description is the whole mechanism rather than a
hint the model may drop.

**What the export settles.** This was written as an open question — whether the
ONNX export lacks a conditioning path, or only the library's flag is wrong — and
the graph comparison above answers it. `talker_prefill` in the CustomVoice
export takes **`inputs_embeds[b,seq,H]`**: a sequence of embeddings assembled by
the caller, matching the design export name for name.

A graph that consumes `inputs_embeds` cannot refuse an instruction. It has no
notion of what the sequence means; whoever builds it decides what is in it.
There is no conditioning path in the export that could be absent, so
`SupportsInstruct=false` is not a statement about the export. It describes the
library's own prompt builder, and it is a policy rather than a capability.

**What is still unmeasured**, and it is about the weights rather than the graph:
whether an instruction prefix changes what the 0.6B checkpoint produces. Nobody
here has compared audio from it with and without one. macOS feeds it, so the
field is live there, but "reaches the model" and "changes the delivery" are
different claims and only the first is established — on either platform.

**How to measure it without listening.** Decoding is stochastic, so two runs of
the same text differ regardless, and no amount of listening separates "the
instruction did something" from "the sampler did something". Compare
**first-step logits** from `talker_prefill` instead, with and without the
instruction embeddings prepended: same graph, same weights, one variable, no
sampling. Identical distributions mean the weights ignore it. Only if they
diverge is it worth fixing a sampler seed and generating a pair to listen to.

One thing that experiment must establish before it can start: the design export
ships `embeddings/*.npy` to build a sequence from, and the CustomVoice export
does not — its embeddings are inside the graphs. Where instruction text gets
embedded for that export is not established here, and assuming it is available
is how this spike would fail quietly.

Tracked as [#104](https://github.com/shaztechio/bunyi-app/issues/104) rather
than left in this paragraph.

**How it closes regardless.** M8 and M10 already require writing our own
inference pipeline, since this library covers CustomVoice only. Prompt
construction becomes ours at that point, and an instruction is text conditioning
prepended to the sequence — the same machinery voice design needs. Preset-voice
`instruct` therefore arrives as a side effect of work already planned, rather
than as a project of its own.

## Open questions

Each of these is also a tracked issue. They spent a long time here where
nothing surfaces them, which is how question 2 went on naming a blocker that
had already shipped, and how question 1 went on asking about an audio backend
that does not exist.

1. **Bare-metal Linux**, and **SoundFlow's `linux-x64` natives**. Mostly
   answered: the app builds, ships and runs on Ubuntu 24.04, the runtime
   library set was measured there from `/proc/<pid>/maps` rather than guessed,
   and playback is confirmed by ear in all three modes.

   This question used to say "the three audio backends miniaudio may pick
   (ALSA, PulseAudio, PipeWire)", and **there is no PipeWire backend** —
   `MiniAudioBackend` is `Null, Wasapi, DirectSound, WinMm, CoreAudio, Sndio,
   Audio4, Oss, PulseAudio, Alsa, Jack, AAudio, OpenSl, WebAudio, Custom`. A
   PipeWire desktop is reached through `pipewire-pulse` and selected as
   **PulseAudio**, so it can never be the thing that is picked and the Linux
   candidates are two, not three.

   What is still open is whether the *other* one works. The app now logs
   `Audio backend: <chosen> (available: …)` the first time it plays
   anything, so the answer is a line in the log rather than an inference from
   the desktop — which was the reason this went unanswered: the two are not
   the same, and nothing recorded the one that mattered.
   Tracked as [#121](https://github.com/shaztechio/bunyi-app/issues/121).
2. **1.7B `int4` speed.** Memory is answered above: the design export's floor
   is 0.74 GB *below* the shipping model's, and its KV geometry is identical,
   so long text costs the same in both modes. Speed is still unmeasured — but
   no longer blocked: this said "cannot be until M8 can run the pipeline", and
   M8 shipped, as question 3 below says four lines further down. Tracked as
   [#122](https://github.com/shaztechio/bunyi-app/issues/122).
3. ~~**Whether wavekat's graphs can be driven through `TtsPipeline`**~~ —
   answered above, and by M8 and M10 shipping. The I/O matches name for name;
   only `hidden_size` differs. The same comparison settles what the
   style-instruction section used to leave open.
4. **Whether the vocoder's `node_pad_1` can be fixed** in the export, or worked
   around by rebuilding that graph. It is the only thing keeping the vocoder on
   CPU, and it affects every GPU provider.
   Tracked as [#123](https://github.com/shaztechio/bunyi-app/issues/123).
