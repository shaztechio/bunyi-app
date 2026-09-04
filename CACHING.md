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

# Caching and cost control for the model bucket

A runbook for putting `models.bunyi.app` behind Cloudflare's cache, and then
for making sure the bill can never surprise you. Written for the Bunyi mirror,
but the shape applies to any R2 bucket on a custom domain — see
[`SELF-HOSTING.md`](SELF-HOSTING.md) for how that bucket is built in the first
place.

Without any cache rule, every request reaches R2. Each model download is
13 requests, so 39 for a full install. Nothing about that is expensive —
R2's free tier covers roughly 769,000 model downloads a month, and egress is
free — but 11 of those 13 files are small, cacheable, and currently fetched
from the origin every single time for no reason.

> **Status, 4 September 2026: none of this is in effect.** The rules below are
> configured correctly and Cloudflare is ignoring them on `models.bunyi.app`,
> which appears to be a known limitation of R2 custom domains rather than
> anything here. Nothing is broken by it and nothing needs doing about it —
> [the measurements are further down](#none-of-this-currently-works-and-it-is-not-the-configuration).
> Read that before following the steps, so you are not debugging a rule that
> was never the problem.

## Read this before you turn caching on

**A stale cached file is no longer a silent problem.** Bunyi verifies every
downloaded file against `manifest.sha256`. Before that, a stale file was
quietly wrong; now it fails its digest and the download stops with an error.

That makes one combination actively dangerous: a **short** TTL on the
manifests and a **long** TTL on the files. Clients fetch the new manifest,
Cloudflare serves them cached old files, and the digests do not match. The
rules below use exactly that combination, because it is the right one for
everything except a model refresh — so the refresh procedure has to purge.
That is step 5, and it is not optional.

## What caching cannot fix

The two large weights files are **over Cloudflare's 512 MB cacheable ceiling**
(Free, Pro and Business plans; Enterprise defaults to 5 GB):

| File | Size | Cacheable |
|------|------|-----------|
| `model.safetensors` | ~1.7 GB | no |
| `speech_tokenizer/model.safetensors` | ~650 MB | no |
| the other 11 files | small | yes |

So ~2.3 GB per model still streams from R2 on every download no matter what
you do here. Egress is free, so this costs nothing; it is only slower for
users far from the bucket's region. Making those two cacheable would mean
splitting them into sub-512 MB parts, which needs client-side reassembly and
is not worth it at this scale.

## 1. Two cache rules

**Caching → Cache Rules.** Create both. Their expressions cannot both match
the same request, so **the order does not matter** — which is the point.
Overlapping rules on this zone have already caused one silent collision,
where a hostname rule quietly overwrote a path rule's browser TTL.

### Rule A — `Models: manifests`

```
(http.host eq "models.bunyi.app" and http.request.uri.path contains "/manifest.")
```

- Cache eligibility: **Eligible for cache**
- Edge TTL: Override origin → **5 minutes**
- Browser TTL: Override origin → **5 minutes**

Short, because the manifests are how a change reaches clients at all. This
is the one file you need to be able to replace without waiting.

### Rule B — `Models: files`

```
(http.host eq "models.bunyi.app" and not http.request.uri.path contains "/manifest.")
```

- Cache eligibility: **Eligible for cache**
- Edge TTL: Override origin → **1 month**
- Browser TTL: Override origin → **1 year**

Long, because model files never change in place — they change by being
replaced wholesale, which is what step 5 is about.

Nothing needs re-uploading to make this work. A cache rule overrides whatever
headers the origin sends, and Browser TTL covers the browser side, so there
is no need to push 11.6 GB again just to change metadata.

## 2. Tiered Cache

**Caching → Tiered Cache → Smart Tiered Cache Topology.** Free on every plan,
one toggle.

Without it, each of Cloudflare's ~300 edge locations fetches from R2
independently, so a "cache hit rate" spread across the world is much worse
than it sounds. With it, only upper-tier data centres talk to the origin.

## 3. Verify

```sh
# Second request should be HIT, with the long TTL
for i in 1 2; do
  curl -sI https://models.bunyi.app/customvoice/config.json \
    | grep -iE "cf-cache-status|^cache-control"
  echo "--"
done

# Manifests keep the short TTL
curl -sI https://models.bunyi.app/customvoice/manifest.sha256 \
  | grep -i "^cache-control"
```

Two different `max-age` values — `31536000` for the file, `300` for the
manifest — means the rules are scoped correctly. One value everywhere means
one rule is matching both, and Rule A's expression is the thing to check.

A newly cached object can also report `MISS` or `EXPIRED` on the first couple
of requests; only a sustained `DYNAMIC` means the rule is not matching.

## 4. Scope the site's asset rule

The zone's existing **`Long cache for static assets`** rule matches on path
alone:

```
starts_with(http.request.uri.path, "/assets/")
```

which applies to *every* host on the zone, `models.bunyi.app` included.
Nothing collides today because the bucket has no `/assets/` path, but Rule B
above would overlap it the moment one appeared. Scope it while you are here:

```
(http.host eq "bunyi.app" and starts_with(http.request.uri.path, "/assets/"))
```

## 5. Purging, when models change

Any time model files are replaced:

1. upload the new files,
2. regenerate and upload **both** manifests (`SELF-HOSTING.md` §7 and §7b),
3. **Caching → Configuration → Purge Everything**, or purge by prefix.

Skip step 3 and clients get the new manifest against cached old files. With
checksums live that is not a subtle degradation — it is a failed download
with a checksum error, for every user, until the cache expires.

The alternative that removes this footgun entirely is a **version segment in
the key**: publish to `/customvoice/v2/…` instead of overwriting
`/customvoice/…`, so new bytes live at a new URL and nothing cached is ever
stale. That is a larger change, because the base URLs are baked into the
built-in mirror configuration in the app and into `SELF-HOSTING.md`. Worth
doing if model updates ever become routine.

## What to expect

11 of 13 requests per model download become cache hits after the first
visitor in a region, so Class B operations against R2 drop by roughly 85%.
Small files also arrive from a nearby edge rather than the bucket's region.

The number to watch, if this ever stops being academic, is Class B operations
in the R2 dashboard. The free tier is 10 million a month. At 13 requests per
download that is ~769,000 downloads; with these rules it is several million.

Measured after the rules were applied: the small files return
`cf-cache-status: HIT`, and **both large files return `BYPASS`** — they are
over the 512 MB ceiling, so 6 of the 39 objects always reach R2. That is the
dominant term in the operation count and no amount of tuning changes it.

> **That measurement no longer reproduces.** Nothing on `models.bunyi.app`
> caches at all today. See "None of this currently works" below before
> spending an evening on the rules — they are not the problem.

## None of this currently works, and it is not the configuration

Measured 4 September 2026, while putting the ONNX set up for
[#100](https://github.com/shaztechio/bunyi-app/issues/100). **Every object on
`models.bunyi.app` returns `cf-cache-status: DYNAMIC`** — MLX and ONNX, small
files and large, manifests and weights, first request and fiftieth.

The rules are not at fault, and neither is the zone:

| Request | `Cache-Control` returned | `cf-cache-status` |
|---|---|---|
| `bunyi.app/` | `max-age=600` | `EXPIRED` |
| `bunyi.app/assets/og-card.png` | `max-age=31536000` | `MISS` |
| `models.bunyi.app/onnx/voiceclone/config.json` | *none* | `DYNAMIC` |
| `models.bunyi.app/customvoice/config.json` | *none* | `DYNAMIC` |

Cache Rules demonstrably work on this zone — the two site rules stamp headers
and cache. Both Models rules exist, are enabled, are set to **Eligible for
cache**, and carry the expressions above verbatim. And yet **no rule stamps
anything on `models.bunyi.app`** — not even the broad `Cache HTML at edge`
rule, which would leave `max-age=600` behind if it matched. This is not a rule
matching and declining to cache. Nothing matches.

### Two things that look like causes and are not

**It is not missing `Cache-Control` on the objects**, which is the usual
first answer and is wrong here. `customvoice/manifest.sha256` carries
`cache-control: public, max-age=300` as R2 object metadata, Cloudflare returns
that header to clients intact — and the response is still `DYNAMIC`. An object
with correct headers, passed through correctly, is not cached. Re-uploading
14.5 GB with headers would achieve nothing; that was checked before it was
suggested to anyone.

**That same header is also how this gets misdiagnosed.** Fetch
`customvoice/manifest.sha256`, see `max-age=300`, and it looks exactly like
Rule A working. It is the object's own metadata, set when the MLX set was
uploaded. `onnx/*/manifest.sha256` was uploaded without it and returns no
`Cache-Control` at all — same rule, same host, different object. Check
`cf-cache-status`, not `Cache-Control`, before concluding a rule is live.

### What it appears to be

A known Cloudflare behaviour with **R2 custom domains**, widely reported and
not something this repository can fix:

- [Cache Rules not applied to R2 custom domain — every response stays cf-cache-status: DYNAMIC](https://community.cloudflare.com/t/cache-rules-not-applied-to-r2-custom-domain-every-response-stays-cf-cache-status/943292)
- [R2 Bucket with Custom Domain always resolving cf-cache-status DYNAMIC](https://community.cloudflare.com/t/r2-bucket-with-custom-domain-always-resolving-cf-cache-status-dynamic/833941)
- [R2 Custom Domain not caching!](https://community.cloudflare.com/t/r2-custom-domain-not-caching/848225)

There is no caching control in **R2 → bucket → Settings → Custom Domains** to
find; that was looked for. If this is ever worth pursuing it is a support
ticket, not a configuration change.

### Why it has not mattered

Worth stating plainly so nobody treats this as urgent:

- **Egress from R2 is free.** Caching buys latency, not money.
- **The free tier is 10 million Class B operations a month**, and this mirror
  is nowhere near it. Uncached, a full three-mode ONNX install is ~106
  requests; the tier covers that roughly 94,000 times over.
- **It cannot help the files that actually take the time.** The ONNX set's
  largest are `text_embedding.npy` at 1,187 MB and `int4/vocoder.onnx.data`
  at 870 MB. Both are over the 512 MB ceiling and would `BYPASS` even with
  everything working.
- **Correctness is unaffected.** Every file is verified against
  `manifest.sha256` on arrival, so a cache that never fills cannot serve
  anything wrong. All three modes were downloaded from this mirror and
  generated successfully on the day this was measured.

What is lost is a nearby edge for the ~30 small files per model. Real, and
worth having if Cloudflare ever fixes it — not worth an evening now.

### One thing that is genuinely slower

Download throughput varies by which Cloudflare colo serves the request, and
with nothing cached every request goes to the origin. Measured minutes apart
from the same machine in Asia:

| Object | Colo (`CF-RAY` suffix) | Throughput |
|---|---|---|
| `int4/vocoder.onnx.data` | `SIN` — Singapore | 23.6 MB/s |
| `embeddings/text_embedding.npy` | `MRS` — Marseille | 1.5 MB/s |

Same bucket, same prefix, same client. A sixteenfold spread, decided by
routing. For comparison, the same 100 MB from Hugging Face at the same moment
came down at 9.9 MB/s, so the mirror is still the faster of the two on
average — it is just far less predictable than a cached edge would be.

# Cost controls

Caching reduces the bill. This section is about making sure it can never
surprise you.

## Size the risk before building anything

Egress is free, so only two lines can bill:

| | |
|---|---|
| **Storage** | 11.6 GB against a 10 GB free tier ≈ **$0.02/month**. Fixed while the objects exist; no traffic cutoff touches it. |
| **Class B ops** | 10M/month free, then $0.36/million. A cold download is ~14 operations, a warm one ~2. |

That puts the free tier somewhere between **770,000 and 5,000,000 model
downloads a month**. Reaching $50 would take roughly 150 million operations
beyond it. Class A is effectively zero — reads are Class B; you only generate
Class A operations by uploading.

The realistic worst case is a bill of a few dollars. Calibrate the machinery
accordingly.

**Bunyi issues one plain `GET` per file — no range requests.** That matters,
because a resumable or chunked downloader turns one file into many billable
reads. `HTTPFileDownloader` uses a single `URLSession` download task per file
with no `Range` header, and publishing `manifest.sha256` removed the
`HEAD`-per-file resume check as well. So the count really is ~14 cold and ~2
warm, not a multiple of it.

## Alerting

**Budget alerts** are the useful one. Manage Account → Billing → Billable
Usage → Budget alerts. Since June 2026 Cloudflare creates a $10 account-level
alert by default on Pay-as-you-go accounts, so check whether one already
exists before adding another.

They are dollar-denominated, account-wide, fire **once per billing cycle** on
*projected* spend, and are explicitly informational — they do not pause or cap
anything. At the numbers above, a $10 alert firing means something genuinely
anomalous.

**There is no native alert on R2 Class B operations specifically.** The
per-product usage notification requires Pro or higher, and R2 is not
documented as a selectable product for it. An operations threshold is
something you build (below) or check by eye in Billable Usage.

## There is no hard spend cap

Cloudflare does not offer one for R2, and budget alerts are documented as
informational. The only true spend cap Cloudflare ships is for AI Gateway and
does not generalise. **Any cutoff is one you build.**

## The kill switch

**A WAF custom rule returning 403.** WAF custom rules run *before* cache and
before the origin fetch, so a blocked request costs **no R2 operation at
all** — which is the property that makes this the right mechanism rather than
a Cache Rule or a Worker.

Security → WAF → Custom rules:

- Expression: `http.host eq "models.bunyi.app"`
- Action: **Block**, with a custom 403 body — the app surfaces the response,
  so plain text saying downloads are unavailable beats a generic block page.

**Create it disabled.** The point is that the emergency action is flipping one
toggle rather than authoring a rule under pressure. Free plan allows five
custom rules; effect is global within seconds; reversal is instant. It breaks
everything on `models.bunyi.app` and nothing else — the S3 API, Workers
bindings and the r2.dev URL are untouched.

Two mechanisms that sound similar and are not:

- **Disabling the R2 custom domain** (R2 → bucket → Settings → Custom Domains
  → Disable) also works and breaks only that hostname, but takes minutes
  rather than seconds. Prefer *Disable* over *Remove*: removing deletes the
  CNAME, and re-adding means going through "Initializing" again.
- **A Worker in front** is the wrong tool. It adds a Workers request charge on
  top of R2 — a new billing line to police an existing one.

## The control you actually want day to day

**A rate limiting rule**, available on the Free plan. Same hostname
expression, Block action, tuned generously — a legitimate download is ~14
requests, so a few hundred per IP per minute leaves enormous headroom.

This is the one that handles the realistic scenario. It degrades an abusive
client instead of taking the app offline for everyone, and unlike the kill
switch it needs no decision at the moment it matters.

## Automating a cutoff

Buildable, and worth it only if the reassurance is worth more than the hour —
at these numbers it is insurance against a bill you would struggle to make
reach $5.

Reading usage: the GraphQL Analytics API dataset `r2OperationsAdaptiveGroups`
gives per-bucket operation counts. **It exposes `actionType`, not a billing
class**, so mapping operations to Class A/B is yours to maintain against the
pricing page — and retention is 31 days, so month-to-date works but backfill
does not. Storage comes from
`GET /accounts/{account_id}/r2/buckets/{bucket}/metrics`.

Flipping the switch: `PATCH /zones/{zone_id}/rulesets/{ruleset_id}/rules/
{rule_id}` to enable the WAF rule, or `PUT /accounts/{account_id}/r2/buckets/
{bucket}/domains/custom/{domain}` with `{"enabled": false}` for the domain.

Run it from **outside** the account it watches — GitHub Actions on a cron
rather than a Cloudflare Worker — so an account-level problem cannot disable
the watchdog. Set the threshold well below anything you would care about:
analytics lag by minutes, so a tight threshold is unsafe.

## What this does not protect against

- **The storage line.** Cutting all traffic leaves the ~$0.02/month. The only
  lever is deleting objects; getting under 10 GB zeroes it.
- **A bug in Bunyi itself.** A retry loop or a version check that re-downloads
  on launch is far likelier than an attacker, comes from legitimate-looking
  IPs, and is spread across installs so per-IP rate limits never trip. This is
  the scenario to actually plan for, and it got more likely when the app
  started shipping a built-in option pointing here. Checksums help: a failing
  digest makes a retry loop loud rather than silent.
- **Anything between checks.** An hourly cron has an hour of blind spot, plus
  analytics lag on top.
- **Deliberate mirroring.** Someone pointing a scraper at these URLs costs you
  operations and gets free bandwidth. Cheap at this scale, but the kill switch
  is all-or-nothing and takes real users down with them. Signed URLs are the
  answer if it ever matters.
- **Forgetting to re-arm.** If the kill switch trips, the app stays broken
  until you disable the rule. Nothing resets it at the start of a billing
  cycle.
- **Products added later.** Cache Reserve in particular is separately billed
  at real rates and would change this analysis. So would putting a Worker in
  the path.
- **A leaked API token** with R2 write access, which generates Class A
  operations and storage without touching `models.bunyi.app` at all. Nothing
  above sees it; the budget alert is the only backstop, and it fires once.

## If you do only two things

Confirm the default budget alert exists, and pre-create the WAF rule in a
disabled state. Everything else here is refinement.
