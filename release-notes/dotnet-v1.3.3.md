# Bunyi 1.3.3 — Windows and Linux

## Smoother long-text speech

- **Adjust the pause between generated sections.** Settings → General → Sentence gap (ms) defaults to 300 ms and accepts 0–5000 ms. Zero disables added silence. Changes apply to the next recording; pauses within a section remain controlled by the voice model.
- **Voice Clone automatic references end at a quiet pause.** Longer reference recordings are sampled up to ten seconds without cutting through speech. Transcription and cloning use the same selection, and saved voices retain it. If no suitable pause is found, Bunyi asks for a shorter recording. Desktop and CLI follow the same rule.

## Downloads and packaging

- Temporary model-server failures retry with backoff. Explicitly paused downloads still stop promptly, and Stop remains available while waiting.
- Microsoft Store packages include the native runtime libraries required for inference, transcription and audio.
- Standard CPU and NVIDIA CUDA Windows installers retain the existing Certum signing and timestamp verification.

## Updating

Close Bunyi and run the installer for your edition. Models, saved voices, recordings and settings are retained. To apply the reference-boundary fix to an existing clone, select its original reference recording, clear the transcript and generate again; save the voice again to keep the new selection. Existing recordings are unchanged.
