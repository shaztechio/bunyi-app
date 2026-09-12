# Windows MSIX

Builds Bunyi's self-contained **win-x64 CPU** app as an unsigned MSIX. This
is submission preparation for [#215](https://github.com/shaztechio/bunyi-app/issues/215),
not a claim of Store certification or installed-package compatibility.
The portable Windows and Linux release artifacts are unchanged.

The MSIX stages `bunyi.defaults.json` with `modelDownloadSource: "mirror"`.
This changes only the default for unset sources. Saved user choices still win;
turning the mirror off in Settings saves Hugging Face explicitly across restarts.
Portable desktop/CLI builds carry `huggingFace`. Packaging verifies the staged
file and the file inside the finished MSIX, and leaves a supplied portable
publish folder unchanged. See [the schema](../../../../spec/DATA-FORMATS.md#packaged-model-download-defaults-net).

## Identity

The manifest uses the product identity supplied from Partner Center on
7 September 2026. These values are public package metadata, not credentials.

| Field | Value |
|---|---|
| Package name | `13743Shazron.Bunyi` |
| Publisher | `CN=47FEF9A0-273C-4EA1-A295-51AC5D89C37C` |
| Publisher display name | `Shazron` |
| Display name | `Bunyi` |
| Package family name | `13743Shazron.Bunyi_gqpaqhm55dfqw` |
| Store ID | `9PD4BBXZ3948` |

The package family name is derived by Windows, and the Store ID belongs to
Partner Center; neither is an additional manifest field. Package version is
read from `apps/dotnet/Directory.Build.props`: `1.2.0` becomes `1.2.0.0`.
The fourth component stays zero for Store submission. Packaging never bumps
that source version. The manifest's checked-in version is a starting value;
the generated manifest always uses the current .NET release version.

The desktop manifest uses the full-trust entry point and `runFullTrust`,
without administrator elevation or AppContainer conversion. The initial
manifest baseline is Windows 10 build 17763 (the MSIX baseline), including
later Windows releases. This is an installation floor, not proof of testing
on every allowed OS; confirm the supported OS matrix and native dependencies
before certification. `MaxVersionTested` is conservatively the same baseline;
raise it when the packaged application has been tested on newer releases.

## Build

Requires Windows, PowerShell 7, the .NET 10 SDK, and the Windows SDK's x64
`MakeAppx.exe` and `MakePri.exe`. Visual Studio is optional. No Python or
certificate is required to build from the committed assets.

From the repository root:

```powershell
./apps/dotnet/packaging/windows/build-msix.ps1
```

The script publishes the app, generates a resource index for all icon sizes,
and runs MakeAppx validation. Output is under `apps/dotnet/artifacts/msix`:
`Bunyi-<version>-win-x64-store.msix` and its SHA-256 checksum. A unique staging
folder is retained for manifest/resource inspection on each run. The script
never recursively deletes supplied paths.

Options:

```powershell
# Reuse a fresh self-contained CPU publish; RID, dependencies and version are checked.
./apps/dotnet/packaging/windows/build-msix.ps1 -PublishDirectory ./apps/dotnet/artifacts/win-x64

# Point at an SDK outside the standard install location.
./apps/dotnet/packaging/windows/build-msix.ps1 -SdkBinPath 'C:\path\to\sdk\bin\10.0.x.y\x64'

# Separate identity/display name for local installation experiments.
./apps/dotnet/packaging/windows/build-msix.ps1 -LocalTest
```

SDK tools can also come from Microsoft's `Microsoft.Windows.SDK.BuildTools`
NuGet package: unpack it and supply its `bin/<version>/x64` directory.
CI builds and uploads an unsigned `windows-store-msix` artifact on Windows;
it does not submit to Partner Center or change the existing release downloads.

## Artwork

`make-assets.py` resamples the existing macOS 1024px Bunyi icon directly:
`apps/macos/Assets.xcassets/AppIcon.appiconset/icon-512pt@2x.png`.
It requires Pillow only when regenerating artwork:

```powershell
python -m pip install Pillow
python apps/dotnet/packaging/windows/make-assets.py
```

Committed assets include the 50px package logo, 44px app icon and 150px tile,
each at 100/125/150/200/400% scale, plus 16/24/32/48/256px target-size icons
with unplated and light-unplated variants. The original purple background is
part of Bunyi's artwork and is retained. `MakePri` resolves the manifest's
unqualified image paths to these variants. No optional wide/large tiles are
declared, so they require no assets.

`StoreListing/StoreLogo-300.png` is a separate 300px Partner Center listing
logo; it is not included in the installed package. Screenshots and listing
copy are separate submission work tracked in #215.

## Signing and installation testing

The **Store** package is deliberately unsigned: Microsoft signs the package
for Store distribution. Upload the `-store.msix` in Partner Center after the
installed-package checks below; it is not a directly installable public download.

For a local test, build with `-LocalTest`, sign the resulting `-local-test.msix`
using a code-signing certificate whose subject is exactly `CN=Bunyi Local Test`,
and trust its public certificate on the test machine before using
`Add-AppxPackage -Path <signed-package>`. A self-signed test certificate is
sufficient for this. Do not distribute it as the Store package or commit a
private key. Signing/trust/installation are explicit local setup steps, not
side effects of the packaging script. See Microsoft's signing guide below.

Before publishing, run Windows App Certification Kit on the installed package
and exercise first-run model downloads, each generation mode, native audio and
Whisper loading, playback, file/folder pickers, voices, history, backup/restore,
and settings. Verify portable-app coexistence, storage virtualization, upgrades
and uninstall with disposable user data. An MSIX identity can affect AppData
redirection and uninstall cleanup; do not assume the portable app's lifecycle
matches the installed package. Models remain separate downloads.

Suggested restricted-capability explanation for certification:

> Bunyi is an Avalonia/.NET desktop text-to-speech application. It runs ONNX
> Runtime and Whisper native libraries locally, plays and saves audio, and
> reads user-selected audio and model folders. It uses the desktop full-trust
> entry point and does not require administrator elevation.

## Microsoft references

- [Manual desktop packaging and manifest](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion)
- [MakeAppx](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool)
- [Store package requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)
- [Signing an app package](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool)
- [Store submission](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission)
