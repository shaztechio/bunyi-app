# Desktop installers

The standard CPU desktop publish feeds three installers. All include .NET;
models remain downloads. CUDA and standalone CLI builds remain separate archives.
The [spec](../../../../spec/FEATURES.md#14-desktop-installers-windows-and-linux)
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

CI also publishes version `0.0.1` as a disposable upgrade fixture, using
`-p:Version=0.0.1` and the builder's `-Version 0.0.1`. Passing its setup as
`-PreviousInstaller` verifies an actual version upgrade, including the installed
DLL's version. This never changes the source version or publishes fixture files.

The normal installation location is `%LOCALAPPDATA%\Programs\Bunyi` and setup
does not elevate. Users close Bunyi themselves before setup or uninstall; setup
never force-closes a generation. The mutex is only a lifetime signal, not a
single-instance restriction. Inno removes its registered files on uninstall;
there are no wildcard user-data deletions or model-download steps.

Windows downloads remain unsigned. When production signing is ready, sign
`Bunyi.App.exe`, `Bunyi.App.dll` and `Bunyi.Core.dll` first, then supply
`-SigningCommand` with an Inno-compatible signing command containing `$f`.
Credentials belong in the signing tool's environment. The builder refuses
unsigned application binaries in this mode, signs setup and uninstall, and
verifies the finished setup signature before checksumming. Production signing
is not configured or claimed by this PR. MSIX/Store submission stays separate.

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
