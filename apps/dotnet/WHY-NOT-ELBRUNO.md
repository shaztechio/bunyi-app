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

# Why preset voice no longer uses ElBruno.QwenTTS

The plain-language version. The measurements, tables and graph-level detail
are in [`RESEARCH-ONNX.md`](RESEARCH-ONNX.md); this is the story for someone
who wants to know *what happened and why* without reading any of that. It was
written the day the change landed, in [#179](https://github.com/shaztechio/bunyi-app/pull/179),
for [#178](https://github.com/shaztechio/bunyi-app/issues/178).

## The setup

Bunyi has three ways of making a voice: **preset voice** (pick a name from a
list), **voice design** (describe a voice in words), and **voice clone** (hand
it a recording). All three run the same kind of model. Two of them — design and
clone — ran on a driver we wrote ourselves, `TalkerLoop`. Preset voice, the
mode everyone meets first, ran on a driver we borrowed: the
[`ElBruno.QwenTTS`](https://github.com/elbruno/ElBruno.QwenTTS) library.

Think of it as three trips on the same road, two in a car we built and one in
a neighbour's. The neighbour's car got us there. It is the reason preset voice
shipped in 1.0.0 at all, and nothing here is a complaint about the loan. But
once we had built our own car, keeping the borrowed one turned out to cost
more than it gave.

## What was wrong with the borrowed car

Three things, none of them obvious until we measured.

**1. It used twice the memory.** A long paragraph in preset voice peaked at
13 GB. The same paragraph in voice design — a *bigger* model — peaked at
4 GB. On a 16 GB machine that is the difference between "fine" and
"everything else on the computer gets squeezed", and preset voice is the mode
a new user tries first. We assumed the bigger model must be the heavier one.
It was the other way round, and the weights on disk are nearly the same size,
so the extra memory could only be the driver.

**2. It threw the style box away.** Preset voice has a box for "how should
this be said" — *softly*, *cheerfully*, *like a bedtime story*. The library
had a rule that said the small preset model does not take instructions, so it
dropped whatever you typed. The model's own documentation says it *does* take
them, and the Mac app feeds them in. Bunyi was honest about it — it logged
that the style was ignored and left it out of the saved file — but honest
about a box that does nothing is still a box that does nothing.

**3. It could not say how far along it was.** No progress while it worked:
silence, then a result. The other two modes count frames as they arrive.

## What changed

We moved the preset trip into our own car. That turned out to be a small
change, because preset voice and voice design are almost the same recipe.
Voice design describes a voice in words; preset voice picks one from a list.
The only real difference is **one extra ingredient** — a "this is Ryan" marker,
which is just one row of a table the model ships — dropped into a slot the
design recipe leaves empty. Everything else is shared, and it is literally the
same code building both, so they cannot drift apart.

Everything else in the app stayed where it was: the same nine voices, the same
names, the same default (Ryan), the same files on disk.

## The gotcha

The first attempt worked, and was wrong.

The preset model ships a dictionary that is missing two special words the
recipe depends on — the markers for "start speaking" and "stop speaking". The
borrowed car had those two words hardcoded. Ours did not know them, so it
spelled them out letter by letter, like reading S‑T‑O‑P aloud instead of
stopping. The model got a recipe of exactly the right shape with nonsense in
two places, and it rambled: twice as long as it should for a short sentence,
and once it gave up after two seconds of a long paragraph.

The tests caught it on the first run, which is what they were for. Now we read
those two words from the model's own settings file, where they were all along,
and if a dictionary is ever missing them we refuse to start rather than quietly
speak nonsense.

## What we got

- **Half the memory.** 13 GB down to 7 GB on long text, 5 GB on short.
- **The style box works.** "Whisper, very softly" changes how Ryan talks — we
  checked with nothing left to chance, and it changed most of what he said.
- **A progress count** while it generates.
- **One driver for all three modes**, and one fewer dependency in the app.

## What we did not get

**Speed.** Preset voice is about as fast as it was — the same on short text,
a little slower on long. We had hoped for a lot better, because voice design is
twice as fast. It turns out that is because the design model is stored in a
compressed form (`int4`) and the preset model is not. The driver was never the
speed; it was only ever the memory. That is written down in the research notes
in the same breath as the win, so nobody expects the other half.

## Would we ever go back?

Only if the model changed underneath us in a way the library handled and we
did not. It is MIT-licensed, it did its job, and its author answered our
questions. The reason not to go back is not that it was bad; it is that a
second driver for the same road costs more to keep than the road is worth.
