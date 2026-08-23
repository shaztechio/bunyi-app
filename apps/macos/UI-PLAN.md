# Making the macOS app look like it was designed

A staged plan. Each stage is one PR, independently shippable, in impact
order. Written against the app as of `93c3eaa`.

Behaviour is governed by [`/spec/FEATURES.md`](../../spec/FEATURES.md), not by
this document. Where a visual change would require a behaviour change, that is
called out — see [Behaviour changes in visual clothing](#behaviour-changes-in-visual-clothing)
at the end, which is the most important section here.

## The diagnosis

Four of the six problems are **layout bugs, not styling preferences**.

### 1. The options rows are centered

`optionsCard` is a `VStack(spacing: 0)` — default `.center` alignment — and
`optionRow` builds an `HStack` with no trailing `Spacer`. The Style row
contains a greedy `TextField`, so it fills the card and sets the VStack's
width. Every row *without* a greedy control — Language, Speaker — is then
horizontally centered inside it.

So "Language / Auto" and "Speaker / Ryan" float mid-window while "Style"
starts at the left edge. `rowDivider`'s `.padding(.leading, 40)` is an inset
computed from the left-aligned geometry (12 padding + 18 icon + 10 spacing)
and now lines up with nothing. Present in all three modes, since Language is
always first and always centered.

This is the single most visible defect. Nothing else on this list will read as
an improvement while it stands.

### 2. The "card" is not a card

`optionsCard` fills with `.controlBackgroundColor`, which in light appearance
is indistinguishable from what sits behind it — leaving three dividers hanging
in white space with no left or right edge. `textCard` has the same problem
with `textBackgroundColor`, defined only by a `.quaternary` hairline. Two
cards, neither of which reads as one.

### 3. Roughly 115 pt of dead space at the bottom

`textCard` is capped at `maxHeight: 220`, the options card is intrinsic, and
the outer `VStack` is `.frame(maxHeight: .infinity, alignment: .topLeading)`.
Everything piles at the top.

Compounding it: `BunyiApp.swift` gives `.defaultSize` to the **Logs** window
but not to the main `WindowGroup`, so the primary window opens at whatever
SwiftUI computes from `minWidth: 620, minHeight: 580`.

### 4. The app has no brand colour

There is no `AccentColor` colorset in `Assets.xcassets` and no
`ASSETCATALOG_COMPILER_GLOBAL_ACCENT_COLOR_NAME` in `project.yml`. So
`Color.accentColor` — the playback bar, History's ring — and
`.borderedProminent` on Generate all render in **whatever accent the user has
set in System Settings**.

The brand indigo and violet exist in exactly two places, neither of them the
UI: `tools/generate-icon.swift` (`0.36, 0.33, 0.96` and `0.63, 0.24, 0.89`)
and `docs/index.html` (`--indigo: #5c54f5`, `--violet: #a03de3`). The values
match each other exactly. The app was simply never told.

### 5. The mode picker's icons never render

`modeBar` builds `Label(mode.rawValue, systemImage: mode.symbol)`, but macOS's
segmented picker takes the title and discards the image. `TTSMode.symbol` is
dead code as far as this window is concerned.

Beside it, the Logs and Help buttons are `.plain` + `.secondary`: two
unlabelled grey glyphs at the far edge of an otherwise empty row, baseline
-aligned to a caption. They read as artifacts rather than affordances.

### 6. No typographic hierarchy

Across the whole main window: `.body`, `.callout`, `.caption`, `.caption2`.
Not one heading, not one weight above `.medium`, not one uppercase label —
every string is 12–13 pt regular grey. Combined with the centered rows, the
eye has nothing to anchor on.

Smaller items in the same family: the disabled `.borderedProminent` Generate
renders white-on-40%-blue, illegible, and it is the resting state on first
launch; the status line is `.tertiary`, the lowest-contrast style SwiftUI
offers, for the app's only feedback channel; and the spacing constants in
`ContentView.swift` alone run 2, 3, 6, 8, 10, 12, 13, 16, 18, 20, 40, 76,
110, 130.

## Borrowing the website's identity

`docs/index.html` is dark-only (`--bg: #0c0b14`). The app must support
System/Light/Dark (`FEATURES.md` §7), so **do not port the surfaces.** Port
these, which are appearance-independent:

| Website | App |
|---|---|
| `--indigo: #5c54f5` | `AccentColor` — Any `#5C54F5`, Dark `#7B72F0` (the site's own lifted variant) |
| `.btn-primary` gradient | Generate, and nothing else that is a button |
| `.steps li::before` badges | ~~Progress surfaces~~ — dropped in Stage 4; the accent already brands them, and a gradient on every surface stops meaning "this is the action" |
| `.eyebrow` | Section labels: 10 pt bold, uppercase, `.tracking(0.9)`, accent |
| `--radius: 14px` | 12 pt cards, 8 pt inner controls, capsule for pills |
| `.callout` left rule | Long explanatory prose in Settings; every inline error |
| `.tag` pill | History's mode label, replacing the `" · "`-joined string |
| `letter-spacing: -0.02em` | `.tracking(-0.3)` on anything ≥15 pt semibold |

**Spacing scale:** 4 / 8 / 12 / 16 / 20 / 24. 20 is the window inset (macOS
convention), 16 between cards, 12 between rows within a card, 8 between a
control and its caption, 4 inside a label/value pair.

**Type scale**, in macOS points:

| Role | Spec |
|---|---|
| Mode subtitle | 12 secondary — the only line under the picker |
| Script editor | 15 regular — it is the content, it should not be 13 |
| Row label | 13 secondary, fixed 76 pt column (unchanged) |
| Section eyebrow | 10 bold uppercase, `.tracking(0.9)`, accent |
| Status line | 12 **secondary, not tertiary** |
| Metadata / counters | 10 tertiary, `monospacedDigit` |

## The stages

### Stage 1 — Fix the layout bugs · ½ day

`ContentView.swift`, `BunyiApp.swift`

- `optionRow`: append `Spacer(minLength: 0)`. Remove the now-redundant
  explicit `Spacer()` calls in the Voice-clone rows so every row is built the
  same way.
- Give both cards a real edge: `RoundedRectangle(cornerRadius: 12)` background
  plus a `.strokeBorder(Color.primary.opacity(0.08))`. **The border is the
  load-bearing part** — the fill difference is not visible in light
  appearance. Unify both radii to 12 (currently 10).
- Kill the void: `textCard.frame(minHeight: 160, maxHeight: .infinity)`, drop
  the outer `alignment: .topLeading`.
- `.defaultSize(width: 760, height: 680)` on the main `WindowGroup`.

No behaviour change. No parity risk — plain box model, native in Avalonia.

> **Done, and seen running.** Both cards carry the border, the text card fills
> the space down to the option rows, and the window opens at 760x680 rather
> than at its minimum.

### Stage 2 — Give the app its own colour · ½ day

New `Assets.xcassets/AccentColor.colorset`, `project.yml`

- `AccentColor.colorset`: Any `#5C54F5`, Dark `#7B72F0`, sRGB.
- `project.yml` → `ASSETCATALOG_COMPILER_GLOBAL_ACCENT_COLOR_NAME:
  AccentColor`. **Required** — without it the colorset is inert.
- Comment the colour values with *why*: they are the same two sRGB values
  `tools/generate-icon.swift` renders the icon from and `docs/index.html`
  declares. Three copies of a brand colour is already one too many; the
  comment is what stops it becoming four.

One colorset re-tints the segmented selection, Generate, the playback bar,
History's ring, focus rings and every Settings picker. No behaviour change,
and it makes parity *easier* — the .NET app can read the same hex from the
spec instead of inheriting a different OS accent.

> **Done, and seen running.** The indigo is on the selected segment, on
> Generate, on History's play ring and on the Settings pickers — in a session
> whose system accent is something else, so the colorset is being read rather
> than inherited.

> **`Theme.swift` was deliberately left out of this stage**, though earlier
> drafts of this plan put it here. Its gradient, radius and spacing constants
> are not referenced until Stage 3, and shipping constants nothing uses is
> scaffolding that ages badly — it invites drift between the constant and the
> literal that someone writes instead. It arrives in Stage 3, with its first
> caller.
>
> When it does: **`project.yml`'s `sources:` list is explicit.** Add
> `Theme.swift` to it and re-run `xcodegen generate`, or it silently will not
> compile in. (`Assets.xcassets` is listed as a folder, so the colorset needed
> no such entry.)

### Stage 3 — Type and spacing on the main window · 1 day

Apply the scales above. Status line `.tertiary` → `.secondary`. Derive the
editor placeholder inset from the editor's own padding plus `NSTextView`'s 5 pt
container inset rather than the hand-tuned 16/13 that does not sit on the
caret. Give the character counter `monospacedDigit` so it stops reflowing as
you type.

> **A mode heading was tried here and removed. Do not re-add it.** This plan
> originally called for a 15 pt semibold title above the subtitle, on the
> grounds that the window had nothing for the eye to land on first. Built, it
> read as clutter: the segmented control directly above already names the
> mode, so the result was three restatements stacked —
> *Voice clone* / *Clone a voice from a clip* / *Copy a voice from a short
> reference clip.*
>
> **The picker is the heading.** A macOS window with a segmented control and
> one line of explanation under it is a finished pattern, not a missing one.
> The diagnosis was right that the window lacked hierarchy; the remedy was
> wrong, because it added a level that duplicated the level above it. What
> actually supplies hierarchy here is contrast between chrome and content —
> the 15 pt editor, the accent, and Stage 4's button — not another line of
> prose.

> **Done, and seen running.** The window shows the segmented control, one line
> of explanation, then the editor. No mode heading, and none missed. The
> character counter reads *31 characters* and holds its width as it changes.

### Stage 4 — Generate/Stop and the bottom bar · 1 day

An `ActionButtonStyle` with two roles: brand-gradient capsule for Generate,
`.quaternary` fill with a `.secondary` label when it is unavailable, and a
filled red capsule for Stop. The old disabled state was white-on-pale-blue —
illegible, and the first thing a new user sees.

*This originally said "keep Stop at the same `minWidth: 110` and
`.controlSize(.large)` so the swap shifts nothing". Both buttons share one
style now, which is a stronger guarantee than matching two — see the note
below.*

**Contains a behaviour change — do not include it here.** §1 says Generate
"says why on hover". Restyling the disabled state is fine; surfacing
`generateBlockedReason` as a visible line is a behaviour change and needs the
spec edited first. It is probably the right change for a non-technical
audience — a tooltip on a disabled button is close to undiscoverable — but it
is a separate PR.

> **Done.** The gradient landed on this button only, not on progress surfaces
> as the identity table originally said.
>
> **Stop changed too, against this plan's instruction to leave it alone.**
> Keeping it `.bordered` with `.tint(.red)` was fine while Generate was also a
> system button. Once Generate became a solid gradient capsule, the outline
> beside it read as barely there in light appearance and all but vanished
> against a dark `.bar` — reported from use, not caught here. The button that
> abandons a running job is the last one that should be hard to find.
>
> So both share one `ActionButtonStyle`, differing only by role: gradient for
> Generate, filled red for Stop. That settles by construction the thing this
> note previously listed as unverified — the two cannot differ in size, since
> they are one style with one set of metrics.
>
> Verified in both appearances by probing the two states that cannot be reached
> from outside the app — the busy branch, and `preferredColorScheme` — then
> reverting the probes. Worth knowing for later stages: a probe build left
> running looks exactly like a bug. This one had Stop showing on launch and was
> reported as backwards behaviour before the clean build replaced it.

### Stage 5 — The header row · ½ day

Move Logs and Help into `.toolbar { ToolbarItemGroup(placement: .primaryAction) }`.
They stop being floating glyphs and get the platform's treatment — and a
toolbar sits outside the `.disabled(engine.status.isBusy)` scope **by
construction**, which is exactly the invariant the existing comment protects
and §2 requires. That makes the constraint structural instead of something to
remember.

Delete `systemImage:` from the `Picker` labels, replacing the comment with why
(see diagnosis 5).

**Parity:** Avalonia has no native window toolbar; the .NET app keeps these in
a header row — i.e. what macOS has today. Permitted divergence
("platform-specific mechanics… never change the observable behavior"), but
worth one sentence in the spec so it is not later read as drift.

**Do not** relitigate History-as-a-fourth-segment. §2a pins it, and any custom
control that separates it visually is a spec change *and* a parity liability.

> **Done, and seen running.** The toolbar carries four buttons — Settings,
> Doctor, Logs, Help — and the picker labels are text alone. History is still a
> segment beside the three modes, as §2a requires.

### Stage 6 — History · 1–1½ days

Row hierarchy: title 13 semibold; a `.tag` pill for the mode, then voice · date
at `.caption`, size at `.caption2`. The current `" · "`-joined string gives four
values identical weight, which is why the list reads as noise. Make all four
row buttons icon-only — `Label("Download", systemImage:)` renders icon *and*
text beside three icon-only buttons, so every row has one wide odd element.
The trash glyph is unconditionally red, giving a red column down the list;
make it `.secondary`, red on hover — the confirmation dialog is what protects
the file. Replace `listRowInsets` + `.padding(.vertical, 2)` with one 44 pt
minimum row height.

**Three things §2a pins:** the ring-as-progress play button, play/**stop** with
no pause, and Copy details acknowledging the copy. Also **do not** hide the
row buttons behind hover — tempting, and wrong here: §2a enumerates them as
affordances for an explicitly non-technical audience.

Note that §2a's opening paragraph says "play/pause per row", contradicting its
own detailed bullet ("play/stop … No pause", with reasons). The detailed
bullet is what the app implements and is the intent; the summary line is
stale. Do not read the opener as licence to add a pause.

> **Done, and seen running** — the *source-only* caveat in the heading above no
> longer applies. Rows read title, then a mode pill, then voice · date · size.
> All four row buttons are icon-only, the trash is secondary rather than red,
> and the rows alternate at a comfortable height.
>
> The three §2a pins were exercised rather than read: pressing play turns the
> triangle into a ring with a stop square inside it — progress on the ring, no
> pause anywhere — and Copy details answers with a green tick before returning
> to its icon.

### Stage 7 — Settings · 1–1½ days

The problem is structural, not stylistic: Models has three fields followed by
~nine lines of caption prose, Storage eight more. It reads as a README with
text fields in it. Wrap each explanatory block in the ported `.callout`
treatment — 3 pt accent left rule, `.quinary` fill — which separates
"explanation" from "control" without shortening a word. Give every `Section`
an `.eyebrow()` header; Models and Storage currently have none on their first
section. Unify radii (6 → 8/12). Give the four bare-red error strings the
callout treatment with an `exclamationmark.triangle`.

**Behaviour, if the prose stays.** Moving the long explanations into the help
book — `HELP.md` is already built and shipped — with a "Learn more" would be
the better product, but §7 specifies "the three per-mode source fields + help".
Spec first, separate PR.

> **Done.** Not every section got an eyebrow, against this stage's
> instruction. Models and Storage did — those are the two the stage names, and
> the two with more than one section. General and Backup have exactly one
> unnamed section each, holding a row already labelled *Appearance* and a pair
> of buttons already labelled *Back up* and *Restore*, under tabs already
> called General and Backup. A header there restates a name that is on screen
> twice, which is Stage 3's mode-heading mistake in a smaller window.
>
> Three of the four bare-red error strings are in `SettingsView.swift` and got
> the treatment. The fourth, `voiceError`, is in `ContentView.swift` and was
> left for that view's own change rather than reached into from here.
>
> **Seen running.** Both questions this note left open are answered, on a Mac
> with Accessibility granted so ⌘, is reachable.
>
> **The accent eyebrow survives the `Form`'s own header slot.** MODEL SOURCES,
> CONFIGURATIONS, LOCATION and DOWNLOADED MODELS all render in the accent,
> uppercase and tracked. macOS does not restyle them.
>
> **`.quinary` reads as nothing.** In light appearance the fill under a callout
> is not perceptible; what separates the block from the surface is the accent
> rule alone. So the contingency this note anticipated is the actual state —
> and it is the reason to leave the rule at 3 pt rather than trimming it as
> decoration later.
>
> Backup carries no eyebrow, matching the decision recorded above rather than
> contradicting it: one unnamed section under a tab already called Backup.

### Stage 8 — Logs · ½ day

Each line is a single interpolated `Text("\(time)  \(message)")`, so the
timestamp column cannot align and a long message wraps *under* the timestamp
instead of hanging-indented. Split into an `HStack`: timestamp in a fixed
62 pt `monospacedDigit` `.tertiary` column, message in a `.leading` frame.
`LogStore.text` builds the clipboard string the same way — **the copy format
must not change**, so this is presentational only.

**Out of scope:** severity colours. `LogStore.Entry` has only `date` and
`message`; adding a level touches every `log()` call site and changes §8.

> **Done, and seen running** — but it took two goes, which is the part worth
> keeping.
>
> The `HStack` this stage prescribed did fix the wrap. #48 then replaced the
> whole view with an `NSTextView` so a selection could span lines, and the
> hanging indent went with it: with no `NSParagraphStyle`, `headIndent` is 0
> and a wrapped line returns to the left margin — under the timestamp, the
> state this stage was written to end. It survived review because #48 was read
> for selection, which it does correctly, and nobody asked what it cost. #137
> restored it with a paragraph style whose indent is derived from the same
> column count the padding uses, so the two cannot drift.
>
> The rest of the stage held throughout: the timestamp is its own fixed, dimmer
> column, and the copy format never changed.

### Stage 9 — First-run state · optional, spec first

The app's first frame for its stated audience is an empty box, a tertiary grey
"Ready" line, and an illegible disabled button. Two or three clickable example
prompts when `text.isEmpty` and nothing has been generated would fix it.

**This is a new feature**, so `FEATURES.md` gets it first and both apps
implement it. Listed last for that reason, not because it is low value — for a
non-technical first-time user it is probably the highest-value item here.

> **Done**, spec first: `FEATURES.md` §1 now carries the behaviour, and the
> .NET app inherits it from there.
>
> Three decisions this section did not pin. **The examples differ per mode**,
> because the modes do not want the same thing: preset voice offers sentences
> and fills the script, voice design offers voice *descriptions* and fills
> `instruct` — the field that mode adds, and the one whose shape nobody
> guesses — so a design example deliberately leaves the script empty.
> **Voice clone gets none at all**: what it is missing on a first run is a
> reference recording, which no shipped example can be, so filling its script
> would leave Generate exactly as unavailable and misdirect the user about
> why.
>
> **"Nothing generated yet" is a real condition, not a restatement of
> `text.isEmpty`.** Clearing the box after a run leaves the result in the
> bottom bar, and suggestions beside audio the user just made read as the app
> forgetting what it did — so the strip also requires `lastOutputURL == nil`.
>
> The chips sit inside the editor's `.disabled` group rather than carrying
> their own copy of the condition, which is the same by-construction argument
> Stage 5 made for the toolbar, pointed the other way: inputs belong inside the
> scope, always-available actions outside it.

## Behaviour changes in visual clothing

Each of these looks like styling and is not. All need `spec/FEATURES.md`
updated first, and both apps to follow.

1. **Surfacing `generateBlockedReason` inline** — §1, verbatim: "Generate is
   unavailable until the mode has what it needs, **and says why on hover**".
2. **Hover-revealed History row buttons** — §2a enumerates play, Download,
   reveal-in-file-manager and Copy details as row affordances for an
   explicitly non-technical audience. It does not literally say they must be
   *visible*, so this one is an inference rather than a quotation — but hiding
   four enumerated affordances behind hover is a change to argue in a PR, not
   to slip into a tidy-up.
3. **Moving Settings prose into the help book** — §7, verbatim: "**Models**:
   the three per-mode source fields (repo ID or base URL) + help".
4. **Log severity colours** — §8 specifies "timestamped, selectable,
   monospaced lines" and has no concept of a level; `LogStore.Entry` carries
   only `date` and `message`. Adding one changes both.
5. **First-run example prompts** — a new feature. Nothing in the spec covers
   it, which is the point: it needs adding there first.
6. **Any custom mode control** separating History from the three modes — §1
   opens "A segmented picker selects one of three modes", §2a opens "A fourth
   segment beside the three generation modes".
7. **Adopting the website's dark palette wholesale** — §7 specifies
   System/Light/Dark under **General**, and says why it constrains design:
   "an app with three appearance states cannot be designed against one fixed
   palette. Any colour that only works on one background is a bug in the other
   two."

The identity transfers as accent, gradient, eyebrow, callout and pill. It does
not transfer as a background colour.

> Checking these citations turned up two spec defects, both since fixed in
> #33: the appearance setting was implemented and shipped but unspecified —
> which under the parity rule meant the .NET app had no way to know it needs
> one — and §2a contradicted itself, its opening paragraph saying "play/pause
> per row" against a detailed bullet specifying "play/stop … No pause".

## Parity notes

- **SF Symbols** have no Avalonia counterpart. Already a live problem rather
  than one these stages create — Stage 5 is the natural moment to add a
  symbol→icon mapping table to the spec.
- **`.bar` material** and **`.toolbar`** have no exact Avalonia equivalent.
  Idiom divergence; one spec sentence each.
- **`.formStyle(.grouped)`** and **`.alternatingRowBackgrounds()`** are
  macOS-only and hand-built on the other side.
- Everything in Stages 1–4 — box model, colours, gradients, type, spacing —
  maps one-to-one. That is deliberate: the highest-impact stages carry the
  least parity debt.
