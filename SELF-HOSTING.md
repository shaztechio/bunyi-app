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

# Hosting the models yourself

Bunyi downloads each mode's model from Hugging Face by default. You can point
it at a server you control instead — useful when the Hub is slow, when you are
distributing to a team, or when machines need to fetch the models repeatedly.

Behaviour is specified in [`spec/FEATURES.md`](spec/FEATURES.md) §3c; this is
the practical version.

## Try this first

Self-hosting is real infrastructure. If the goal is only "download faster
once", there is a cheaper answer:

```sh
pip install -U "huggingface_hub[hf_transfer]"
HF_HUB_ENABLE_HF_TRANSFER=1 hf download <repo> --local-dir <models-folder>/models/<repo>
```

`hf_transfer` parallelises chunks and is usually several times faster than the
default client. **Settings → Storage** already shows this command per mode with
your models folder filled in. Pre-fetch there and the app finds the files on
first Generate, with no network access at all.

Self-hosting earns its keep when you are serving other people or many
machines — not for a one-off download.

## Not on bunyi.app

The obvious idea does not work. `bunyi.app` is GitHub Pages, which caps
individual files at **100 MB** (a hard Git limit), sites at about **1 GB**, and
bandwidth at roughly **100 GB/month**. The models are ~1.4 GB, ~3.4 GB and
~3.4 GB, and individual `.safetensors` files are far over 100 MB.

**GitHub Releases does not work either**, for a subtler reason: release assets
are a flat namespace, and the model needs nested paths such as
`speech_tokenizer/model.safetensors`. There is no way to express a directory in
an asset name.

## What the app requires

From `TTSEngine.downloadFromBaseURL` and `fileList`:

| Requirement | Detail |
|---|---|
| **HTTPS** | Plain `http://` is blocked by App Transport Security. Allowing it needs an `NSAppTransportSecurity` exception in `project.yml` and a rebuild. |
| **`<base>/manifest.txt`** | Newline-separated relative paths. Blank lines and lines starting with `#` are ignored. If it is missing or empty, the app falls back to a built-in Qwen3-TTS file list. |
| **`<base>/<path>` for each entry** | Must return **200**. Directory structure is preserved on disk, so nested paths must be served as nested paths. |
| **Required files** | `config.json` and `model.safetensors` — a non-200 on either fails the download. |
| **Everything else** | Best-effort. A 404 is logged and skipped, because single-shard repos have no `.index.json`, and a missing `tokenizer.json` is backfilled automatically from a known fallback. |

No CORS headers are needed — this is a native app, not a browser.

Downloaded files land in `models/self-hosted/<slug>` inside your models folder
and are reused offline from then on, exactly like Hub downloads.

## Step by step (Cloudflare R2)

R2 because egress is free, which is the entire point of moving off the Hub.
Any static host works — see [Other hosts](#other-hosts).

### 1. Fetch the model once

```sh
pip install -U "huggingface_hub[hf_transfer]"
HF_HUB_ENABLE_HF_TRANSFER=1 hf download \
  mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16 \
  --local-dir ~/bunyi-models/customvoice
```

### 2. Generate the manifest

From inside the model folder:

```sh
cd ~/bunyi-models/customvoice
find . -type f ! -name manifest.txt | sed 's|^\./||' > manifest.txt
```

Check it lists nested paths, not just top-level files:

```sh
grep speech_tokenizer manifest.txt
```

### 3. Upload, preserving directory structure

```sh
# rclone config → new remote → Cloudflare R2, then:
rclone copy ~/bunyi-models/customvoice r2:bunyi-models/customvoice --progress
```

### 4. Serve it over HTTPS

In the R2 dashboard, attach a custom domain — `models.bunyi.app`, say — and add
the CNAME it gives you at your DNS provider, alongside the records already
pointing `bunyi.app` at GitHub Pages. A public `r2.dev` URL also works but is
rate-limited and not intended for production traffic.

### 5. Point Bunyi at it

**Settings → Models**, in the field for that mode:

```
https://models.bunyi.app/customvoice
```

The scheme is what decides: anything starting `http://` or `https://` is
treated as a base URL, anything else as a Hugging Face repo ID. Clearing the
field restores the built-in default.

### 6. Verify before trusting it

```sh
curl -sI https://models.bunyi.app/customvoice/manifest.txt | head -1
curl -sI https://models.bunyi.app/customvoice/config.json   | head -1
```

Both must be `200`. Then press Generate and watch **Window → Logs** (⌘L): it
names every file as it downloads, and says exactly which one failed and with
what status code if the layout is wrong.

Repeat for each mode you want to self-host, each with its own prefix and base
URL. The three defaults are:

| Mode | Repo |
|---|---|
| Preset voice | `mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16` |
| Voice design | `mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16` |
| Voice clone | `mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16` |

## Other hosts

Anything that serves static files over HTTPS with directory structure intact:

- **Amazon S3 + CloudFront** — the classic; watch egress pricing.
- **Backblaze B2** — free egress via the Cloudflare bandwidth alliance.
- **A VPS with nginx or Caddy** — simplest to reason about, and fine on a LAN.
- **An internal mirror**, or your own Hugging Face fork with the base URL
  pointed at its `.../resolve/main` directory.

## When it goes wrong

**"Your server is missing a required model file: config.json (HTTP 404)."**
The base URL is wrong, or the upload flattened the structure. Check
`curl -sI <base>/config.json`.

**Some files 404 but the download completes.** Expected. Only `config.json`
and `model.safetensors` are required; the rest are best-effort.

**The download never starts, and the log mentions ATS.** The base URL is
`http://`. Use HTTPS.

**It re-downloads every time.** A folder counts as complete when it holds a
`config.json`, at least one `.safetensors`, and no `.incomplete` files. A
partial download leaves `.incomplete` markers behind — delete the folder under
`models/self-hosted/` and start again.
