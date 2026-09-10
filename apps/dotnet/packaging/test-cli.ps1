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

# Runs against the published folder, including folders extracted from release
# archives. It needs neither a model, desktop session, nor installed .NET runtime.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $PublishDirectory)

$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executable = Join-Path $publishPath $(if ($IsWindows) { 'bunyi.exe' } else { 'bunyi' })
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "CLI executable is missing: $executable" }
if (Get-ChildItem -LiteralPath $publishPath -Filter 'Avalonia*.dll' -Recurse) { throw 'The CLI contains Avalonia dependencies.' }
$rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
$whisperFolder = Join-Path $publishPath "runtimes/$rid"
if (-not (Test-Path -LiteralPath $whisperFolder) -or
    -not (Get-ChildItem -LiteralPath $whisperFolder -Filter '*whisper*' -Recurse)) {
    throw "CLI is missing Whisper natives for $rid."
}
foreach ($native in Get-ChildItem -LiteralPath (Join-Path $publishPath 'runtimes') -Filter '*whisper*' -Recurse -File) {
    if (-not $native.FullName.StartsWith($whisperFolder + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "CLI contains a foreign Whisper native: $($native.FullName)"
    }
}

$smokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('bunyi-publish-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokeRoot | Out-Null

function Invoke-BunyiJson {
    param([string[]] $CommandArgs, [int] $ExpectedExit = 0)
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['BUNYI_DATA_DIR'] = $smokeRoot
    foreach ($argument in $CommandArgs) { $start.ArgumentList.Add($argument) }
    $start.ArgumentList.Add('--json')
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw "CLI timed out: $($CommandArgs -join ' ')"
        }
        if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($outputTask, $errorTask)).Wait(30000)) {
            throw "CLI streams remained open after exit: $($CommandArgs -join ' ')"
        }
        $output = $outputTask.GetAwaiter().GetResult()
        $diagnostics = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne $ExpectedExit) {
            throw "CLI exited $($process.ExitCode), expected ${ExpectedExit}: $output $diagnostics"
        }
        $result = $output | ConvertFrom-Json -ErrorAction Stop
        if ($result.schemaVersion -ne 1 -or -not $result.type -or -not $result.operationId) {
            throw "Invalid CLI result: $output"
        }
        return $result
    }
    finally { $process.Dispose() }
}

$started = $false
try {
    $version = Invoke-BunyiJson -CommandArgs @('version')
    if (-not $version.ok) { throw 'Version command failed.' }
    $invalid = Invoke-BunyiJson -CommandArgs @('generate', 'preset') -ExpectedExit 3
    if ($invalid.ok -or -not $invalid.error.code) { throw 'Invalid input did not produce a structured error.' }
    $null = Invoke-BunyiJson -CommandArgs @('models', 'status', '--one-shot')
    $null = Invoke-BunyiJson -CommandArgs @('server', 'start')
    $started = $true
    $null = Invoke-BunyiJson -CommandArgs @('server', 'start')
    $null = Invoke-BunyiJson -CommandArgs @('server', 'status')
    $null = Invoke-BunyiJson -CommandArgs @('models', 'status', '--require-server')
    $null = Invoke-BunyiJson -CommandArgs @('server', 'stop')
    $started = $false
    Write-Host "CLI distribution passed version, input validation, model status, and server lifecycle checks ($rid)."
}
finally {
    if ($started) {
        try { $null = Invoke-BunyiJson -CommandArgs @('server', 'stop') }
        catch { Write-Warning "Smoke-test server cleanup failed: $_" }
    }
}
