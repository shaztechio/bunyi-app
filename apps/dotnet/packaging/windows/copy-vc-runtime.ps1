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
    [Parameter(Mandatory)][string]$Destination,
    [string]$VcRuntimeDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Visual C++ runtime staging requires Windows.' }
if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
    throw 'The runtime destination must be an existing staging directory.'
}
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
$runtimeFiles = @((Get-ChildItem -LiteralPath $VcRuntimeDirectory -Filter '*.dll' -File)) + @((Get-Item -LiteralPath $openmp))
foreach ($dll in $runtimeFiles | Sort-Object FullName -Unique) {
    $signature = Get-AuthenticodeSignature -LiteralPath $dll.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "Visual C++ runtime is not validly signed by Microsoft: $($dll.Name)"
    }
    $bytes = [IO.File]::ReadAllBytes($dll.FullName)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) { throw "Expected x64 runtime: $($dll.Name)" }
    Copy-Item -LiteralPath $dll.FullName -Destination $Destination
}
