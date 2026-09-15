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

# Run on a disposable Windows runner. Refuse to touch an existing installation.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer, [string]$PreviousInstaller)
$ErrorActionPreference = 'Stop'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\app.bunyi.Bunyi.Desktop_is1'
if (Test-Path $key) { throw 'Bunyi is already installed; use a clean test account.' }
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$expectedHash = ((Get-Content -LiteralPath "$installerPath.sha256" -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'Installer checksum mismatch.'
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('bunyi-installer-test-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $work 'app'
New-Item -ItemType Directory -Path $work | Out-Null
$sentinels = @()
foreach ($root in @((Join-Path $env:LOCALAPPDATA 'Bunyi'), (Join-Path $env:APPDATA 'Bunyi'))) {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $sentinel = Join-Path $root ([guid]::NewGuid().ToString('N') + '.installer-test')
    Set-Content -LiteralPath $sentinel -Value 'retain user data'
    $sentinels += $sentinel
}
function Invoke-Setup([string]$Path, [string[]]$Extra = @()) {
    $process = Start-Process -FilePath $Path -WindowStyle Hidden -PassThru -ArgumentList (@(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $work + '/' + [guid]::NewGuid() + '.log"')) + $Extra)
    if (-not $process.WaitForExit(120000)) { throw 'Installer did not finish within two minutes.' }
    return $process.ExitCode
}
try {
    if ($PreviousInstaller) {
        $previous = (Resolve-Path -LiteralPath $PreviousInstaller).Path
        if ((Invoke-Setup $previous @('/DIR="' + $install + '"')) -ne 0) { throw 'Previous version install failed.' }
        $oldVersion = (Get-ItemProperty $key).DisplayVersion
        if ((Invoke-Setup $installerPath) -ne 0) { throw 'Version upgrade failed.' }
        $newVersion = (Get-ItemProperty $key).DisplayVersion
        if ([version]$newVersion -le [version]$oldVersion) { throw 'Upgrade did not advance the registered version.' }
        Write-Host "Version upgrade verified: $oldVersion -> $newVersion"
        if ([version]([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'Bunyi.App.dll')).FileVersion) -ne
            [version]"$newVersion.0") { throw 'Upgrade left an old application binary.' }
    }
    if ((Invoke-Setup $installerPath @('/DIR="' + $install + '"', '/TASKS=desktopicon')) -ne 0) { throw 'Install failed.' }
    if (-not (Test-Path $key)) { throw 'Installed Apps registration missing.' }
    & (Join-Path $PSScriptRoot '../test-defaults.ps1') -Path $install
    foreach ($dll in @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $install $dll))) { throw "Installer omitted native runtime: $dll" }
        if ((Get-AuthenticodeSignature -LiteralPath (Join-Path $install $dll)).Status -ne 'Valid') {
            throw "Invalid Microsoft runtime signature: $dll"
        }
    }
    # Load the installed inference/audio natives too: desktop startup alone
    # doesn't load ONNX or Whisper and can hide missing CRT/OpenMP dependencies.
    $nativeHandles = [Collections.Generic.List[IntPtr]]::new()
    try {
        foreach ($dll in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'vcomp140.dll',
                            'onnxruntime.dll', 'miniaudio.dll', 'runtimes/win-x64/ggml-base-whisper.dll',
                            'runtimes/win-x64/ggml-cpu-whisper.dll', 'runtimes/win-x64/ggml-whisper.dll',
                            'runtimes/win-x64/whisper.dll')) {
            $nativeHandles.Add([Runtime.InteropServices.NativeLibrary]::Load((Join-Path $install $dll)))
        }
    }
    finally {
        for ($i = $nativeHandles.Count - 1; $i -ge 0; $i--) { [Runtime.InteropServices.NativeLibrary]::Free($nativeHandles[$i]) }
    }
    foreach ($shortcut in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Bunyi.lnk'),
                             (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Bunyi.lnk'))) {
        if (-not (Test-Path -LiteralPath $shortcut)) { throw "Missing shortcut: $shortcut" }
    }
    # Reinstall must repair its own payload, retaining the existing install path.
    Set-Content -LiteralPath (Join-Path $install 'LICENSE') -Value 'damaged payload'
    if ((Invoke-Setup $installerPath) -ne 0) { throw 'Reinstall failed.' }
    if ((Get-Content (Join-Path $install 'LICENSE') -Raw) -notmatch 'Apache License') { throw 'Reinstall did not repair the payload.' }
    $app = Start-Process -FilePath (Join-Path $install 'Bunyi.App.exe') -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Seconds 5
        if ($app.HasExited) { throw 'The installed desktop exited during startup.' }
        $liveGuard = $null
        if (-not [Threading.Mutex]::TryOpenExisting('Local\Bunyi.Desktop.Running', [ref]$liveGuard)) {
            throw 'The installed desktop did not create its installer guard.'
        }
        $liveGuard.Dispose()
    }
    finally {
        # This test-owned process has not generated speech or downloaded models.
        # Prefer an ordinary close; clean up only this idle smoke process if the
        # runner's window station cannot deliver it.
        if (-not $app.HasExited) {
            [void]$app.CloseMainWindow()
            if (-not $app.WaitForExit(10000)) { $app.Kill(); $app.WaitForExit() }
        }
        $app.Dispose()
    }
    $uninstaller = Join-Path $install 'unins000.exe'
    $guard = [Threading.Mutex]::new($false, 'Local\Bunyi.Desktop.Running')
    try {
        if ((Invoke-Setup $installerPath) -eq 0) { throw 'Setup ignored a running Bunyi instance.' }
        if ((Invoke-Setup $uninstaller) -eq 0) { throw 'Uninstall ignored a running Bunyi instance.' }
    }
    finally { $guard.Dispose() }
    if ((Invoke-Setup $uninstaller) -ne 0) { throw 'Uninstall failed.' }
    # Inno's uninstaller may finish its final self-deletion just after exit.
    for ($attempt = 0; $attempt -lt 20 -and (Test-Path $key); $attempt++) { Start-Sleep -Milliseconds 250 }
    if ((Test-Path $key) -or (Test-Path (Join-Path $install 'Bunyi.App.exe'))) { throw 'Uninstall left the application registered or installed.' }
    foreach ($shortcut in @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Bunyi.lnk'),
                             (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Bunyi.lnk'))) {
        if (Test-Path -LiteralPath $shortcut) { throw "Uninstall left shortcut: $shortcut" }
    }
    foreach ($sentinel in $sentinels) {
        if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'retain user data') { throw 'User data was modified.' }
    }
    Write-Host "Install, repair, busy protection and uninstall passed. Logs: $work"
}
finally {
    # Remove only files created by this test, never user data directories.
    foreach ($sentinel in $sentinels) { Remove-Item -LiteralPath $sentinel -ErrorAction SilentlyContinue }
}
