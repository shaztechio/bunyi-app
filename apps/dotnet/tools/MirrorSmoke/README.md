# Model mirror smoke test

After the workflow is merged to `main`, open **Actions → Model mirror smoke
test → Run workflow**. It is manual only and runs on `ubuntu-latest`, using
an empty temporary directory and the production URLs from Bunyi.Core.
No secrets, saved settings, model cache, or Hugging Face fallback are used.

For each of preset, design and clone, the tool uses the app's
`ModelDownloader.ResolveFileListAsync` to obtain the manifest and requires
checksums for every required file. It uses `HttpFileDownloader.SizeOfAsync`
and `FetchAsync` to download and checksum-verify a small config file, then
starts a real weight download and cancels after just over 1 MiB has arrived.
It checks that cancellation retains an incomplete file. The temporary
directory is deleted at exit. Application code and behavior are unchanged.

Expect roughly 3–6 MiB of weight bodies across all modes, plus small configs
and manifests; network buffering can receive additional bytes. The tool has
a five-minute deadline. The workflow has a ten-minute limit including build.
Failures produce a nonzero exit code; each mode gets a PASS/FAIL line.

The log records UTC start time, original URLs, final status/host, response
timing, received bytes and selected diagnostic headers. Redirect handling
uses the standard HttpClient handler, like the app. Successful redirects'
intermediate headers are not exposed; logged headers belong to the final
response. Signed URL query strings and raw exception text are never logged.

Interpret failures using the HTTP receipt: 403 may indicate an access rule;
429 indicates rate limiting; 503 with `X-Bunyi-Download-Status: paused`
indicates the deliberate pause gate. A generic 503 can indicate a permission
service or configuration failure. DNS/TLS/network failures are classified
separately. Correlate UTC time and CF-Ray with Cloudflare events.

A pass proves these downloads work from that Linux runner. It does not test
Windows Store/MSIX installation, UI interaction, every object, complete weight
checksums, resume, inference, or access from every IP/region. The existing
Worker verification tools cover full-file and resume behavior separately.

Local equivalent (requires .NET 10; runs on Windows or Linux):

```sh
dotnet run --project apps/dotnet/tools/MirrorSmoke -c Release
```
