# Packaged model-download defaults

The contract is in [FEATURES.md §3a](FEATURES.md#3-model-management) and
[DATA-FORMATS.md](DATA-FORMATS.md#packaged-model-download-defaults).

Every macOS, Windows and Linux desktop/CLI package ships the canonical
`spec/bunyi.defaults.json`, which selects the Bunyi mirror. Per-mode user sources
take precedence. On Windows/Linux, the mirror switch saves explicit sources, so
turning it off survives restart and upgrade. On macOS, explicit source-field
values have the same persistence. Only clearing/resetting overrides follows the
package again. Whisper keeps its existing source; this setting applies only to
the three TTS models.

Native macOS follow-up: add the matching Settings → Models convenience switch,
preserving existing custom sources until explicitly changed, saving all three
choices atomically, and reflecting effective sources after reset/restore.
The existing native source fields and saved mirror configuration remain usable.
Verify keyboard and VoiceOver behavior under #158 and pass macOS CI when
implementing this follow-up.

MSIX installation/upgrade/coexistence testing remains a separate distribution
check; package creation alone does not establish Microsoft Store certification.
