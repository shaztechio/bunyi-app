# docs/ — the bunyi.app website

The GitHub Pages site for Bunyi. The published page is static HTML with inline
CSS; visitors do not need JavaScript to use its download or disclosure controls.
`index.html` is a template: Python fills its two release-version placeholders
before deployment. No browser-side GitHub API calls are needed.

## Publishing

**Settings -> Pages -> Build and deployment -> Source: GitHub Actions.**
`.github/workflows/pages.yml` builds and deploys the site from `main`:

- README, site, renderer, or workflow changes on `main` rebuild the page.
- Successful **Signed release** and **Windows + Linux release** workflow runs
  explicitly dispatch it after the release assets have uploaded. Their
  **Refresh website** jobs record that handoff. `workflow_dispatch` works with
  `GITHUB_TOKEN`, so no extra access token or release-event delivery is needed.
- A stable release published manually queues the same workflow on `main`,
  preserving the Pages environment's main-only deployment policy.
- **Actions -> Website -> Run workflow**, on `main`, refreshes it on demand.
- Pull requests test and build a preview artifact; they never deploy it.

The build reads published GitHub releases and picks the highest complete stable
version independently in `v*` (macOS) and `dotnet-v*` (Windows/Linux). It ignores
drafts, prereleases, and incomplete asset uploads. macOS needs a matching DMG,
ZIP, and checksum file; Windows/Linux need both standard and CUDA archives with
their checksums. An older maintenance release cannot replace a newer version.
If either family has no complete release, the build fails and the deployed site
stays in place. A successful build-only release run simply redeploys the current
published versions.

Only the deployment job has Pages write permissions. Release-triggered builds
read the current `main` website, not the release tag's older website or artifacts
from the triggering workflow. Version updates create no repository commits.

The build also generates `releases/macos.svg`, `releases/windows.svg`, and
`releases/linux.svg` from the same selected versions. The root README embeds
these badges and links to the website's platform-specific download sections.
Each release refresh therefore updates the README's displayed versions along
with Pages, without editing the Markdown or opening a PR. No PR creation or
approval permission, extra token, or organization setting is required.

GitHub caches external README images, so a badge can briefly show an older
version after deployment. The permanent download links lead to the current
website regardless of that cache. See [GitHub's image caching documentation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/about-anonymized-urls).

## Local preview

From the repository root, with Python 3 and the GitHub CLI:

```sh
mkdir -p artifacts/site-preview
gh api --paginate --slurp 'repos/shaztechio/bunyi-app/releases?per_page=100' > artifacts/site-preview/releases.json
python3 tools/build_site.py --releases-json artifacts/site-preview/releases.json --output artifacts/site-preview/public
```

Open `artifacts/site-preview/public/index.html` in a browser. The API response
can be reused offline. Run the selection/rendering regression tests with:

```sh
python3 -m unittest discover -s tools -p 'test_build_site.py' -v
```

`CNAME` claims the **bunyi.app** custom domain. It only takes effect once
DNS points at GitHub Pages — apex `A` records to `185.199.108.153`,
`185.199.109.153`, `185.199.110.153`, `185.199.111.153` (plus the `AAAA`
equivalents), and a `www` `CNAME` to `shaztechio.github.io`. Until then the
site is reachable at `https://shaztechio.github.io/bunyi-app/`; delete
`CNAME` if you'd rather stay on that URL.

## Contents

| Path | What it is |
|------|-----------|
| `index.html` | The whole site — hero, modes, features, platform status, build steps |
| `assets/icon.png` | 1024px app icon — used by the **repository** README, not by this page |
| `assets/icon-256.png` | 256px copy: favicon and `apple-touch-icon` |
| `assets/icon-512.png` | 512px copy, the hero image |
| `assets/icon-64.png` | 64px copy, the wordmark in the header and footer |
| `assets/og-card.png` | 1200×630 link-preview image (`og:image`) |
| `tools/generate-og-card.swift` | Renders `og-card.png` |
| `CNAME` | Custom domain |
| `.nojekyll` | Serve files as-is |

## Analytics

`index.html` loads [PostHog](https://posthog.com) to count visits to this
site. **The apps do not.** Bunyi runs offline once the models are down, and
instrumenting it would contradict the thing the page is selling — keep the
snippet in `docs/` and nowhere else.

The `phc_…` value in the snippet is a *public project key*. It belongs in
client-side HTML, it is write-only (events in, nothing out), and it is not
the personal API key that reads data back — that one never lands in the
repository.

Analytics go through **`t.shaztech.io`**, a managed reverse proxy in front
of PostHog's US region, rather than straight to `us.i.posthog.com`. One host
now, not two: the snippet derives the asset URL by replacing `.i.posthog.com`
with `-assets.i.posthog.com` inside `api_host`, and a proxy domain does not
contain that substring — so the replacement does nothing and `array.js`
comes from the proxy as well. That only works because the proxy serves
`/static/` too; a proxy that forwarded only the ingest endpoints would load
nothing at all.

`ui_host: 'https://us.posthog.com'` exists solely because of the proxy. Without
it, links PostHog generates back into its own UI would point at the proxy
domain, which does not host a UI.

The domain is one this project controls rather than PostHog's, so a blocklist
keyed on `posthog.com` no longer matches. It is **not** first-party to the site
in the sense that matters for cookies: `t.shaztech.io` and `bunyi.app` are
different registrable domains, so a browser treats the proxy as third-party
exactly as it treated `us.i.posthog.com`. A CSP still applies, and nothing else
on the page depends on the script — `p.onerror` gives up quietly and the site
renders the same either way.

`posthog.init` carries two flags that exist only to stop PostHog fetching
modules this page has no use for. Both were measured, not guessed — the
check is to load the page and look at what comes back from `t.shaztech.io`.

They are also the two lines missing from the snippet PostHog's dashboard hands
you, so pasting a fresh one over this file silently removes them and costs
every visitor about 40 KB again. They are kept on purpose.

- `disable_surveys: true` — surveys are enabled per PostHog project, and
  leaving them on made every visitor fetch a 33 KB `surveys.js`. Remove the
  flag if the site ever runs a survey.
- `capture_performance: false` — drops `web-vitals.js`. Real user timings
  are not why this page has analytics, and Lighthouse measures it properly
  on demand.

`capture_dead_clicks` is deliberately **not** set. It looks like it should
save the 7 KB `dead-clicks-autocapture.js`, and it does not: the project's
remote config already reports `captureDeadClicks: false`, and the module is
loaded by **heatmaps** instead. `enable_heatmaps: false` does remove it —
at the cost of the click and scroll maps for the landing page, which are
worth more than 7 KB of async script. Left on deliberately.

Cloudflare Web Analytics is *also* on this page — `beacon.min.js`, injected
by the proxy rather than by anything in this repository. So visits are
counted twice, by two systems. That is a Cloudflare dashboard setting, not
a code change; turn one off if one is enough.

## Link previews

`og:image` must be an **absolute** URL — scrapers do not resolve relative
ones — so every OpenGraph URL on the page points at `https://bunyi.app/…`.
They are only correct once the domain actually serves this site; until then
previews resolve to nothing, whatever the tags say.

Regenerate the card after changing the wordmark or tagline:

```sh
swift docs/tools/generate-og-card.swift    # from the repository root
```

It draws into an explicitly sized bitmap rather than `NSImage.lockFocus()`,
which would use the display's backing scale and quietly emit a 2400×1260
image that contradicts the declared `og:image:width`/`height`.

The icons are copies. If `apps/macos/tools/generate-icon.swift` changes,
re-copy them:

```sh
cp apps/macos/Assets.xcassets/AppIcon.appiconset/icon-512pt@2x.png docs/assets/icon.png
cp apps/macos/Assets.xcassets/AppIcon.appiconset/icon-128pt@2x.png docs/assets/icon-256.png
cp apps/macos/Assets.xcassets/AppIcon.appiconset/icon-256pt@2x.png docs/assets/icon-512.png
cp apps/macos/Assets.xcassets/AppIcon.appiconset/icon-32pt@2x.png  docs/assets/icon-64.png
```

There are four sizes because the page should send roughly what it displays.
The hero renders at 240 CSS px and the wordmark at 26, so 512 and 64 cover a
2× display with a little room and no more; serving the 1024px icon into a
240px box cost 166 KB to show 170 KB of nothing. Every one is a plain copy
of a file the icon generator already produces — resampling here would be a
image-processing build step. These assets stay pre-generated; the site build only fills release references.

Each `<img>` also carries `width`/`height` attributes. They do not size
anything (CSS does), they give the browser the aspect ratio up front so the
space is reserved before the file lands and the page below it never jumps.
Keep them in step with the file you point at.

## Keeping it honest

The site describes what the apps actually do, so it's downstream of the
spec like everything else — see [`/AGENTS.md`](../AGENTS.md). If a feature
changes in [`spec/FEATURES.md`](../spec/FEATURES.md), update the matching copy
here (the "Features", "Platforms", and "Get it running" sections).

This used to say "or the .NET app stops being a scaffold". It did, and nothing
here was updated — which is the argument for the sentence above rather than
against it.

## Release links and page controls

Use `{{DOTNET_VERSION}}` for Windows/Linux versions, release links, and archive
filenames, and `{{MACOS_VERSION}}` for macOS. The renderer fills every occurrence;
no manual version edit is needed when cutting a release. If release asset names
change, update `tools/build_site.py` and its tests to match.

The feature section shows its first six items and keeps the remaining items in
native `<details>`, accessible by pointer or keyboard with scripting disabled.
Screenshot, download, and code-signing sections share CSS for their OS pickers
but have separate radio names and IDs, so changing one does not change the others.
All start on the detected OS; without JavaScript they start on macOS. Use
`?os=mac`, `?os=win`, `?os=linux`, or `?os=none` to check the initial states.

## Screenshots

The screenshot picker has real captures for macOS, Windows, and Linux, with
platform-specific alternative text and captions. Only the selected image is
shown, and the narrower 1x Windows/Linux captures are not enlarged. Native radio
controls support keyboard navigation and work with JavaScript disabled.

`assets/screenshot-windows.png` was captured from the published CPU Windows
1.2.0 app on 2026-09-06, showing Preset voice in light appearance. The 762 x 712
capture includes the real Windows title bar and uses the visible window bounds,
excluding the invisible resize border. Its PNG URL replaces the older WebP
capture so cached copies of that image do not hide the update.

`assets/screenshot-linux.png` was captured from the published CPU Linux 1.2.0
app under WSLg on 2026-09-06, using a temporary clean profile and light appearance.
It shows the app's client area at its native 760 x 680 size, without adding a
simulated desktop frame. Screenshot assets are updated when the UI changes;
release-version rendering does not modify screenshots.
