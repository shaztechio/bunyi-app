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

# Caching the model bucket at Cloudflare

A runbook for putting `models.bunyi.app` behind Cloudflare's cache. Written
for the Bunyi mirror, but the shape applies to any R2 bucket on a custom
domain — see [`SELF-HOSTING.md`](SELF-HOSTING.md) for how that bucket is
built in the first place.

Without any cache rule, every request reaches R2. Each model download is
13 requests, so 39 for a full install. Nothing about that is expensive —
R2's free tier covers roughly 769,000 model downloads a month, and egress is
free — but 11 of those 13 files are small, cacheable, and currently fetched
from the origin every single time for no reason.

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
