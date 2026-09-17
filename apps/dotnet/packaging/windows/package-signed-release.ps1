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

# Run only on a disposable signing runner: test-installer installs/uninstalls.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][string]$CompilerPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PublishRoot = (Resolve-Path -LiteralPath $PublishRoot).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$signScript = Join-Path $PSScriptRoot 'sign-files.ps1'
# Inno expands $q to a quote and $f to the quoted filename (including uninstall).
$signCommand = 'pwsh.exe -NoProfile -File $q' + $signScript + '$q -Path $f'
$desktopNames = @('Bunyi.App.exe', 'Bunyi.App.dll', 'Bunyi.Core.dll')
$cliNames = @('bunyi.exe', 'bunyi.dll', 'Bunyi.Core.dll')
foreach ($flavor in @('cpu', 'cuda')) {
    $cuda = $flavor -eq 'cuda'
    $suffix = if ($cuda) { '-cuda' } else { '' }
    $desktop = Join-Path $PublishRoot "$flavor/win-x64"
    $cli = Join-Path $PublishRoot "$flavor/cli-win-x64"
    $binaries = @($desktopNames | ForEach-Object { Join-Path $desktop $_ }) +
                @($cliNames | ForEach-Object { Join-Path $cli $_ })
    & $signScript -Path $binaries
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -PublishDirectory $desktop `
        -OutputDirectory $OutputDirectory -Version $Version -Cuda:$cuda `
        -CompilerPath $CompilerPath -SigningCommand $signCommand
    $installer = Join-Path $OutputDirectory "Bunyi-$Version-win-x64$suffix-setup.exe"
    & $signScript -Path $installer -VerifyOnly
    & (Join-Path $PSScriptRoot 'test-installer.ps1') -Installer $installer -Cuda:$cuda `
        -SigningThumbprint $env:CERTUM_KEY_ID

    foreach ($distribution in @(
        @{ Name = "Bunyi-$Version-win-x64$suffix"; Source = $desktop; Files = $desktopNames; Cli = $false },
        @{ Name = "Bunyi-CLI-$Version-win-x64$suffix"; Source = $cli; Files = $cliNames; Cli = $true }
    )) {
        $name = $distribution.Name
        $archive = Join-Path $OutputDirectory "$name.zip"
        Compress-Archive -Path (Join-Path $distribution.Source '*') -DestinationPath $archive
        $extracted = Join-Path $PublishRoot "verified/$name"
        Expand-Archive -LiteralPath $archive -DestinationPath $extracted
        & $signScript -Path @($distribution.Files | ForEach-Object { Join-Path $extracted $_ }) -VerifyOnly
        if ($distribution.Cli) {
            & (Join-Path $PSScriptRoot '../test-cli.ps1') -PublishDirectory $extracted
        } else {
            & (Join-Path $PSScriptRoot '../test-defaults.ps1') -Path $extracted
        }
        ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLower() + "  $name.zip") |
            Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    }
}
