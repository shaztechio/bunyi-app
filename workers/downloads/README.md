# Bunyi model download redirects

This Worker issues five-minute, method-specific S3 presigned URLs for files
in the six published model manifests. A 307 sends the client directly to R2;
the Worker does not stream or cache weights. GET and HEAD are supported.
The original model URL remains the source identity in both apps.

## Setup and deployment

`npm ci`, `npm test`, then `npm run build` performs a dry run.
`npm run deploy` updates the live Worker at `models.bunyi.app/*` and its
test address, `bunyi-downloads.bunyi.workers.dev`. The production route was
activated in the dashboard on 11 September 2026 after native-client tests
passed. The checked-in route records that activation for future deployments.
Keep its dashboard failure mode set to **Fail closed (block)**.

The existing Worker must have these **secrets**, entered through Cloudflare:
`R2_ACCESS_KEY_ID` and `R2_SECRET_ACCESS_KEY`. Use an Object Read only
credential scoped to `bunyi-models`. Never put credentials or signed URLs
in this repository, app binaries, issue bodies, or test output.

The signer binds DOWNLOAD_PERMISSION to the control worker's named
PermissionService entrypoint. That service exposes only a private read operation
against one SQLite Durable Object selected by bucket name; the signer cannot
mutate permission. Every valid GET/HEAD consults it afresh. No allowed decision is
cached, reused across requests, or obtained from legacy KV. The control worker
persists a block before domain shutdown, so API failures cannot clear permission.

DOWNLOADS_ENABLED must equal true before the authority is consulted. Explicit
blocked permission yields 503, Retry-After: 60 and X-Bunyi-Download-Status: paused.
Uninitialized/malformed state, missing binding, service/storage failure, or a
one-second timeout yields generic unavailable 503 without redirect or paused marker.
Both hostnames enforce the same gate. Authorization checks ordered after a persisted
block deny. A pre-block grant may finish its redirect within two seconds; a fresh
request nonce and signing timestamp anchored to authorization prevent delayed/reused
grants extending validity. URLs last 300 seconds; active transfers may continue.
Never log credentials, signed URLs, or upstream exception details.

The checked-in DOWNLOADS_ENABLED is false in both production and staging configs.
Every deploy explicitly preserves the intended pause; keep_vars does not override
a checked-in variable. After owner recovery, enabling issuance is deliberate:

```sh
# Owner only, after the coordinated rollout and /status verification:
npx wrangler deploy --var DOWNLOADS_ENABLED:true
# Emergency pause / every migration or rollback deploy:
npx wrangler deploy --var DOWNLOADS_ENABLED:false
```

## Published files

`src/published-files.json` lists exact permitted keys and their SHA-256
digests. Only the six public manifests and their published files are allowed.
When model contents change, run `node tools/refresh-files.js`, inspect the
diff against the uploaded manifests, test and deploy through a PR.
The refresh tool reads public manifests over HTTPS; it never lists arbitrary
bucket contents or publishes new objects.

Requests cannot select a different bucket, expiry, response-header override,
or destination. Request query parameters and credentials are not copied
into signed URLs. Range/If-Range are not signed, so a 307 client can preserve
them on retry. Each retry must start at the stable original URL.

## Rollout gate

This change requires the companion bunyi-app-control PR. Follow its
[owner-run rollout checklist](https://github.com/shaztechio/bunyi-app-control/blob/codex/authoritative-download-permission/docs/rollout.md).
The checklist covers live configuration inventory, quiescing old writers, preserving
ARMED/thresholds/audit history, explicit blocked initialization, control deployment
before signer cutover, and owner recovery. Old KV state is never permission input.
If migrating an existing kill, leave issuance blocked until deliberate recovery.

Use wrangler.staging.jsonc with a separate bunyi-models-staging bucket, isolated
control KV/object namespace, staging-only read credentials and test objects. It
binds r2-killswitch-staging#PermissionService and has no production routes. For the
second staging hostname, attach models-staging.bunyi.app only to the staging bucket
and route models-staging.bunyi.app/* to the staging signer. Production bucket
configuration does not authorize either staging hostname.

Record kill→503, rearm→307, missing/broken binding→generic 503, GET/HEAD,
range/resume and expiry behavior with the native probes and known fixture hashes.
Measure added authorization p50/p95 latency, errors/timeouts and request/storage
cost; confirm the current account plan and binding support. No live staging or
production activation is established by unit tests or the historical measurements
below. Stage after both PRs are reviewed; do not kill production for a test.

Rollback starts with issuance paused. Keep routes in place and preserve a blocking
gate, authority data and migration metadata. Removing the route may restore public
R2 delivery. Never enable an old KV-only signer while assuming the Durable Object
block protects it. Repair forward or deploy a compatible version, verify the same
authority, then perform explicit owner recovery before unpausing.

## Native compatibility

The HTTP contract and source identity remain unchanged. Existing .NET
DownloadHttp/DownloadServiceTests distinguish paused and unavailable responses and
retain downloaded files. On current main, macOS ModelFileTransfer accepts 200/206,
preserves partial files on non-success and uses the original URL on retry; it
handles 503 generically and does not yet expose the paused marker distinctly.
That existing UI parity gap is tracked in the companion PR, not claimed solved by
this server change. No on-disk DATA-FORMATS change is needed. Full macOS native
verification requires the owner/CI macOS host; Windows cannot run URLSession.

## Verification tools

The native probes use the same HTTP stacks as the apps, without changing
settings or model caches. .NET streams into an incremental hash, explicitly
cancels after a partial receipt, and resumes through a fresh redirect.
URLSession verifies full downloads and exact ranged bytes against the file;
it does not claim to test the app's Stop button.

Run from this directory:

```sh
dotnet run --project verify/dotnet -- https://bunyi-downloads.bunyi.workers.dev onnx/customvoice/vocoder.onnx.data f4cd93d2b48b833a6aaca7d5a3c95dd99853baba565514cb91777e3ce3c4cc8d --expiry
swift verify/urlsession.swift https://bunyi-downloads.bunyi.workers.dev onnx/customvoice/vocoder.onnx.data f4cd93d2b48b833a6aaca7d5a3c95dd99853baba565514cb91777e3ce3c4cc8d
```

The optional .NET expiry check takes about five minutes. Signed URLs remain
in process memory and are never printed. CI runs the unit tests/build, a
small-file live .NET probe on Windows/Linux, and both complete affected
vocoder downloads with URLSession on macOS. The live probes require the
test Worker and published objects to remain available.

## Initial test deployment: 11 September 2026

Deployed version: `305bc2a3-e4c4-4d62-900f-e56995c11970`, test hostname only.
On the Singapore Windows machine, .NET measured these complete transfers:

| File | Bytes | Full GET | Stop/resume |
| --- | ---: | ---: | ---: |
| Preset vocoder | 456,261,632 | 23.01 s / 19.83 MB/s | 10.52 s / 43.35 MB/s |
| Clone vocoder | 912,219,264 | 41.69 s / 21.88 MB/s | 24.09 s / 37.87 MB/s |

All four runs matched their published SHA-256. These are individual
measurements, not guaranteed throughput. The five-minute expiry check also
passed: the stale signed URL returned 403, and the original Worker URL
supplied a fresh 206 range response with the requested offset and length.

## References

- [R2 presigned URLs](https://developers.cloudflare.com/r2/api/s3/presigned-urls/)
- [Cloudflare's aws4fetch example](https://developers.cloudflare.com/r2/examples/aws/aws4fetch/)
- [Private service bindings](https://developers.cloudflare.com/workers/runtime-apis/bindings/service-bindings/)
