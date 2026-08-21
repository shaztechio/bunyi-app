# docs/ — the bunyi.app website

The GitHub Pages site for Bunyi. Plain static HTML: one page, inline CSS,
no build step, no Jekyll (`.nojekyll`). Open `index.html` in a browser to
preview it exactly as it will publish.

## Publishing

GitHub serves this folder directly — no workflow needed:

**Settings → Pages → Build and deployment → Source: _Deploy from a branch_,
Branch: `main`, folder: `/docs`.** Every push to `main` that touches
`docs/` redeploys.

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
build step, and this folder deliberately has none.

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
