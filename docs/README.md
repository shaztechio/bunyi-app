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
| `CNAME` | Custom domain |
| `.nojekyll` | Serve files as-is |

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
