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
param([Parameter(Mandatory)][string]$PackagePath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'MSIX native runtime checks require Windows.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('bunyi-msix-test-' + [guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($package, $work)
# Check the finished archive, not the staging directory. Retain it for diagnosis.
Write-Host "Extracted MSIX for validation: $work"
foreach ($name in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')) {
    $path = Join-Path $work $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "MSIX omitted native runtime: $name" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "MSIX runtime is not validly signed by Microsoft: $name"
    }
    $bytes = [IO.File]::ReadAllBytes($path)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ([BitConverter]::ToUInt16($bytes, $peOffset + 4) -ne 0x8664) { throw "MSIX runtime is not x64: $name" }
}
# A fresh process keeps previously loaded ONNX/Whisper libraries from masking
# problems and releases their file locks. This is not an installed-package or
# clean-VM generation test: the host still supplies Windows system libraries.
& (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $PSScriptRoot 'test-native-runtime.ps1') -AppDirectory $work
if ($LASTEXITCODE -ne 0) { throw 'MSIX native runtime initialization failed.' }
Write-Host 'MSIX runtime payload and native initialization verified.'
