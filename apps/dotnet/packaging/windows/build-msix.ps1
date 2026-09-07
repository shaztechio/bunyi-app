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
    [switch]$LocalTest,
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$SdkBinPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'MSIX packaging requires Windows.' }

$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$PackageName = [string]$manifest.Package.Identity.Name
$Publisher = [string]$manifest.Package.Identity.Publisher
$PublisherDisplayName = [string]$manifest.Package.Properties.PublisherDisplayName
$DisplayName = [string]$manifest.Package.Properties.DisplayName
if ($LocalTest) {
    $PackageName = 'Bunyi.LocalTest'
    $Publisher = 'CN=Bunyi Local Test'
    $PublisherDisplayName = 'Shazron'
    $DisplayName = 'Bunyi (Local Test)'
}
# Fail before publishing if the SDK tools are unavailable. The SDK can be
# installed normally or unpacked from Microsoft.Windows.SDK.BuildTools.
if (-not $SdkBinPath) {
    $sdkCommand = Get-Command MakeAppx.exe -ErrorAction SilentlyContinue
    if ($sdkCommand) { $SdkBinPath = Split-Path $sdkCommand.Source }
    else {
        $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $sdkVersions = @(Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending)
        foreach ($sdkVersion in $sdkVersions) {
            $candidate = Join-Path $sdkVersion.FullName 'x64'
            if ((Test-Path "$candidate/MakeAppx.exe") -and (Test-Path "$candidate/MakePri.exe")) {
                $SdkBinPath = $candidate
                break
            }
        }
    }
}
if (-not $SdkBinPath) { throw 'Install the Windows SDK or supply -SdkBinPath containing MakeAppx.exe and MakePri.exe.' }
$makeAppx = Join-Path $SdkBinPath 'MakeAppx.exe'
$makePri = Join-Path $SdkBinPath 'MakePri.exe'
foreach ($tool in @($makeAppx, $makePri)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing SDK tool: $tool" }
}

[xml]$props = Get-Content -LiteralPath (Join-Path $dotnetRoot 'Directory.Build.props') -Raw
$versionPrefix = [string]$props.Project.PropertyGroup.VersionPrefix
if ($versionPrefix -notmatch '^\d+\.\d+\.\d+$') {
    throw "Expected a release VersionPrefix (major.minor.patch), got '$versionPrefix'."
}
$packageVersion = [version]"$versionPrefix.0"
if ($packageVersion.Major -lt 1 -or $packageVersion.Major -gt 65535 -or
    $packageVersion.Minor -gt 65535 -or $packageVersion.Build -gt 65535) {
    throw 'MSIX version components must fit UInt16, with a nonzero major and zero revision.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $dotnetRoot 'artifacts/msix' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($PublishDirectory) {
    $publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
    $publishPrefix = $publish.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($OutputDirectory.Equals($publish, [StringComparison]::OrdinalIgnoreCase) -or
        $OutputDirectory.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must not be inside PublishDirectory; that would copy staging into itself.'
    }
}
# Keep each staging folder for inspection. Never delete or reuse caller-owned output.
$runDirectory = Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N'))
$stage = Join-Path $runDirectory 'package'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if (-not $PublishDirectory) {
    $publish = Join-Path $runDirectory 'publish'
    & dotnet publish (Join-Path $dotnetRoot 'src/App') -c Release -r win-x64 --self-contained `
        -p:BunyiCuda=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
foreach ($required in @('Bunyi.App.exe', 'Bunyi.App.dll', 'Bunyi.App.deps.json',
    'Bunyi.App.runtimeconfig.json', 'coreclr.dll', 'onnxruntime.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) {
        throw "Publish output is missing $required. Supply a self-contained win-x64 CPU publish folder."
    }
}
$deps = Get-Content -LiteralPath (Join-Path $publish 'Bunyi.App.deps.json') -Raw | ConvertFrom-Json
if ($deps.runtimeTarget.name -notlike '*/win-x64' -or
    @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.ML.OnnxRuntime.Gpu/*' }).Count) {
    throw 'Only the win-x64 CPU build is supported for MSIX.'
}
$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'Bunyi.App.dll')).FileVersion
if ([version]$binaryVersion -ne $packageVersion) {
    throw "Published binary version $binaryVersion does not match package version $packageVersion. Republish the app."
}
Get-ChildItem -LiteralPath $publish | Copy-Item -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Destination $stage -Recurse

$manifest.Package.Identity.SetAttribute('Name', $PackageName)
$manifest.Package.Identity.SetAttribute('Publisher', $Publisher)
$manifest.Package.Identity.SetAttribute('Version', $packageVersion.ToString())
$manifest.Package.Properties.DisplayName = $DisplayName
$manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$visual = $manifest.GetElementsByTagName('VisualElements', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')[0]
$visual.SetAttribute('DisplayName', $DisplayName)
$manifestPath = Join-Path $stage 'AppxManifest.xml'
$manifest.Save($manifestPath)

# Index scale and target-size variants so Windows chooses a crisp icon per surface.
$priConfig = Join-Path $runDirectory 'priconfig.xml'
& $makePri createconfig /cf $priConfig /dq en-US /o
if ($LASTEXITCODE -ne 0) { throw 'MakePri createconfig failed.' }
# One MSIX carries every scale. The SDK defaults to separate resource-pack
# indexes, which would leave high-DPI assets outside the main resource map.
[xml]$config = Get-Content -LiteralPath $priConfig -Raw
$null = $config.resources.RemoveChild($config.resources.packaging)
$config.Save($priConfig)
& $makePri new /pr $stage /cf $priConfig /mn $manifestPath /of (Join-Path $stage 'resources.pri') /o
if ($LASTEXITCODE -ne 0) { throw 'MakePri resource indexing failed.' }
$suffix = if ($LocalTest) { '-local-test' } else { '-store' }
$packagePath = Join-Path $OutputDirectory "Bunyi-$packageVersion-win-x64$suffix.msix"
& $makeAppx pack /d $stage /p $packagePath /o
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx packaging/validation failed.' }
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($packagePath))" | Set-Content -LiteralPath "$packagePath.sha256" -Encoding ascii
Write-Host "Unsigned MSIX: $packagePath"
Write-Host "Staged manifest: $manifestPath"
if ($LocalTest) { Write-Warning 'Local-test identity: not a Store submission. Sign and trust a test certificate before installing.' }

