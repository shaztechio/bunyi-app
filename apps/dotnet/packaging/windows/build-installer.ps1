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
if ($deps.runtimeTarget.name -notlike '*/win-x64' -or
    @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.ML.OnnxRuntime.Gpu/*' }).Count) {
    throw 'Only the self-contained win-x64 CPU desktop build can be installed.'
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
$metadata = Join-Path $dotnetRoot ('artifacts/installer-metadata/' + [guid]::NewGuid().ToString('N'))
& $Python (Join-Path $PSScriptRoot '../desktop_metadata.py') --output $metadata
if ($LASTEXITCODE -ne 0) { throw 'Store metadata generation failed.' }
$product = Get-Content -LiteralPath (Join-Path $metadata 'metadata.json') -Raw | ConvertFrom-Json
$arguments = @('/Qp', "/DProductVersion=$version", "/DProductName=$($product.name)",
    "/DPublisherName=$($product.publisher)", "/DProductDescription=$($product.description)",
    "/DPublishDirectory=$publish", "/DMetadataDirectory=$metadata", "/DOutputDirectory=$OutputDirectory",
    "/DLicensePath=$([IO.Path]::GetFullPath((Join-Path $dotnetRoot '../../LICENSE')))")
if ($SigningCommand) { $arguments += @('/DSignedBuild', "/Sbunyi=$SigningCommand") }
& $CompilerPath @arguments (Join-Path $PSScriptRoot 'Bunyi.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$name = "Bunyi-$version-win-x64-setup.exe"
$installer = Join-Path $OutputDirectory $name
if ($SigningCommand -and (Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'Valid') {
    throw 'Installer signature verification failed.'
}
((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLower() + "  $name") |
    Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Host "Installer: $installer"
