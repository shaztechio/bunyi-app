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

## Which model set you are hosting

**Read this before downloading anything.** Bunyi ships as two apps, and they do
not use the same weights. macOS runs MLX and wants `.safetensors`; Windows and
Linux run ONNX Runtime and want `.onnx` graphs. Neither can load the other's
files, so hosting the wrong set produces a mirror that downloads perfectly and
then fails to generate.

| | macOS (MLX) | Windows and Linux (ONNX) |
|---|---|---|
| Preset voice | `mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16` | `elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX` |
| Voice design | `mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16` | `wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX` (`int4`) |
| Voice clone | `mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16` | `wavekat/Qwen3-TTS-0.6B-Base-ONNX` (`int4`) |
| Total to host | 11.6 GB | 15.6 GB |

Windows and Linux share one set exactly — same app, same files, byte for byte —
so there is nothing to host twice for them. Serving both families means two sets
of folders in the same bucket, which is fine and costs pennies.

Every step below works for either. Where they differ, the difference is in the
variables you set in step 5 and nowhere else.

> **Voice clone uses a different model size across the two.** macOS uses the
> 1.7B Base model and the ONNX apps use the 0.6B one, because no 1.7B ONNX
> export meets the in-context-learning requirement in §1. This is deliberate,
> not a mistake to correct while mirroring.

## Try this first

Self-hosting is real infrastructure. If the goal is only "download faster
once", there is a cheaper answer:

```sh
uv tool install huggingface_hub      # or: pipx install huggingface_hub
hf download <repo> --local-dir <models-folder>/models/<repo>
```

**Settings → Storage** already shows the `hf download` line per mode with your
models folder filled in — in both apps. Pre-fetch there and the app finds the
files on first Generate, with no network access at all.

One caveat for the ONNX exports: two of the three publish their weights twice,
once at `int4/` and once at `fp32/`, and Bunyi only ever reads `int4/`. A plain
`hf download` fetches both, turning a 5.85 GB download into 18.55 GB. Add
`--exclude "fp32/*"` and the numbers in the table above are what you get.

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
bandwidth at roughly **100 GB/month**. A single model runs to gigabytes either
way, and individual `.safetensors` and `.onnx.data` files are far over 100 MB.

**GitHub Releases does not work either**, for two reasons. Release assets are
capped at **2 GB** each and the largest MLX weight file is 3.86 GB. And assets
are a flat namespace, so there is no way to express
`speech_tokenizer/config.json` — or `int4/talker_decode.onnx` — in an asset
name, and both models need those paths to survive.

## What the app requires

The same in both apps:

| Requirement | Detail |
|---|---|
| **`<base>/manifest.sha256`, or `manifest.txt`** | Whitespace-separated: a 64-hex first token is a digest, the rest is the path. Blank lines and `#` comments ignored. `manifest.sha256` wins when both are served. If neither is, the app falls back to a built-in file list. |
| **`<base>/<path>` for each entry** | Must return **200**. Directory structure is preserved on disk, so nested paths must be served as nested paths. |
| **Everything else** | Best-effort. A 404 on a non-required file is logged and skipped. |

No CORS headers are needed — these are native apps, not browsers.

Downloaded files land in `models/self-hosted/<slug>` inside your models folder
and are reused offline from then on, exactly like Hub downloads. The slug comes
from the base URL's host and path, so **changing the URL later means a fresh
download into a new folder** — pick the folder names in step 5 as though they
are permanent, because for anyone already using them they are.

And where they differ:

| | macOS (MLX) | Windows and Linux (ONNX) |
|---|---|---|
| **Plain `http://`** | Blocked by App Transport Security. Allowing it needs an `NSAppTransportSecurity` exception in `project.yml` and a rebuild. | Allowed as it is — no equivalent gate, so a LAN server on `http://` works. |
| **Required files** | `config.json` and `model.safetensors`. A non-200 on either fails the download. | A per-export list — see `apps/dotnet/src/Core/Models/ModelLayout.cs`. Roughly: `config.json`, the eight `int4/*.onnx` graphs and their `.data` siblings, every `embeddings/*.npy`, and `tokenizer/{vocab.json,merges.txt}`. Voice clone also requires `speaker_encoder.onnx` and `tokenizer_encoder.onnx` with their `.data` files. |
| **Fallback file list** | A built-in Qwen3-TTS list, plus `tokenizer.json` backfilled from a known source when missing. | The built-in list for that export. Serving no manifest at all therefore works — but publishing `manifest.sha256` is what lets the app verify what it got, and it is what `spec/FEATURES.md` §3a requires before a mirror can be offered inside the app. |
| **Counts as complete** | `config.json`, at least one `.safetensors`, and no `.incomplete` files. | Every required entry present at non-zero length, every `.onnx` that declares external data sitting beside its `.onnx.data`, and no `*.incomplete` anywhere. |

**The `.onnx.data` pairing is the one to watch.** A graph file is a few
megabytes and its external data is hundreds; lose the big half in an upload and
the folder still looks finished. That is the exact shape the completeness rule
was written for.

### The manifest overrides the built-in list

This is the mistake that costs your users the most, and it is easy to make.
When a manifest is served, **it is the file list** — the app fetches what the
manifest names, not what it knows the model needs. So a manifest generated from
a folder that still contains `fp32/` makes every client download 12.70 GB of
weights that nothing will ever open.

Generate manifests from a folder holding exactly what you mean to serve. Step 6
excludes the right things; step 7 lists whatever survived.

## What this costs, and how big it is

**macOS (MLX)** — 11.6 GB for all three:

| Model | Total | Largest single file |
|---|---|---|
| `…0.6B-CustomVoice-bf16` (preset voice) | 2.50 GB | 1.81 GB |
| `…1.7B-VoiceDesign-bf16` (voice design) | 4.52 GB | 3.83 GB |
| `…1.7B-Base-bf16` (voice clone) | 4.54 GB | 3.86 GB |

**Windows and Linux (ONNX)** — 15.6 GB for all three, once `fp32/` is left
behind. The right-hand column is what the repository publishes, and the gap
between the two columns is the whole reason step 6 has exclusions:

| Model | Hosted | Published upstream |
|---|---|---|
| `…0.6B-CustomVoice-ONNX` (preset voice) | 5.88 GB | 5.88 GB |
| `…1.7B-VoiceDesign-ONNX` (voice design) | 5.85 GB | 18.55 GB |
| `…0.6B-Base-ONNX` (voice clone) | 3.86 GB | 8.77 GB |

R2's free tier covers 10 GB of storage. One family costs a few cents a month
beyond it — about $0.02 for MLX, $0.08 for ONNX at $0.015/GB — and both
together, 27.2 GB, is about $0.26. Hosting only the modes you actually use may
keep you inside the free tier entirely. Downloads (egress) are free from R2 at
any volume, which is the reason to use it.

That 3.86 GB MLX file is also the second reason GitHub Releases cannot work:
assets are capped at 2 GB each.

Requests are the other half of the bill, and they are what caching reduces —
see [`CACHING.md`](CACHING.md) once your bucket is serving. Read it before
turning caching on rather than after: with checksums published, a stale
cached file stops a download instead of quietly degrading it.

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

### 5. Choose which modes to host

Each mode uses a different model. Nothing is shared between them: each needs
its own download, manifest, folder in the bucket, and base URL in Settings.

Set these three variables once and every remaining step follows from them.
**This is the only step that differs between the two apps.** Run the block for
the models you are hosting; everything after it is identical.

For **macOS (MLX)**:

```sh
MODELS=(
  "customvoice:mlx-community/Qwen3-TTS-12Hz-0.6B-CustomVoice-bf16"
  "voicedesign:mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-bf16"
  "voiceclone:mlx-community/Qwen3-TTS-12Hz-1.7B-Base-bf16"
)
EXCLUDE=()                                # nothing to leave behind
PROBE=speech_tokenizer/config.json        # a nested file every model has
```

For **Windows and Linux (ONNX)**:

```sh
MODELS=(
  "onnx/customvoice:elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX"
  "onnx/voicedesign:wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX"
  "onnx/voiceclone:wavekat/Qwen3-TTS-0.6B-Base-ONNX"
)
EXCLUDE=(--exclude "fp32/*" --exclude "validation/*")
PROBE=tokenizer/vocab.json
```

The left half of each line is the folder name, the right half is the Hugging
Face repo. Everything below reuses them, so run these in one shell session.

**The folder name becomes part of the URL, and the URL becomes the folder on
every user's disk.** Choose it once. Serving both families from one bucket is
why the ONNX names are nested under `onnx/` here — slashes work fine
throughout, in the bucket and in the base URL — but any scheme does, as long as
the two sets cannot collide.

**You do not have to host all three modes.** A Settings field left blank keeps
using Hugging Face, so delete the lines you do not want. Preset voice alone is
2.50 GB on MLX or 5.88 GB on ONNX; the full sets are 11.6 GB and 15.6 GB.

**`EXCLUDE` is not optional for ONNX.** Two of those repos publish the same
weights twice, at `int4/` and `fp32/`, and Bunyi reads only `int4/`. Without the
exclusions you download 18.55 GB instead of 5.85 GB, pay to store it, and — far
worse — list it in the manifest, so every one of your users downloads it too.
`validation/` is sample audio from the export process, of use to nobody here.

### 6. Download them

> **Hosting the ONNX set from a machine that already runs Bunyi? Skip 6 and 7.**
>
> Bunyi has already downloaded these models — generating once in a mode fetches
> them — and `apps/dotnet/tools/MirrorManifest` turns what is on disk into a
> mirror without downloading anything twice:
>
> ```sh
> cd apps/dotnet
> dotnet run --project tools/MirrorManifest -- --out ~/bunyi-mirror
> ```
>
> It reads the models folder, checks every file each mode **requires** is
> present, writes `manifest.sha256` and an `rclone --files-from` list per
> prefix, and prints the upload commands with your paths already in them.
>
> It is not just a shortcut. The manifest comes from `ModelLayout` — the app's
> own statement of what it fetches — rather than from a `find` over a folder, so
> the two failures this step warns about below cannot happen: a missing required
> file stops the run by name, and a file no mode asks for is reported and left
> out instead of being published to every user. On a machine that had generated
> in all three modes it left out six such files without being told to.
>
> Then continue at step 8.

```sh
uv tool install huggingface_hub      # once; puts `hf` in ~/.local/bin

for pair in "${MODELS[@]}"; do
  hf download "${pair#*:}" --local-dir ~/bunyi-models/"${pair%%:*}" "${EXCLUDE[@]}"
done
```

This is the slow part — 11.6 GB or 15.6 GB, once. Everything after it comes
from your own host.

Check you got what you expected before going on, because every later step
inherits whatever is in these folders:

```sh
du -sh ~/bunyi-models/*/ ~/bunyi-models/*/*/ 2>/dev/null
```

An ONNX folder near 18 GB means the exclusions did not apply. Delete it and run
the download again rather than pruning it by hand — the manifest in step 7 is
built from whatever is left on disk, so a folder that is only mostly right
produces a manifest that is confidently wrong.

### 7. Generate a manifest for each

`manifest.txt` is the list of files Bunyi should fetch. Each model needs its
own, built from its own folder:

```sh
for pair in "${MODELS[@]}"; do
  ( cd ~/bunyi-models/"${pair%%:*}" \
    && find . -type f ! -name 'manifest.*' ! -path './.*' \
       | sed 's|^\./||' > manifest.txt )
done
```

The `! -path './.*'` matters. `hf download` leaves a `.cache/huggingface/`
tree of `.lock` and `.metadata` files behind, and without that exclusion the
manifest lists about thirty of them — forty-three entries where thirteen are
real. Bunyi would fetch every one: thirty pointless requests, and thirty stray
files in the models folder.

(Releases before this counted files rather than bytes for progress, so a
manifest like that also reported "1 of 43" while the single 3.8 GB file
downloaded, and the bar barely moved. Progress follows bytes now — but the
clutter is reason enough to exclude the cache.)

Check they include the nested entries, not only top-level files:

```sh
for pair in "${MODELS[@]}"; do
  printf '%-22s %s nested entries\n' "${pair%%:*}" \
    "$(grep -c / ~/bunyi-models/"${pair%%:*}"/manifest.txt)"
done
```

Each should report several. A zero means something flattened that folder and
the upload will not work.

For ONNX, one more look — at what the manifest says, not only how much of it
there is:

```sh
for pair in "${MODELS[@]}"; do
  grep -c '^fp32/' ~/bunyi-models/"${pair%%:*}"/manifest.txt
done
```

Every line must be `0`. A manifest **is** the file list, so an `fp32/` entry
here is an instruction to every client to fetch 12.70 GB it will never open.

### 7b. Add checksums (recommended)

Also publish `manifest.sha256`, and Bunyi verifies every file it downloads
against it. Without one, a truncated upload or a half-finished `rclone` run
produces a model that loads and generates nonsense, with nothing to say why.

```sh
for pair in "${MODELS[@]}"; do
  ( cd ~/bunyi-models/"${pair%%:*}" \
    && find . -type f ! -name 'manifest.*' ! -path './.*' \
       | sed 's|^\./||' | sort | tr '\n' '\0' \
       | xargs -0 shasum -a 256 > manifest.sha256 )
done
```

On Linux, `sha256sum` in place of `shasum -a 256` — the output format is the
same, which is the point of using it. Verify before you upload anything:

```sh
for pair in "${MODELS[@]}"; do
  ( cd ~/bunyi-models/"${pair%%:*}" && shasum -a 256 -c manifest.sha256 ) \
    | grep -v ': OK$' || echo "${pair%%:*}: all files OK"
done
```

Bunyi prefers `manifest.sha256` and falls back to `manifest.txt`, so keep
both and regenerate them together. **Do not put the digests into
`manifest.txt` instead.** Every released version of Bunyi reads each line of
that file as a filename, so a `<digest>  model.safetensors` line is requested
verbatim, 404s, and fails the download outright. A separate file is invisible
to those versions, which is what makes it safe to add whenever you like.

What this does and does not do: it catches corruption, truncation and partial
uploads — the things that actually go wrong. It is not proof of authenticity.
Anyone who can rewrite a model file on your server can rewrite the manifest
next to it.

### 8. Upload them, keeping the folders as folders

A model is not a flat pile of files. The MLX ones have a `speech_tokenizer/`
subfolder; the ONNX ones have `int4/`, `embeddings/` and `tokenizer/`. Bunyi
asks for those files by those paths, so they have to arrive in the bucket at the
same paths (`customvoice/speech_tokenizer/config.json`, not
`customvoice/config.json`). That is what "preserving directory structure"
means, and `rclone copy` does it by default.

```sh
for pair in "${MODELS[@]}"; do
  echo "=== ${pair%%:*} ==="
  rclone copy ~/bunyi-models/"${pair%%:*}" \
    r2:bunyi-models/"${pair%%:*}" \
    --exclude '.*' --exclude '.*/**' --progress
done
```

Same reason as the manifest: the two `--exclude` patterns keep `hf download`'s
`.cache/huggingface/` tree out of the bucket. Nothing there is secret — locks
and etags — but it is thirty-odd objects nobody will ever read.

**Already uploaded them?** The manifest is what actually matters, so
regenerate and re-upload that; then delete the stray objects at your leisure:

```sh
for pair in "${MODELS[@]}"; do
  rclone purge r2:bunyi-models/"${pair%%:*}"/.cache
  rclone copy ~/bunyi-models/"${pair%%:*}" r2:bunyi-models/"${pair%%:*}" \
    --include 'manifest.*'
done
```

Both manifests, together — they describe the same file set, and a bucket
where only one of them has been refreshed is a bucket where new clients and
old clients disagree about what to download.

Confirm the whole set survived the trip. `rclone check` compares both sides
file by file, which catches a truncated upload as well as a flattened one:

```sh
for pair in "${MODELS[@]}"; do
  echo "=== ${pair%%:*} ==="
  rclone check ~/bunyi-models/"${pair%%:*}" r2:bunyi-models/"${pair%%:*}" \
    --exclude '.*' --exclude '.*/**'
done
```

`0 differences found` is the line to look for. Anything else names the files,
and re-running the `rclone copy` above fixes it.

For ONNX especially, confirm no graph lost its external data on the way up — a
`.onnx` without its `.onnx.data` is the failure that looks like success:

```sh
for pair in "${MODELS[@]}"; do
  rclone ls r2:bunyi-models/"${pair%%:*}" | awk '{print $2}' | sort > /tmp/there
  ( cd ~/bunyi-models/"${pair%%:*}" && find . -name '*.onnx.data' | sed 's|^./||' ) \
    | sort | comm -23 - /tmp/there | sed "s|^|MISSING ${pair%%:*}/|"
done
```

No output means every one arrived.

### 9. Make the bucket readable over HTTPS

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

One bucket serves every model; the folder name is what separates them.

### 10. Point Bunyi at them

**Settings → Models** has one field per mode, in both apps. Fill in the ones
you hosted, using the folder names you chose in step 5:

| Mode | macOS (MLX) | Windows and Linux (ONNX) |
|---|---|---|
| Preset voice | `https://models.bunyi.app/customvoice` | `https://models.bunyi.app/onnx/customvoice` |
| Voice design | `https://models.bunyi.app/voicedesign` | `https://models.bunyi.app/onnx/voicedesign` |
| Voice clone | `https://models.bunyi.app/voiceclone` | `https://models.bunyi.app/onnx/voiceclone` |

**Save the three as a configuration** rather than retyping them. Settings →
Models has a name field and a Save button for exactly this: the three belong
together, and switching back to Hugging Face is then one click instead of three
cleared fields.

With the quick option, substitute `https://pub-xxxxxxxx.r2.dev` for
`https://models.bunyi.app`.

The scheme decides how the value is read: anything starting `http://` or
`https://` is a base URL, anything else is a Hugging Face repo ID. Clearing a
field restores the built-in default for that mode.

### 11. Verify before trusting it

```sh
BASE=https://models.bunyi.app

for pair in "${MODELS[@]}"; do
  for f in manifest.txt manifest.sha256 "$PROBE"; do
    printf '%-22s %-30s ' "${pair%%:*}" "$f"
    curl -sI "$BASE/${pair%%:*}/$f" | head -1
  done
done
```

Every line must say `200`. The `$PROBE` line is the one that catches a
flattened upload — the manifests can both pass while the model is still
unusable.

Then press Generate in each mode you hosted and watch the log: it names every
file as it downloads, and says exactly which one failed and with what status
code. On macOS that is **Window → Logs** (⌘L); on Windows and Linux it is the
**Logs** button in the header.

## What self-hosting does not cover

Voice clone transcribes the reference clip with a 141 MB Whisper model, fetched
through the same downloader with the same progress, resume and checksums. It is
**not** one of the three configurable sources, so there is no field to point at
your own copy of it. On a machine that will never reach Hugging Face, fetch it
ahead of time the way **Settings → Storage** shows.

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

**Some files 404 but the download completes.** Expected — non-required files
are best-effort. On MLX only `config.json` and `model.safetensors` are
required; on ONNX the required list is per export and much longer, so a 404
there usually stops the download instead.

**The download never starts, and the log mentions ATS.** macOS only: the base
URL is `http://`. Use HTTPS. The Windows and Linux app has no equivalent gate,
so plain HTTP on a LAN works there as it is.

**Everything downloads, then generation fails to load the model.** Almost
always a missing `.onnx.data`. The graph beside it is small enough to arrive
unnoticed, so the folder looks complete. Re-run the `rclone check` in step 8;
it names the file.

**Every client is downloading 18 GB.** The manifest lists `fp32/`. Regenerate
it from a folder that does not contain those files — step 6's `EXCLUDE`, then
steps 7 and 7b again — and re-upload both manifests.

**`rclone` says `AccessDenied` on `ListBuckets` (HTTP 403).** Expected, and
not a broken token: a bucket-scoped token cannot enumerate the account's
buckets. Address the bucket directly — `rclone lsjson r2:bunyi-models` — and
operations inside it will work. Only reach for an account-wide token if you
genuinely need to list buckets.

**`rclone purge` says `AccessDenied` on `GetBucketVersioning`.** Same cause,
and it is a warning wearing an error's clothes — read the rest of the line:
*"assuming unversioned"*. `purge` asks whether the bucket keeps object
versions so it knows whether to delete those too; a bucket-scoped token cannot
read that setting, so rclone assumes not and deletes the objects anyway. The
assumption is right: R2 buckets are unversioned unless you turn it on. Confirm
it worked from outside rather than from the exit code:

```sh
curl -sI https://models.bunyi.app/customvoice/.cache/huggingface/download/config.json.lock | head -1
```

A `404` means gone.

**It re-downloads every time.** The folder is not counting as complete. On
MLX that means a `config.json`, at least one `.safetensors`, and no
`.incomplete` files. On ONNX it means every required entry present at non-zero
length, every `.onnx` that declares external data sitting beside its
`.onnx.data`, and no `*.incomplete` anywhere. A partial download leaves those
markers behind — delete the folder under `models/self-hosted/` and start again.

**A mode still downloads from Hugging Face after you changed the URL.** Check
the trailing slash and the spelling: the folder on disk is derived from the URL,
so `…/onnx/customvoice` and `…/onnx/customvoice/` are two different mirrors as
far as the app is concerned, each with its own copy.
