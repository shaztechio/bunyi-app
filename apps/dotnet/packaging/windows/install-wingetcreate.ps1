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

# Download the winget manifest tool used by the release workflow into an explicit
# tools directory. The version is pinned and the hash is the SHA-256 of the
# wingetcreate.exe asset on microsoft/winget-create's v1.12.13.0 GitHub release.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$tool = Join-Path $Destination 'wingetcreate.exe'
Invoke-WebRequest 'https://github.com/microsoft/winget-create/releases/download/v1.12.13.0/wingetcreate.exe' -OutFile $tool
if ((Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash.ToLower() -ne
    '24042bd37915805615e6cf969ac57c6439124c3fe85823327f5f3fb24bd9ffea') {
    Remove-Item -LiteralPath $tool -Force
    throw 'wingetcreate checksum mismatch.'
}
Write-Host "wingetcreate: $tool"
