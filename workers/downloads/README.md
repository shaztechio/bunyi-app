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

The Worker reads the existing `KILLSWITCH_STATE` KV binding's
`killswitch:state` record. Only an explicit `killed: false` permits signing.
Missing state, missing configuration and read errors fail closed.
It never writes to this KV namespace. `DOWNLOADS_ENABLED=false` also
stops new links. KV changes can take time to propagate; this is not an
instant revocation mechanism. Previously issued links remain usable until
expiry, and in-progress transfers may continue afterwards.
The existing hostname WAF/domain disable alone does not revoke S3 links.
Monitor S3 reads as well as public-domain traffic in the existing cost monitor.

Paused responses carry HTTP 503, `Retry-After: 60`, and
`X-Bunyi-Download-Status: paused`. Configuration or KV read errors remain generic
503s. The app handles older deployments without the marker as unavailable and
still offers an explicit Hugging Face source switch. Deploy the Worker change
after review to enable the more specific paused wording; this does not require
new secrets, bindings, or routes.

`keep_vars` preserves dashboard variables during deployment, while explicitly
configured variables are still applied. If emergency-paused in the dashboard,
update the checked-in configuration before deploying again; the supplied
`DOWNLOADS_ENABLED=true` would otherwise re-enable issuance. Secrets are
preserved by Wrangler. Observability remains enabled, but the Worker never
logs signed URLs, credentials, or exception details.

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

Keep the production R2 custom domain unchanged until validation succeeds:

1. GET and HEAD redirect correctly, including a 206 response for Range.
2. Download the whole affected vocoder file, verify its published SHA-256,
   and measure sustained throughput; a fast small range is not sufficient.
3. Interrupt a transfer, then request the remaining bytes through the
   original Worker URL. Check Content-Range and the final combined hash.
4. Repeat a resumed request after the first signed link expires; an expired
   link should fail while the original Worker URL supplies a fresh one.
5. Verify both .NET HttpClient (Windows/Linux) and URLSession (macOS).
6. Verify the killed/error states in unit tests; do not trigger the real
   production kill switch just to test the new Worker.

These checks passed before production activation. Existing model paths,
folders and source settings remain unchanged; the R2 custom domain and DNS
remain in place underneath the Worker route. New or resumed requests use
the redirect immediately; an already-open download requires reconnecting.

For rollback, remove only the `models.bunyi.app/*` Worker route in the
dashboard and remove the same route from this configuration before another
deployment. That restores the previous R2 public delivery path. Do not
delete the bucket custom domain or DNS record. A rollback is not an emergency
stop: set `DOWNLOADS_ENABLED=false` or use the existing kill switch to stop
new signed links. The Worker consults its KV state even if the old domain
disable action alone would not intercept a Worker route.

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
- [KV consistency](https://developers.cloudflare.com/kv/concepts/how-kv-works/#consistency)
