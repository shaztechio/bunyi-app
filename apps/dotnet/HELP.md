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

## Start here

1. Type or paste your text in the big box.
2. Pick a mode at the top: **Preset voice**, **Voice design**, or **Voice clone**. (The fourth tab, **History**, is where everything you have made is kept.)
3. Click **Generate**.

The first time you use each mode, Bunyi downloads that mode's model — between about 3.9 GB and 5.9 GB — and shows a progress bar with an estimate of the time remaining. This happens once per mode. Every generation after that is offline and much faster.

When generation finishes the audio plays automatically. **Play** repeats it, and the folder button opens the folder where the file was saved.

If the download is a lot to ask for before hearing anything, the **Storage** tab in Settings gives you a command per mode to fetch a model in advance instead.

## The three modes

### Preset voice

Choose a speaker from the list that comes with the model, and Bunyi reads your text in that voice.

There is a **style instruction** box — a short phrase describing how the text should be said, such as "cheerful and quick" or "calm, like a bedtime story". Leave it blank for a neutral reading.

**The instruction has no effect in this mode.** The speech library this app currently uses does not pass it to the small preset-voice model, so it changes nothing — and rather than pretend otherwise, Bunyi leaves the style out of the file's saved details when that happens, so a clip never claims a delivery it did not have.

This is a difference from the Mac version, where the instruction does reach the model, and it is being worked on. In the meantime, **Voice design** takes a description and acts on it.

### Voice design

Instead of picking a speaker, describe the voice you want: "a warm older man with a slight rasp", or "a bright, energetic presenter". Bunyi builds a voice to match the description.

Style instructions work here too, and they do a different job from the description. The description is *who is speaking*; the instruction is *how they are speaking right now*.

### Voice clone

Give Bunyi a short recording of a voice and it reads your text in that voice.

Two things matter for a good clone:

- **The transcript.** Cloning works by lining up the recording with the words in it, so Bunyi needs to know what the clip says. Leave the transcript box blank and it listens to the clip and writes the transcript for you, on your own computer. Whatever you type yourself is always used instead.
- **The recording.** A few clean seconds of a single person speaking, without music or background noise, beats a long noisy clip. Bunyi converts the audio to the rate the model needs, so you do not have to prepare the file.

Only the first ten seconds of the clip are used, and the transcript is taken from exactly that much — a transcript running past the audio makes the clone finish the recording instead of speaking your text.

There is no style instruction in this mode. The model behind cloning cannot take one, so the emotion of a cloned voice comes from the delivery in the reference clip. If you want the same cloned voice in different moods, save one voice per mood.

## Language

The language menu covers English, Chinese, Japanese, Korean, German, French, Russian, Portuguese, Spanish, and Italian, plus **auto**, which lets the model decide from your text. Auto is a good default; set the language explicitly when your text is short or mixes languages.

## Saved voices

In Voice clone mode you can save a clone as a named voice. Bunyi copies the recording into its own storage, so the saved voice keeps working even if you later move or delete the original file.

Pick a saved voice from the menu and its recording and transcript fill in for you. Deleting a saved voice removes the copy Bunyi made.

Saved voices are not the same as the preset speakers. A preset is a voice the model was trained on; a saved voice is a shortcut that re-runs the clone with the inputs you stored.

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

This tab also shows ready-made download commands, one per mode, if you would rather fetch a model in advance instead of waiting on first use, and lists what you have already downloaded so you can delete a model you no longer want.

### Backup

**Back up** collects everything in your models folder into a single `.zip` file, so you do not have to download several gigabytes again after rebuilding a machine or moving to a new one.

**Restore** unpacks a backup back into your models folder. Models you already have are left alone — restoring never overwrites what is already there.

Both show progress and can be stopped part way.

### Appearance

Light, dark, or follow the system. It applies to every Bunyi window straight away.

**Free memory when switching modes** is on this tab too. Each mode uses its own model, and a model can be several gigabytes, so Bunyi lets go of one as soon as you leave its tab. Turn it off to keep it loaded and come back to that mode without waiting for it again — at the cost of the memory it holds meanwhile.

### About

The version you are running, the platform it was built for, and the licence and credits.

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
