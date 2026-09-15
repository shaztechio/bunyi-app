# Copyright 2026 Shazron Abdullah and Bunyi contributors
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$Python = 'python',
    [string]$Version,
    [switch]$Cuda,
    [string]$VcRuntimeDirectory,
    # Inno SignTool command with $f placeholder; credentials stay in the signing
    # tool's environment. The staged EXE/DLLs must already be production-signed.
    [string]$SigningCommand
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows installer packaging requires Windows.' }
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $dotnetRoot 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($OutputDirectory -eq $publish -or $OutputDirectory.StartsWith($publish + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside PublishDirectory.'
}
if (-not $CompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $CompilerPath = $command.Source }
    else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
}
if (-not (Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 6 or pass -CompilerPath pointing to ISCC.exe.' }
[xml]$props = Get-Content -LiteralPath (Join-Path $dotnetRoot 'Directory.Build.props') -Raw
if (-not $Version) { $Version = [string]$props.Project.PropertyGroup.VersionPrefix }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a major.minor.patch version.' }
foreach ($file in @('Bunyi.App.exe', 'Bunyi.App.dll', 'coreclr.dll', 'onnxruntime.dll', 'Bunyi.App.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file))) { throw "Missing self-contained app file: $file" }
}
$deps = Get-Content -LiteralPath (Join-Path $publish 'Bunyi.App.deps.json') -Raw | ConvertFrom-Json
$hasCuda = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.ML.OnnxRuntime.Gpu/*' }).Count -gt 0
if ($deps.runtimeTarget.name -notlike '*/win-x64' -or $hasCuda -ne $Cuda.IsPresent) {
    throw 'Expected a self-contained win-x64 desktop build matching -Cuda (omit it for CPU).'
}
if ($Cuda -and -not (Test-Path -LiteralPath (Join-Path $publish 'onnxruntime_providers_cuda.dll'))) {
    throw 'CUDA publish is missing its ONNX CUDA provider.'
}
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'Bunyi.App.dll')).FileVersion
if ([version]$binaryVersion -ne [version]"$version.0") { throw "Binary version $binaryVersion does not match $version." }
& (Join-Path $PSScriptRoot '../test-defaults.ps1') -Path $publish
if ($SigningCommand) {
    foreach ($file in @('Bunyi.App.exe', 'Bunyi.App.dll', 'Bunyi.Core.dll')) {
        if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $publish $file)).Status -ne 'Valid') {
            throw "Sign the published $file before building a signed installer."
        }
    }
}
$metadata = Join-Path ([IO.Path]::GetTempPath()) ('bunyi-installer-' + [guid]::NewGuid().ToString('N'))
if ($metadata.StartsWith($publish.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The temporary staging directory must be outside PublishDirectory.'
}
& $Python (Join-Path $PSScriptRoot '../desktop_metadata.py') --output $metadata
if ($LASTEXITCODE -ne 0) { throw 'Store metadata generation failed.' }
$product = Get-Content -LiteralPath (Join-Path $metadata 'metadata.json') -Raw | ConvertFrom-Json
if (-not $VcRuntimeDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'Install the Visual Studio C++ redistributable build component or supply -VcRuntimeDirectory (x64 Microsoft.VC*.CRT).'
    }
    $visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $visualStudio) { throw 'No Visual Studio C++ installation found.' }
    $candidates = @(Get-ChildItem -Path "$visualStudio/VC/Redist/MSVC/*/x64/Microsoft.VC*.CRT" -Directory |
        Sort-Object {
            # The display string can include "built by: cloudtest". Sort using
            # the fixed numeric resource fields instead of parsing that text.
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $_.FullName 'vcruntime140.dll'))
            [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
        } -Descending)
    if (-not $candidates.Count) { throw 'No x64 Visual C++ redistributable CRT directory found.' }
    $VcRuntimeDirectory = $candidates[0].FullName
}
foreach ($file in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $VcRuntimeDirectory $file))) { throw "Missing Visual C++ runtime: $file" }
}
$openmp = Join-Path $VcRuntimeDirectory 'vcomp140.dll'
if (-not (Test-Path -LiteralPath $openmp)) {
    $openmpFolder = Join-Path (Split-Path $VcRuntimeDirectory) ((Split-Path $VcRuntimeDirectory -Leaf) -replace '\.CRT$', '.OpenMP')
    $openmp = Join-Path $openmpFolder 'vcomp140.dll'
}
if (-not (Test-Path -LiteralPath $openmp)) { throw 'The x64 Microsoft Visual C++ OpenMP redistributable (vcomp140.dll) is required by Whisper.' }
# App-local redistribution permits non-administrative installation. Never copy
# runtime DLLs out of System32: use Microsoft's designated VS Redist directory.
# Keep the supplied publish immutable, including when preparing signed builds.
$payload = Join-Path $metadata 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
Get-ChildItem -LiteralPath $publish | Copy-Item -Destination $payload -Recurse
$runtimeFiles = @((Get-ChildItem -LiteralPath $VcRuntimeDirectory -Filter '*.dll' -File)) + @((Get-Item -LiteralPath $openmp))
foreach ($dll in $runtimeFiles | Sort-Object FullName -Unique) {
    $signature = Get-AuthenticodeSignature -LiteralPath $dll.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Visual C++ runtime is not validly signed by Microsoft: $($dll.Name)"
    }
    $bytes = [IO.File]::ReadAllBytes($dll.FullName)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) { throw "Expected x64 runtime: $($dll.Name)" }
    Copy-Item -LiteralPath $dll.FullName -Destination $payload
}
$arguments = @('/Qp', "/DProductVersion=$version", "/DProductName=$($product.name)",
    "/DPublisherName=$($product.publisher)", "/DProductDescription=$($product.description)",
    "/DPublishDirectory=$payload", "/DMetadataDirectory=$metadata", "/DOutputDirectory=$OutputDirectory",
    "/DLicensePath=$([IO.Path]::GetFullPath((Join-Path $dotnetRoot '../../LICENSE')))")
if ($SigningCommand) { $arguments += @('/DSignedBuild', "/Sbunyi=$SigningCommand") }
if ($Cuda) { $arguments += '/DCudaBuild' }
& $CompilerPath @arguments (Join-Path $PSScriptRoot 'Bunyi.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$suffix = if ($Cuda) { '-cuda' } else { '' }
$name = "Bunyi-$version-win-x64$suffix-setup.exe"
$installer = Join-Path $OutputDirectory $name
if ($SigningCommand -and (Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') {
    throw 'Installer signature verification failed.'
}
((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLower() + "  $name") |
    Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Host "Installer: $installer"
