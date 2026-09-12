# Windows/Linux 1.3.0 preparation

This is release preparation, not authorization to publish. The version bump,
packaged defaults and release notes go through a PR. Once approved for release,
tag the merged version commit `dotnet-v1.3.0`; the tag path verifies that the
binary version matches the tag. Do not use the manual bump mode to bypass the
repository's PR-only rule.

Release inputs: merged #220–#223, #229–#231 and the packaged-default implementation.
Native macOS remains independently versioned. Long-text inference/listening
limitations remain in [LONG-TEXT-PLAN.md](LONG-TEXT-PLAN.md).

Validation gates:

- Release tests on Windows and Linux, including explicit mirror-off persistence,
  reset, custom sources, and recovery from a mirror-default package.
- Build-only release workflow (`bump: none`) on the release branch: four desktop
  and four CLI archives, checksums, and one separate CPU-only mirror MSIX.
- Validate packaged defaults in the actual desktop/CLI archives and MSIX.
  Exercise CLI version, input validation and server lifecycle from extracted files.
- Launch the rebuilt Windows app. Inspect Models, restart after an explicit
  source choice, and check preservation of existing settings and model folders.
- Installed local-test MSIX: launch, verify mirror default, turn it off, restart
  and verify the override. Keep the Store identity and unsigned artifact separate.
- Record existing real-model validation honestly; broader long-passage,
  multilingual and subjective voice-continuity checks remain tracked rather
  than inferred from passing unit tests. Store certification, install/upgrade/
  uninstall and coexistence acceptance are separate from archive creation.

Publication gate: obtain release approval only after the build artifacts and
validation results are reviewable. After publication, update README/spec wording
from prepared/unreleased to released and verify download links/checksums.
