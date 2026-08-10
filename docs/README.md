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
| `assets/icon.png` | 1024px app icon, copied from `apps/macos/Assets.xcassets` |
| `assets/icon-256.png` | 256px copy, used as the favicon |
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

Analytics load from `us-assets.i.posthog.com` and send to
`us.i.posthog.com`, so a strict CSP or a blocklist will drop them. Nothing
else on the page depends on the script: `p.onerror` gives up quietly and
the site renders the same either way.

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
```

## Keeping it honest

The site describes what the apps actually do, so it's downstream of the
spec like everything else — see [`/AGENTS.md`](../AGENTS.md). If a feature
changes in [`spec/FEATURES.md`](../spec/FEATURES.md), or the .NET app stops
being a scaffold, update the matching copy here (the "Features",
"Platforms", and "Get it running" sections).
