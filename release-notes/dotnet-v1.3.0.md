# Bunyi 1.3.0 — Windows and Linux

## Longer scripts

All three modes estimate speech duration and split long scripts into smaller
sections. Attempts that run past their limit retry as shorter sections. Bunyi
saves one complete recording and then plays it. Stop cancels the unfinished
recording. Long Voice Design requests use a completed opening as a reference
for the remaining speech and may download the configured clone model.

## CLI for agents and terminals

Separate CLI archives provide preset, design and clone generation, an optional
persistent server, model downloads, job progress/cancellation, local playback,
and management commands. Everything runs locally. Read the CLI guide before
using the server or automating generation.

## Downloads and settings

Downloads show bytes received and distinguish waiting from slow or stalled
transfers. Rate limits use bounded retries with a countdown. If the Bunyi mirror
is unavailable, an explicit recovery action can switch the affected mode to
Hugging Face. Existing source-specific downloads are kept.

Portable desktop and CLI builds default to Hugging Face. The separately prepared
Windows MSIX defaults to the Bunyi mirror. Settings → Models can switch all
three modes between them, and the saved choice survives restart and upgrade.
Reset restores the defaults packaged with that build. Switching sources may
require separate model downloads.

The script heading and scrollbar now fit correctly in the generation view.

## Availability

Desktop and CLI archives are available for Windows/Linux x64, each with a
standard CPU build and an optional NVIDIA CUDA build. The MSIX is a separate
unsigned submission/testing artifact, not a directly installable public download
or a claim of Microsoft Store certification. Native macOS releases are separate.
