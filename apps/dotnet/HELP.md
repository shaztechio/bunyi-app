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

# Bunyi Help

Bunyi turns text into speech on your own computer. Nothing you type and no audio you make is sent anywhere: the voice models run locally, and after the first download the app works with no internet connection at all.

## Saying the name

**Bunyi** is *BOON-yee*. The "ny" in the middle is a single sound, the one in the middle of *onion*, or the "ñ" of *jalapeño*; it is not "boon-yai" or "bun-yi".

The word is Malay and Indonesian for **sound**.

## What this version can do

This is the Windows and Linux version of Bunyi, and it is still being built. **Preset voice** works end to end. **Voice design** and **Voice clone** are not here yet, and neither are saved voices or backup — the macOS version has them, and they are coming.

Everything below describes what this version actually does today.

## Start here

1. Type or paste your text in the big box.
2. Click **Generate**.

The first time you generate, Bunyi downloads the voice model — about 5.9 GB — and shows a progress bar with an estimate of the time remaining. This happens once. Every generation after that is offline and much faster.

When generation finishes the audio plays automatically. **Play** repeats it, and the folder button opens the folder where the file was saved.

If the download is a lot to ask for before hearing anything, the **Storage** tab in Settings gives you a command to fetch the model in advance instead.

## Preset voice

Choose a speaker from the list that comes with the model, and Bunyi reads your text in that voice.

There is a **style instruction** box — a short phrase describing how the text should be said, such as "cheerful and quick" or "calm, like a bedtime story". Leave it blank for a neutral reading.

**On this version the instruction is currently ignored.** The library Bunyi uses for speech treats this model as one that cannot take instructions, although the people who made the model say it can. The box is still there because the setting is saved with each clip, and it will start working when that is fixed upstream.

## Language

The language menu covers English, Chinese, Japanese, Korean, German, French, Russian, Portuguese, Spanish, and Italian, plus **auto**, which lets the model decide from your text. Auto is a good default; set the language explicitly when your text is short or mixes languages.

## Stopping

While Bunyi is working — downloading the model or generating — the **Generate** button becomes **Stop**. Press it, or press Escape, to abandon the run.

Stopping is not always instant. The app stops waiting immediately, but the part doing the actual speech work may take a moment to wind down, and Bunyi says *Stopping…* until it has. It will not start another generation until that finishes.

If you close the window while Bunyi is working, it asks first. *Keep working* leaves it running; *Stop and close* cancels it.

## History

The **History** tab lists everything you have generated, newest first.

Each row shows what was said, the voice it used, and when it was made. Hover over a row to see the whole thing — the full text, the language, the voice, and which model produced it.

Every row has four things you can do:

- **Play** — a ring around the button fills as the audio plays. Press it again to stop.
- **Save a copy** — put a copy wherever you like.
- **Show this file on disk** — open the folder it is in.
- **Move to the Trash** — after confirming. It goes to the Recycle Bin on Windows, or your desktop's Trash on Linux, rather than vanishing, so you can still get it back.

**Copy everything about this clip** puts all of it on the clipboard, ready to paste into a note or a message.

History reads the folder each time you open it, so a file you delete outside Bunyi disappears from the list too.

## Where your files go

Generated audio is saved automatically as a WAV file named for the mode and the time it was made. On Windows they are in `%LOCALAPPDATA%\Bunyi\Outputs`; on Linux, `~/.local/share/Bunyi/Outputs`. The folder button next to **Play** opens it.

**Each file remembers how it was made.** The text, the voice, the language, and the model are stored inside the WAV itself, so a file you find months later — or send to someone else — still says what produced it. Other audio apps can read it too: the title and artist fields show the text and the voice.

## Settings

The **gear** in the top right opens Settings.

### Models

The model can come from either of two places:

- A **Hugging Face repo ID**, which is the default and needs nothing from you.
- A **web address** starting with `https://`, if you host the model files yourself or on a server inside your organization.

Bunyi decides which one you meant from what you typed. Clear the field to go back to the built-in default.

### Storage

Models are large, so you can keep them wherever you like — an external drive, for instance. Choose a folder here and Bunyi remembers it across launches, with buttons to open it in your file manager or reset it to the default.

Keep models on a reasonably fast drive if you can. Bunyi reads the model straight from disk as it speaks rather than loading all of it first, so a slow drive makes generating slower, not just starting up.

This tab also shows the ready-made download command, if you would rather fetch the model in advance instead of waiting on first use, and lists what you have already downloaded so you can delete a model you no longer want.

### Appearance

Light, dark, or follow the system. It applies to every Bunyi window straight away.

## Doctor

The **stethoscope** in the top right checks whether this computer can finish a generation, and tells you what it found.

It looks at whether the model is downloaded, whether there is room on the disk for it, whether there is memory to load it, whether the server it comes from is answering, and whether Bunyi can write into the folder it saves to. Ask it directly and it also checks the files you have already downloaded against the checksums your server publishes — this is what catches a model that arrived incomplete and would otherwise load and produce nonsense.

Every check is reported, including the ones that passed. **Copy** puts the findings on the clipboard, and they are written to the Logs as well, so they are easy to include when you describe a problem to somebody.

The report names the mode it checked. The History tab is not a generation mode, so a check started there reports on the mode you generated with last.

Doctor stays available while Bunyi is working, which is usually when you want it.

**Bunyi also runs these checks by itself before every generation** — without the checksum one, which is too slow to run every time. If something would stop the run, such as no room on the disk or a server that is not answering, it says so before the download starts rather than after several gigabytes of it. Running low on memory never stops a generation: it is noted in the Logs instead, because a computer short of memory still finishes, just more slowly. When nothing is wrong you will not see anything at all.

## Logs

The **lines** button in the top right opens a running account of what Bunyi is doing: downloads, generation progress, where each file was saved, and the full text of any error. Select and copy from it when you want to report a problem, or use **Copy** to take all of it at once.

The same lines are also written to a file, so a run that ended badly still left a record. On Windows it is in `%LOCALAPPDATA%\Bunyi\Logs`; on Linux, `~/.local/share/Bunyi/Logs`.

## If something goes wrong

**Start with the stethoscope.** It checks the things that most often stop a generation — disk space, memory, a model that has not downloaded, a server that is not answering — and says which one it is.

**A download seems stuck.** Model files are big, and the progress bar can look frozen while a single multi-gigabyte file is being written. Bunyi watches the bytes actually arriving on disk and says so in the status line if nothing new has arrived for a while. Open the Logs for detail.

**A download stopped part way.** Start it again — Bunyi keeps what it already fetched and carries on from there rather than starting the file over.

**A model will not download from your own server.** Use `https://` rather than `http://`. The Logs name the exact file and status code that failed.

**Generating is slow.** The first run after starting Bunyi includes loading the model, which takes a while on its own. If every run is slow, check where your models folder is: reading the model from a slow external drive slows down the speaking itself.

**Not enough memory.** Doctor warns rather than stopping you, and the run will usually finish anyway. Closing other applications first is the thing that helps most.
