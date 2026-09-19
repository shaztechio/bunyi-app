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
param([Parameter(Mandatory)][string]$AppDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'Run native initialization checks in x64 PowerShell on Windows.'
}
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class BunyiNativeProbe {
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AddDllDirectory(string path);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr reserved, uint flags);
    public static IntPtr Load(string path) {
        // Search the target DLL's directory, AddDllDirectory, and System32;
        // never the current directory or PATH. Handles live until process exit.
        var handle = LoadLibraryExW(path, IntPtr.Zero, 0x100 | 0x400 | 0x800);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        return handle;
    }
}
'@
if ([BunyiNativeProbe]::AddDllDirectory($app) -eq [IntPtr]::Zero) { throw 'Could not add the app runtime directory.' }
# Load the packaged CRT explicitly before any inference library, so an installed
# redistributable cannot satisfy an omitted or broken package payload.
foreach ($name in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'vcomp140.dll')) {
    $null = [BunyiNativeProbe]::Load((Join-Path $app $name))
}
$null = [BunyiNativeProbe]::Load((Join-Path $app 'onnxruntime.dll'))
$null = [Reflection.Assembly]::LoadFrom((Join-Path $app 'Microsoft.ML.OnnxRuntime.dll'))
# Exercise the same managed NativeMethods initializer seen in the Store failure.
$options = [Microsoft.ML.OnnxRuntime.SessionOptions]::new()
$options.Dispose()
Write-Host 'ONNX Runtime SessionOptions initialized.'
$whisper = @(Get-ChildItem -LiteralPath (Join-Path $app 'runtimes/win-x64') -Filter 'whisper.dll' -Recurse -File)
if (-not $whisper.Count) { throw 'MSIX omitted Windows Whisper natives.' }
foreach ($dll in $whisper) {
    $null = [BunyiNativeProbe]::Load($dll.FullName)
    Write-Host "Whisper native loaded: $($dll.FullName)"
}
