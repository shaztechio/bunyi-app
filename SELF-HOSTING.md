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
uv tool install huggingface_hub      # or: pipx install huggingface_hub
hf download <repo> --local-dir <models-folder>/models/<repo>
```

**Settings → Storage** already shows the `hf download` line per mode with your
models folder filled in. Pre-fetch there and the app finds the files on first
Generate, with no network access at all.

Do not reach for `pip install` on macOS without checking which Python you get:
`/usr/bin/pip` is still Python 2.7 and fails with *"Could not find a version
that satisfies the requirement"*, leaving no `hf` command behind. `uv` or
`pipx` install it into `~/.local/bin` without touching any Python you depend
on.

Two pieces of advice that are widely repeated and now wrong:

- **`huggingface_hub[hf_transfer]` no longer exists.** The extra was removed in
  v1.0 and the Xet backend (`hf_xet`) ships by default, so
  `HF_HUB_ENABLE_HF_TRANSFER=1` does nothing.
- **Xet only accelerates repos stored that way.** All three Qwen3-TTS repos
  report `xetEnabled: false`, so they come from the ordinary CDN at ordinary
  speed. No client-side flag makes them faster — which is exactly why
  self-hosting is worth the trouble for these models.

Self-hosting earns its keep when you are serving other people or many
machines — not for a one-off download.

## Not on bunyi.app

The obvious idea does not work. `bunyi.app` is GitHub Pages, which caps
individual files at **100 MB** (a hard Git limit), sites at about **1 GB**, and
bandwidth at roughly **100 GB/month**. The models are ~1.4 GB, ~3.4 GB and
~3.4 GB, and individual `.safetensors` files are far over 100 MB.

**GitHub Releases does not work either**, for two reasons. Release assets are
capped at **2 GB** each and the largest weight file is 3.86 GB. And assets are
a flat namespace, so there is no way to express `speech_tokenizer/config.json`
in an asset name — the model needs that path to survive.

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

## What this costs, and how big it is

| Model | Total | Largest single file |
|---|---|---|
| `…0.6B-CustomVoice-bf16` (preset voice) | 2.50 GB | 1.81 GB |
| `…1.7B-VoiceDesign-bf16` (voice design) | 4.52 GB | 3.83 GB |
| `…1.7B-Base-bf16` (voice clone) | 4.54 GB | 3.86 GB |

**All three come to 11.6 GB.** R2's free tier covers 10 GB of storage, so
hosting the lot costs a few cents a month — about $0.02 at $0.015/GB beyond
the free allowance. Hosting only the mode you actually use keeps you inside
the free tier. Downloads (egress) are free from R2 at any volume, which is the
reason to use it.

That 3.86 GB file is also the second reason GitHub Releases cannot work:
assets are capped at 2 GB each.

## Step by step (Cloudflare R2)

R2 is Cloudflare's object storage: somewhere to put files so they have URLs.
It is like a folder on the internet, and unlike most such services it does not
charge for downloads.

If you have never used it, this is the whole path from nothing.

### 1. Create a Cloudflare account and turn on R2

1. Sign up at [dash.cloudflare.com/sign-up](https://dash.cloudflare.com/sign-up).
   Free.
2. In the sidebar choose **R2 Object Storage**.
3. Click through the R2 activation. **It asks for a payment card even for the
   free tier.** Nothing is charged inside the free allowance.

### 2. Create a bucket

**R2 → Create bucket.** Name it something like `bunyi-models`. Leave the
location automatic. A bucket is just a named container for files.

### 3. Create an API token

This is how `rclone` proves it is you.

1. **R2 → API → Manage API tokens → Create API token.**
2. Permission: **Object Read & Write**.
3. Scope it to the `bunyi-models` bucket.
4. Create it, then copy the three values it shows **once**:
   **Access Key ID**, **Secret Access Key**, and your **Account ID** (also in
   the R2 sidebar).

### 4. Install rclone and point it at the bucket

`rclone` is a file-copying tool that speaks R2's protocol.

```sh
brew install rclone
```

Configure it in one command — no interactive menus. Substitute your three
values:

```sh
rclone config create r2 s3 \
  provider=Cloudflare \
  access_key_id=YOUR_ACCESS_KEY_ID \
  secret_access_key=YOUR_SECRET_ACCESS_KEY \
  endpoint=https://YOUR_ACCOUNT_ID.r2.cloudflarestorage.com \
  acl=private
```

Check it can reach the bucket:

```sh
rclone lsjson r2:bunyi-models
```

An empty bucket prints `[]`. That is success.

Do **not** check with `rclone lsd r2:` — that lists every bucket in the
account, which the token from step 3 is scoped away from on purpose, so it
fails with `AccessDenied` even though the token works perfectly for the
bucket it is meant for.

### 5. Download the model once

```sh
uv tool install huggingface_hub      # once; puts `hf` in ~/.local/bin
hf download mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16 \
  --local-dir ~/bunyi-models/customvoice
```

This is the one slow download. Everything after it is served from your own
host.

### 6. Generate the manifest

`manifest.txt` is the list of files Bunyi should fetch. Build it from the
folder you just downloaded:

```sh
cd ~/bunyi-models/customvoice
find . -type f ! -name manifest.txt | sed 's|^\./||' > manifest.txt
```

Check it includes the nested entries, not only top-level files:

```sh
grep speech_tokenizer manifest.txt
```

Expect to see `speech_tokenizer/config.json` and friends. If that comes back
empty, something flattened the folder and the upload will not work.

### 7. Upload, keeping the folders as folders

The model is not a flat pile of files — it has a `speech_tokenizer/`
subfolder, and Bunyi asks for those files by that path. So they have to arrive
in the bucket at the same path: `customvoice/speech_tokenizer/config.json`,
not `customvoice/config.json`. That is what "preserving directory structure"
means, and `rclone copy` does it by default.

```sh
rclone copy ~/bunyi-models/customvoice r2:bunyi-models/customvoice --progress
```

Confirm the nesting survived:

```sh
rclone ls r2:bunyi-models/customvoice | grep speech_tokenizer
```

### 8. Make the bucket readable over HTTPS

The files exist but are private. Two ways to expose them:

**The quick way — an r2.dev URL.** In the bucket's **Settings → Public
access**, enable **Allow Access** for the r2.dev subdomain. Cloudflare gives
you a URL like `https://pub-xxxxxxxx.r2.dev`. Fine for trying this out;
Cloudflare rate-limits it and says not to use it in production.

**The proper way — your own subdomain**, e.g. `models.bunyi.app`. In the
bucket's **Settings → Custom Domains → Connect Domain**.

> **This requires the domain's DNS to be on Cloudflare.** `bunyi.app` currently
> uses Hover's nameservers, so you would first move the zone to Cloudflare
> (free: add the site, copy the existing records across, change the
> nameservers at Hover). GitHub Pages keeps working — the same A, AAAA, and
> CNAME records, set to "DNS only". If you would rather not move DNS, use the
> r2.dev URL.

### 9. Point Bunyi at it

**Settings → Models**, in the field for that mode:

```
https://models.bunyi.app/customvoice
```

or, with the quick option:

```
https://pub-xxxxxxxx.r2.dev/customvoice
```

The scheme decides how the value is read: anything starting `http://` or
`https://` is a base URL, anything else is a Hugging Face repo ID. Clearing
the field restores the built-in default.

### 10. Verify before trusting it

```sh
curl -sI https://models.bunyi.app/customvoice/manifest.txt | head -1
curl -sI https://models.bunyi.app/customvoice/config.json   | head -1
curl -sI https://models.bunyi.app/customvoice/speech_tokenizer/config.json | head -1
```

All three must be `200` — the third is the one that catches a flattened
upload. Then press Generate and watch **Window → Logs** (⌘L): it names every
file as it downloads, and says exactly which one failed and with what status
code.

Repeat per mode, each with its own prefix and base URL:

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

**`rclone` says `AccessDenied` on `ListBuckets` (HTTP 403).** Expected, and
not a broken token: a bucket-scoped token cannot enumerate the account's
buckets. Address the bucket directly — `rclone lsjson r2:bunyi-models` — and
operations inside it will work. Only reach for an account-wide token if you
genuinely need to list buckets.

**It re-downloads every time.** A folder counts as complete when it holds a
`config.json`, at least one `.safetensors`, and no `.incomplete` files. A
partial download leaves `.incomplete` markers behind — delete the folder under
`models/self-hosted/` and start again.
