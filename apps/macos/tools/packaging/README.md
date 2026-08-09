# Packaging: the disk image, its background, and the help book

How `Bunyi-<version>-<build>.dmg` is built, why it is built that way, and the
two caches that will convince you a change did not work when it did.

Everything here was learned by getting it wrong. The failure modes are silent:
no error, no log line, just artwork that does not appear or help text that does
not change. Read this before touching `make-dmg.sh` or `make-help.sh`.

## Building the image

```sh
./apps/macos/build-dist.sh Release             # ad-hoc signed, dist/macos/Bunyi.app
./apps/macos/tools/packaging/make-dmg.sh       # dist/macos/Bunyi-0.1.0-1.dmg
```

`make-dmg.sh` does this, and the order matters:

1. Stage `Bunyi.app`, an `Applications` symlink, the committed `.DS_Store`
   layout, and `.background/background.tiff` into a temporary folder.
2. Create a **read-write** (`UDRW`) image from the staging folder.
3. **Mount it.**
4. Build the background reference **against that mounted volume**
   (`make-background-alias.py`) and write it into the volume's own `.DS_Store`
   (`set-dmg-background.py`).
5. Detach, `hdiutil convert` to compressed `UDZO`.
6. Developer ID sign the image.

Steps 2–5 exist for one reason: **the background reference is bound to the
identity of the volume it was made against.** Building `UDZO` directly from the
staging folder is a one-liner and produces an image whose background never
draws.

## The background: what Finder actually needs

Finder stores the window's background picture in the `.DS_Store`'s `icvp`
record, as `backgroundImageAlias`, with `backgroundType = 2`. It is **not** a
relative path — it is a reference to a file on a specific volume.

Two things must both be true, and each was wrong in turn here:

**It must be a version-2 AliasRecord, not bookmark data.** A DMG known to
display its background was inspected and Finder had stored an AliasRecord
(first four bytes `00000000`, version `2` at offset 6). Bookmark data
(`book` magic) written into the same field did not draw, even though it
resolved to the correct file.

**Its volume identity must match the volume that ships.** An alias identifies a
volume by **name and creation date**. Copying a layout from another project and
rewriting only the strings leaves that project's creation date in place, so the
volume never matches. Creating the reference against a scratch image has the
same problem — with bookmark data it shows up as `stale: true` on resolution.

Both failures look identical from the outside: Finder falls back to
`backgroundColor` (white) and reports nothing.

### Checking it, without opening the image

```sh
hdiutil attach -nobrowse -readonly dist/macos/Bunyi-0.1.0-1.dmg
python3 - <<'PY'
import os, struct, plistlib
MAC = 2082844800   # seconds between the 1904 and 1970 epochs
d = open("/Volumes/Bunyi/.DS_Store", "rb").read()
alloc = struct.unpack_from(">I", d, 8)[0] + 4
addrs = struct.unpack_from(">%dI" % struct.unpack_from(">I", d, alloc)[0], d, alloc + 8)
off = (addrs[2] & ~0x1F) + 4
# ... walk records to the icvp blob, then:
#   embedded = struct.unpack_from(">I", alias, 38)[0]
#   actual   = int(os.stat("/Volumes/Bunyi").st_birthtime) + MAC
#   they must be equal
PY
hdiutil detach /Volumes/Bunyi
```

The check that matters is `embedded == actual`. "The alias is present" proves
nothing; presence was never the failing condition.

### Changing the window layout

Icon positions, window size, and icon size live in the committed
`dmg-layout.DS_Store` (`Iloc` records for positions, `bwsp` for the window
frame, `icvp` for view options). The background reference inside it is
**overwritten at build time**, so do not bother fixing it by hand.

To change positions: mount a read-write image, arrange it in Finder, and copy
the resulting `.DS_Store` over `dmg-layout.DS_Store`. Positions are keyed by
**filename**, so `Bunyi.app` and `Applications` must keep those exact names — a
layout naming something else positions nothing.

### `.DS_Store` internals, briefly

A `Bud1` buddy-allocator file holding a B-tree. Records live in fixed
power-of-two blocks (4096 bytes here). Two traps:

- **Blocks do not grow.** Bookmark data at ~1.6–2 KB does not fit alongside
  everything else; an AliasRecord at ~320 bytes does. If something must be
  dropped, `pBB0`/`pBBk` hold Finder window-browser state a disk image has no
  use for.
- **Counts are stored twice.** The node counts its own records, and the DSDB
  header counts them tree-wide. Removing a record without updating both leaves
  a file that parses until a reader walks past the last real record into zeroed
  slack — losing the whole layout, not just the background.

## The help book, and two caches that outlive rebuilds

`make-help.sh` renders `HELP.md` into `Bunyi.help` inside the bundle, as a
post-build script. Editing `HELP.md` and rebuilding is not enough for anyone to
see the change, because the book is cached in two places with **different
keys**:

| Cache | Path | Key |
|---|---|---|
| `helpd` index | `~/Library/Caches/com.apple.helpd/Generated` | book id + **book** version |
| Help Viewer content | `~/Library/Group Containers/group.com.apple.helpviewer.content/Library/Caches` | app id + book id + **app** version |

The second one is the one that bites. It is keyed on the **app's**
`CFBundleShortVersionString`, which does not move between development builds,
so Help Viewer keeps serving the copy it made the first time. No amount of
version-bumping inside the help book invalidates it.

So `make-help.sh` does both:

- mixes a digest of `HELP.md` into the **book's** short version, which
  invalidates `helpd`'s index; and
- **deletes** the Help Viewer copy outright, because there is no key to change.

### When help still looks stale

```sh
killall helpd "Help Viewer"
rm -rf ~/Library/Caches/com.apple.helpd/Generated/app.bunyi.help* \
       ~/Library/Group\ Containers/group.com.apple.helpviewer.content/Library/Caches/*app.bunyi.help*
```

Then check what is actually registered — **mounting a DMG registers the app
inside it**, and those registrations survive unmounting:

```sh
LSR=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
"$LSR" -dump | grep "path:.*Bunyi.app" | sort -u
```

Entries under `/Volumes/Bunyi/Bunyi.app` are phantoms from earlier test mounts.
`helpd` resolves a book by identifier and serves whichever registration it
indexed last, so a phantom pointing at a long-gone image will happily serve its
old help text. Clear them and re-register the real bundle:

```sh
"$LSR" -u /Volumes/Bunyi/Bunyi.app        # repeat per phantom
"$LSR" -f "$PWD/dist/macos/Bunyi.app"
```

To confirm the reader is getting the current text, check the copy Help Viewer
made rather than the one the build produced:

```sh
grep -c "<a phrase you just added>" \
  ~/Library/Group\ Containers/group.com.apple.helpviewer.content/Library/Caches/*bunyi*/Contents/Resources/en.lproj/Bunyi.html
```

## Signing

`make-dmg.sh` Developer ID signs the **image**. The app inside is whatever
`build-dist.sh` produced, which is ad-hoc signed — Gatekeeper rejects it, and
`make-dmg.sh` warns. For a distributable image run
[`sign-and-notarize.sh`](sign-and-notarize.sh) instead; it signs the app,
notarizes and staples it, rebuilds the image, and notarizes that separately.
See the "Releasing" section of [`../../AGENTS.md`](../../AGENTS.md).
