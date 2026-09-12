# Long-text generation validation

The inference path was tested on Windows CPU with an 89-word generic passage,
sampling seed 7 and the installed self-hosted exports. Clone used a synthetic
4.4-second reference. No private voice or user script was used.

| Mode | Final audio | Generation time |
|---|---:|---:|
| Preset | 36.04 s | 178.78 s |
| Clone | 28.76 s | 68.10 s |
| Design → Clone | 29.76 s | 204.15 s |

Design's first continuation failed to select EOS and hit its 560-frame limit.
That attempt was discarded; its two smaller replacements finished after 67 and
83 frames. The opening was retained once. This verified real bounded recovery.
Local Whisper transcriptions retained all passage words for Preset and Clone;
Design retained the passage with one additional “Ah” near the opening.

These generation measurements predate the final playback simplification and
are retained as inference evidence, not new timing claims. The app now plays
only the completed saved recording. Regression tests check that rule across
all three modes, section retries, cancellation and one-file metadata/cleanup.
Reports are in `results/`; generated audio remains in local artifacts.

Broader listening checks for omissions, repetitions, voice identity and joins,
Linux device validation, macOS sectioning parity and the original model failure's
underlying cause remain open in the shared long-text plan.
