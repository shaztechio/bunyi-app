# Packaged model-download defaults

The contract is in [FEATURES.md §3a](FEATURES.md#3-model-management) and
[DATA-FORMATS.md](DATA-FORMATS.md#packaged-model-download-defaults-net).

Windows/Linux ship a Hugging Face default in portable desktop/CLI builds and a
mirror default in Windows MSIX. Per-mode user sources take precedence. The
mirror switch saves explicit sources, so turning it off survives restart and
upgrade. Only clearing/resetting overrides follows the package again.

Native macOS follow-up: add the matching Settings → Models convenience switch,
preserving existing custom sources until explicitly changed, saving all three
choices atomically, and reflecting effective sources after reset/restore.
The existing native source fields and saved mirror configuration remain usable;
the native package continues to default to Hugging Face. Verify keyboard and
VoiceOver behavior under #158 and pass macOS CI when implementing this follow-up.

MSIX installation/upgrade/coexistence testing remains a separate distribution
check; package creation alone does not establish Microsoft Store certification.
