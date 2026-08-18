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

Short text = "Hello! We'll begin in just a few minutes." (~4.4–5.1 s of audio).
Long text = a three-sentence paragraph (~22 s of audio, 267–280 frames).
RTF = wall-clock / audio duration; **lower is better, 1.0 is realtime**.

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

Frame counts vary between runs of the same text because sampling is stochastic;
RTF is the comparable figure, and the gaps here are far larger than that noise.

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
   model at `int4` needs measuring before any promise is made about it.

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

## Open questions

1. **Bare-metal Linux**, and **SoundFlow's `linux-x64` natives**. Inference is
   proven on WSL2 and NAudio is not a problem; the playback library is still
   untested on either, and WSL2 is not a substitute for a real distro on the
   audio path.
2. **1.7B `int4` memory and speed**, for both wavekat exports. If design mode
   cannot run in a reasonable footprint, that is a scope decision, not a bug.
3. **Whether wavekat's graphs can be driven through `TtsPipeline`** rather than
   hand-ported. Their file convention matches; the graph I/O has not been
   compared. If they match, M8 shrinks a great deal.
4. **Whether the vocoder's `node_pad_1` can be fixed** in the export, or worked
   around by rebuilding that graph. It is the only thing keeping the vocoder on
   CPU, and it affects every GPU provider.
