# Bunyi 1.3.2 — Windows and Linux

## Signed Windows downloads

Windows desktop and CLI binaries, setup installers and uninstallers are now
digitally signed with a Certum certificate issued to **Shazron Elhazar Abdullah**.
Both standard CPU and NVIDIA CUDA editions are signed and timestamped. The
release process verifies signatures before publishing and generates SHA-256
checksums from the final files.

SmartScreen may still warn on a new download. Use **More info** to check the
publisher. Signing identifies the publisher and lets Windows detect changes
to the signed files; it does not guarantee an immediate SmartScreen reputation.

## Updating

Close Bunyi and run the 1.3.2 installer for your edition. It upgrades the existing
installation while retaining models, voices, recordings and settings. Standard
and CUDA installers can replace each other in the same installation.

Existing 1.3.1 assets and checksums remain available unchanged. This is a
packaging and release update; speech-generation behavior is unchanged.
Linux packages and archives retain checksum verification. Native macOS releases
keep their independent version and existing Apple signing/notarization.
