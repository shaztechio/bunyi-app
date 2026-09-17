# Desktop installers

The standard CPU desktop publish feeds three installers. All include .NET;
models remain downloads. Windows CUDA also has a setup EXE. Linux CUDA and
standalone CLI builds remain separate archives.
The [spec](../../../spec/FEATURES.md#14-desktop-installers-windows-and-linux)
defines upgrade, uninstall and data-preservation behavior. macOS keeps its DMG.

## Metadata and artwork

`desktop_metadata.py` reads `windows/AppxManifest.xml` for the reserved product
name, publisher and description. `windows/StoreListing/text/` is the existing
7 September 2026 Store listing pack, copied verbatim from the prepared local
`artifacts/windows-store/listing-pack/text/` files so CI can use it. The complete
description stays as Store source material; Linux uses its platform-neutral
opening paragraphs and features, not its Windows-specific requirements.

Linux search terms come from the Store search terms. The Windows ICO embeds
the existing Store target-size PNGs verbatim (16/24/32/48/256px); Linux icons
use the same PNGs plus the Store's 600px tile. There is no new artwork or
separate marketing copy. The application ID is `app.bunyi.Bunyi`, matching
Bunyi's existing reverse-domain identity. Linux's launcher is `bunyi-desktop`;
`bunyi` remains the standalone CLI command.

## Windows

Needs PowerShell 7, Python 3.12+, .NET 10, Visual Studio's C++ redistributable
component and Inno Setup 6. CI downloads
Inno Setup **6.7.3** from the official release and verifies its pinned SHA-256.

From `apps/dotnet`:

```powershell
dotnet publish src/App -c Release -r win-x64 --self-contained -p:BunyiCuda=false -o artifacts/win-x64
./packaging/windows/install-inno.ps1 -Destination artifacts/inno
./packaging/windows/build-installer.ps1 -PublishDirectory artifacts/win-x64 -CompilerPath artifacts/inno/compiler/ISCC.exe
./packaging/windows/test-installer.ps1 -Installer dist/Bunyi-1.3.1-win-x64-setup.exe
```

Use the current version's filename. The test refuses an existing installed
Bunyi and uses a temporary program directory. It checks Start/desktop shortcuts,
Installed Apps registration, reinstall repair, setup/uninstall refusal while
the desktop mutex exists, removal, and user-data preservation. It does not
exercise speech or claim a screen-reader pass. Installer logs are retained.

For CUDA, publish with `-p:BunyiCuda=true` into a separate directory and pass
`-Cuda` to both `build-installer.ps1` and `test-installer.ps1`. The output is
`Bunyi-<version>-win-x64-cuda-setup.exe`. The builder checks the dependency
manifest and provider payload, refusing a CPU/CUDA mismatch.

Both installers use the same Bunyi identity and path. Installing one replaces
the other while retaining user data. CPU setup removes the exact GPU-provider
DLLs owned by the CUDA package. Pass the other flavor's installer as
`-AlternativeInstaller` to exercise a full flavor round trip; CI tests CUDA
over an older CPU fixture, CUDA → CPU → CUDA, repair, startup and uninstall.
GPU acceleration itself requires NVIDIA hardware and compatible user-installed
CUDA/cuDNN libraries; the hosted runner verifies packaging, not GPU inference.

CI also publishes version `0.0.1` as a disposable upgrade fixture, using
`-p:Version=0.0.1` and the builder's `-Version 0.0.1`. Passing its setup as
`-PreviousInstaller` verifies an actual version upgrade, including the installed
DLL's version. This never changes the source version or publishes fixture files.

The normal installation location is `%LOCALAPPDATA%\Programs\Bunyi` and setup
does not elevate. Users close Bunyi themselves before setup or uninstall; setup
never force-closes a generation. The mutex is only a lifetime signal, not a
single-instance restriction. Inno removes its registered files on uninstall;
there are no wildcard user-data deletions or model-download steps.

The release workflow signs Windows downloads using Certum SimplySign. The
build jobs upload intermediate payloads; a separate Windows signing job uses
one authenticated session for CPU and CUDA. It signs the desktop and CLI's
own EXE/DLL files, builds signed setup/uninstall executables, checks the
installed signatures, and verifies the binaries again after ZIP extraction.
Checksums describe the final signed files. A signing failure stops publication;
unsigned intermediate artifacts are never included in a release. Existing
unsigned releases are unchanged. MSIX/Store submission stays separate.

Configure these Actions secrets on the repository (or a protected signing
environment if an `environment:` gate is added to the signing job):

- `CERTUM_USERNAME`: SimplySign login email.
- `CERTUM_OTP_URI`: the full `otpauth://totp/...` provisioning URI, including
  its secret and algorithm/digits/period parameters; not a temporary OTP.
- `CERTUM_KEY_ID`: the certificate's 40-character SHA-1 thumbprint, without
  spaces. This is a certificate selector; signatures use SHA-256. Update it
  when the certificate is renewed or reissued.

The workflow uses `dismine/windows-app-signing-setup-action`, pinned to reviewed
commit `89ae3b032d4bc7a5b98d1a42a34e61ecb6faad64`, to install SimplySign and
authenticate its desktop client on the hosted Windows runner. This is a
third-party integration, not a Certum headless API. Screenshots are disabled;
never print or upload the provisioning URI, generated OTPs or authentication
screenshots. The URI grants signing access and belongs only in Actions secrets.
Only trusted release code should run this job; it has a read-only GitHub token,
and no PR trigger. PR checks exercise ordinary unsigned builds without secrets.

Use **Windows + Linux release → Run workflow → bump: none**, leaving `version`
empty, to test the full signing flow without tagging, releasing or refreshing
the website. Successful runs expose `windows-signed` containing both flavors'
installers, portable desktop/CLI ZIPs and checksums. Inspect signatures before
cutting the first signed release; SmartScreen can still warn on new downloads.

For local signing with SimplySign connected, `windows/sign-files.ps1 -Path ...`
uses `CERTUM_KEY_ID` and discovers the Windows SDK x64 SignTool; set
`BUNYI_SIGNTOOL` to override its location. `-VerifyOnly` requires a valid trusted
signature from that exact certificate and a trusted timestamp. For an installer,
sign `Bunyi.App.exe`, `Bunyi.App.dll` and `Bunyi.Core.dll` first, then pass
`-SigningCommand` to `build-installer.ps1` with an Inno-compatible command
containing `$f`. The release packaging script supplies this command and signs
both setup and uninstall. Run `package-signed-release.ps1` only on a disposable
Windows runner: it includes installation/uninstallation tests.

ONNX and Whisper need the Visual C++ CRT and OpenMP runtimes even in a
self-contained .NET build. Setup stages Microsoft's signed x64 DLLs from
Visual Studio's `VC/Redist/MSVC/.../x64/Microsoft.VC*.CRT` and sibling
`Microsoft.VC*.OpenMP` directories beside the app. Override detection with
`-VcRuntimeDirectory`; it must contain the CRT DLLs with OpenMP beside or in it.
The builder verifies Microsoft signatures and x64 architecture and never copies
System32 DLLs. App-local deployment keeps setup non-administrative; runtime
updates ship with Bunyi releases. See Microsoft's
[local deployment guidance](https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170).

## Linux

Needs Python 3.11+, `dpkg-deb` and `rpmbuild`. Build on Linux after publishing:

```sh
dotnet publish src/App -c Release -r linux-x64 --self-contained -p:BunyiCuda=false -o artifacts/linux-x64
python3 packaging/linux/build-packages.py --publish-directory artifacts/linux-x64
```

Use `--formats deb` or `--formats rpm` to build one format, and `--output` to
change the default `apps/dotnet/dist`. Builders validate CPU/RID/version and
the packaged mirror default, leave the publish folder unchanged, and retain
unique staging directories. Each artifact has a `.sha256` sidecar.

Packages own `/usr/lib/bunyi`, `/usr/bin/bunyi-desktop`, and their desktop/icon/
AppStream/license files under `/usr/share`. They do not own any home-directory
data. Native dependencies are explicit, including audio, ICU, OpenSSL and
OpenMP. Optional .NET diagnostic libraries are not mandatory dependencies.
No APT/DNF repository or automatic application updater is installed.

`linux/test-installed.sh` only runs in disposable Docker containers. CI tests
Ubuntu 24.04, Debian 12/13 and Fedora 43/44: dependency resolution, native
ONNX/Whisper/graphics loading, desktop-entry validation, non-root GUI startup
under Xvfb, reinstall repair and package removal (including DEB purge) while
retaining user data. No preinstalled .NET runtime is used. These checks gate
both PRs and releases.

PR checks also install an older `0.0.1` package fixture before the current
package. Build it with `-p:Version=0.0.1` and `build-packages.py --version 0.0.1`;
pass its directory as the third argument of `test-installed.sh`. Builders still
require the published binary version to match the requested package version.

## Release validation

The workflows package the same CPU publish used for portable archives, attach
checksums, and gate publication on installation checks. The website checks
uploaded assets before showing installer links; old archive-only releases keep
their existing instructions, and a partially uploaded installer set is skipped.

Before shipping, also exercise on real Windows/Linux desktops: keyboard and
screen-reader access to setup, first-run downloads, all three generation modes,
playback, recording/transcription, folder pickers, voices, history, backup and
restore, and custom model paths. Container startup and native loading do not
prove hardware audio or spoken accessibility behavior. Store certification and
Store-data migration remain tracked separately in #215.
