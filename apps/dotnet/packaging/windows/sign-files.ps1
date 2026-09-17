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
    [Parameter(Mandatory)][string[]]$Path,
    [string]$Thumbprint = $env:CERTUM_KEY_ID,
    [string]$SignToolPath = $env:BUNYI_SIGNTOOL,
    [switch]$VerifyOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Authenticode signing requires Windows.' }
$Thumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
if ($Thumbprint -notmatch '^[0-9A-F]{40}$') { throw 'CERTUM_KEY_ID must be a SHA-1 certificate thumbprint (40 hex characters).' }
if (-not $SignToolPath) {
    # SimplySign exposes its certificate through the x64 provider.
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $candidates = @(Get-ChildItem -Path "$sdk/*/x64/signtool.exe" -File |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending)
    if (-not $candidates.Count) { throw 'Install the Windows SDK x64 SignTool, or set BUNYI_SIGNTOOL.' }
    $SignToolPath = $candidates[0].FullName
}
$SignToolPath = (Resolve-Path -LiteralPath $SignToolPath).Path
$files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if (-not $files.Count) { throw 'No files supplied for signing.' }
if (-not $VerifyOnly) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
        throw 'The signing certificate must be valid and connected to its private key.'
    }
    & $SignToolPath sign /sha1 $Thumbprint /fd SHA256 /tr http://time.certum.pl /td SHA256 @files
    if ($LASTEXITCODE -ne 0) { throw "SignTool signing failed (exit $LASTEXITCODE)." }
}
foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "Invalid signature or unexpected publisher certificate: $file"
    }
    if (-not $signature.TimeStamperCertificate) { throw "Missing trusted timestamp: $file" }
    & $SignToolPath verify /pa /all /tw $file
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed (exit $LASTEXITCODE): $file" }
}
