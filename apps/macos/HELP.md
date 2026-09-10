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

Bunyi turns text into speech on your own Mac. Nothing you type and no audio you make is sent anywhere: the voice models run locally, and after the first download the app works with no internet connection at all.

## Saying the name

**Bunyi** is *BOON-yee* — /ˈbuːɲi/ in the phonetic alphabet. The "ny" in the middle is a single sound, the one in the middle of *onion*, or the "ñ" of *jalapeño*; it is not "boon-yai" or "bun-yi".

The word is Malay and Indonesian for **sound**.

## Start here

1. Type or paste your text in the big box.
2. Pick a mode at the top: **Preset voice**, **Voice design**, or **Voice clone**. (The fourth tab, **History**, is where everything you have made is kept.)
3. Click **Generate**.

The first time you use each mode, Bunyi downloads that mode's voice model — between about 1.5 GB and 4.5 GB — and shows a progress bar with an estimate of the time remaining. This happens once per mode. Every generation after that is offline and much faster.

**Downloading is setup for speech.** Bunyi keeps the stage name visible and says when speech has not started yet. After setup, it creates your speech automatically. Downloaded models are saved for reuse; changing or deleting a model can require another download.

Two progress bars show **Overall model download** and **Current file**. Each has its own percentage and exact byte count. A large file can receive data without its rounded percentage changing, so a **Received … bytes** message and the time since the last data arrived show that the download is moving—even one byte counts. Download time estimates refer to downloading only.

Both bars also show their total size in MB or GB. **Model download elapsed** shows how long this setup attempt has taken, including checking files and reconnecting. Speed and remaining time use recent incoming data, so earlier fast files cannot hide a slow current download.

**Download is slow** means data has been arriving slowly for at least 30 seconds and substantial downloading remains. **Reconnect and resume** opens a fresh connection for the current file while keeping downloaded data. It can help a poor connection, but does not guarantee a faster server. **Stop** also keeps partial files; the next Generate resumes when the server supports it. If the server ignores resume requests, that file starts again.

If no data arrives, the message changes to **Waiting for more data**. After 30 seconds it says the download may be stalled. Incoming data clears that warning. **Checking model files**, **Loading model**, **Creating your speech**, and **Preparing your audio file** identify the work that follows. Only **Your audio is ready** means speech has finished and been saved.

When generation finishes the audio plays automatically. **Play** repeats it, and the reveal button opens the folder where the file was saved.

## The three modes

### Preset voice

Choose a speaker from the list that comes with the model, and Bunyi reads your text in that voice.

You can also add a **style instruction** — a short phrase describing how it should be said, such as "cheerful and quick" or "calm, like a bedtime story". Leave it blank for a neutral reading.

### Voice design

Instead of picking a speaker, describe the voice you want: "a warm older man with a slight rasp", or "a bright, energetic presenter". Bunyi builds a voice to match the description.

Style instructions work here too, and they do a different job from the description. The description is *who is speaking*; the instruction is *how they are speaking right now*.

### Voice clone

Give Bunyi a short recording of a voice and it reads your text in that voice.

Two things matter for a good clone:

- **The transcript.** Cloning works by lining up the recording with its words. Leave this box blank and press **Generate**: Bunyi prepares the voice model, transcribes the recording on your Mac, and creates your speech in one run. Selecting a recording does not start a download. Whatever you type yourself is used instead. **Stop** cancels the current run and keeps downloaded files for reuse. You can edit the transcript afterwards and generate again.
- **The recording.** A few clean seconds of a single person speaking, without music or background noise, beats a long noisy clip. Bunyi converts the audio to the rate the model needs, so you do not have to prepare the file.

There is no style instruction in this mode. The model behind cloning cannot take one, so the emotion of a cloned voice comes from the delivery in the reference clip. If you want the same cloned voice in different moods, save one voice per mood.

## Language

The language menu covers English, Chinese, Japanese, Korean, German, French, Russian, Portuguese, Spanish, and Italian, plus **auto**, which lets the model decide from your text. Auto is a good default; set the language explicitly when your text is short or mixes languages.

## Saved voices

In Voice clone mode you can save a clone as a named voice. Bunyi copies the recording into its own storage, so the saved voice keeps working even if you later move or delete the original file.

Pick a saved voice from the menu and its recording and transcript fill in for you. Deleting a saved voice removes the copy Bunyi made.

Saved voices are not the same as the preset speakers. A preset is a voice the model was trained on; a saved voice is a shortcut that re-runs the clone with the inputs you stored.

## Stopping

While Bunyi is working — downloading a model, transcribing, or generating — the **Generate** button becomes **Stop**. Press it, or press Escape, to abandon the run.

Stopping is not always instant. The app stops waiting immediately, but the part doing the actual speech work may take a moment to wind down, and Bunyi says *Stopping…* until it has. It will not start another generation until that finishes.

## History

The **History** tab lists everything you have generated, newest first.

Each row shows what was said, the voice it used, and when it was made. Hover over a row to see the whole thing — the full text, the language, the voice or reference, and which model produced it.

Every row has four things you can do:

- **Play** — a ring around the button fills as the audio plays. Press it again to stop.
- **Download** — save a copy wherever you like.
- **Show in Finder** — reveal the original file.
- **Trash** — move it to the Trash, after confirming. It goes to the Trash rather than vanishing, so you can still get it back.

**Copy details** puts everything Bunyi knows about a clip on the clipboard, ready to paste into a note or a message.

History reads the folder each time you open it, so a file you delete in the Finder disappears from the list too.

## Where your files go

Generated audio is saved automatically as a WAV file named for the mode and the time it was made. The reveal button next to **Play** opens the folder in the Finder.

**Each file remembers how it was made.** The text, the voice, the language, and the model are stored inside the WAV itself, so a file you find months later — or send to someone else — still says what produced it. Other audio apps can read it too: the title and artist fields show the text and the voice.

## Settings

Open Settings with **⌘,**, or the gear in the toolbar.

### General

**Appearance** is light, dark, or follow the system. It applies to every Bunyi window straight away.

**Free memory when switching modes** decides *when* Bunyi lets go of a model, not whether it does. Each mode uses its own model, and a model can be several gigabytes.

- **On**, the default: the model goes as soon as you leave its tab. Coming back to that mode means loading it again.
- **Off**: it stays while you look at other tabs, so going back to it is instant. It is released the next time you generate in a *different* mode — the moment the memory is actually wanted.

Either way you never hold two models at once. The setting moves when the memory comes back, not whether it does.

### Models

Each mode has its own model, and each one can come from either of two places:

- A **Hugging Face repo ID**, which is the default and needs nothing from you.
- A **web address** starting with `https://`, if you host the model files yourself or on a server inside your organization.

Bunyi decides which one you meant from what you typed. Clear a field to go back to the built-in default for that mode.

### Storage

Models are large, so you can keep them wherever you like — an external drive, for instance. Choose a folder here and Bunyi remembers it across launches, with buttons to open it in the Finder or reset it to the default.

This tab also shows ready-made download commands, one per mode, if you would rather fetch a model in advance instead of waiting on first use.

### Backup

**Back up** collects everything in your models folder into a single `.zip` file, so you do not have to download several gigabytes again after erasing a Mac or moving to a new one.

**Restore** unpacks a backup back into your models folder. Models you already have are left alone — restoring never overwrites what is already there.

Both show progress and can be stopped part way.

## Doctor

The **stethoscope** in the toolbar checks whether this Mac can finish a generation, and tells you what it found.

It looks at whether the model for the current mode is downloaded, whether there is room on the disk for it, whether there is memory to load it, whether the server it comes from is answering, and whether Bunyi can write into the folder it saves to. Ask it directly and it also checks the files you have already downloaded against the checksums your server publishes — this is what catches a model that arrived incomplete and would otherwise load and produce nonsense.

Every check is reported, including the ones that passed. **Copy** puts the findings on the clipboard, and they are written to the Logs as well, so they are easy to include when you describe a problem to somebody.

The report names the mode it checked, because the three modes use different models. The History tab is not a generation mode, so a check started there reports on the mode you generated with last.

Doctor stays available while Bunyi is working, which is usually when you want it.

**Bunyi also runs these checks by itself before every generation** — without the checksum one, which is too slow to run every time. If something would stop the run, such as no room on the disk or a server that is not answering, it says so before the download starts rather than after several gigabytes of it. Running low on memory never stops a generation: it is noted in the Logs instead, because a Mac short of memory still finishes, just more slowly. When nothing is wrong you will not see anything at all.

## Logs

**Window → Logs**, or **⌘L**, opens a running account of what Bunyi is doing: downloads, transcription, generation progress, where each file was saved, and the full text of any error. Select and copy from it when you want to report a problem.

## If something goes wrong

**Start with the stethoscope.** It checks the things that most often stop a generation — disk space, memory, a model that has not downloaded, a server that is not answering — and says which one it is. See [Doctor](#doctor).

**A download seems stuck.** Model files are big, and the progress bar can look frozen while a single multi-gigabyte file is being written. Bunyi watches the bytes actually arriving on disk and says so in the status line if nothing new has arrived for a while. Check the Logs window for detail.

**A cloned voice sounds wrong, or says the wrong words.** Almost always the transcript does not match the recording. Check that it is what the clip actually says, then generate again.

**The app asks for permission to recognize speech.** That prompt appears the first time Bunyi transcribes a reference clip. Recognition prefers to run on your Mac; if a language is not available offline, macOS may use Apple's service for that step.

**A model will not download from your own server.** Use `https://` rather than `http://` — plain HTTP is blocked unless the app was built to allow it. The Logs window names the exact file and status code that failed.

**Closing the window during a download or generation.** Bunyi asks before stopping work in progress. *Keep Working* leaves it running; *Stop and Close* cancels it.
