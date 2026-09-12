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

# Checks the actual publish folder or packed MSIX, not merely the source template.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [ValidateSet('huggingFace', 'mirror')][string] $ExpectedSource = 'huggingFace'
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Path -PathType Container) {
    $json = [IO.File]::ReadAllText((Join-Path $Path 'bunyi.defaults.json'))
}
else {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $entry = $archive.GetEntry('bunyi.defaults.json')
        if ($null -eq $entry) { throw 'The package is missing bunyi.defaults.json.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
}
$document = [System.Text.Json.JsonDocument]::Parse($json)
try {
    $root = $document.RootElement
    $version = $root.GetProperty('schemaVersion')
    $source = $root.GetProperty('modelDownloadSource')
    if ($version.ValueKind -ne 'Number' -or $version.GetInt32() -ne 1 -or
        $source.ValueKind -ne 'String' -or $source.GetString() -cne $ExpectedSource) {
        throw "Expected schemaVersion 1 and modelDownloadSource '$ExpectedSource' in $Path."
    }
}
finally { $document.Dispose() }
Write-Host "Packaged defaults verified: $ExpectedSource ($Path)."
