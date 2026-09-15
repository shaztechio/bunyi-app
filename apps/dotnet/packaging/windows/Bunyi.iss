; Copyright 2026 Shazron Abdullah and Bunyi contributors
;
; Licensed under the Apache License, Version 2.0 (the "License");
; you may not use this file except in compliance with the License.
; You may obtain a copy of the License at
;
;     http://www.apache.org/licenses/LICENSE-2.0
;
; Unless required by applicable law or agreed to in writing, software
; distributed under the License is distributed on an "AS IS" BASIS,
; WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
; See the License for the specific language governing permissions and
; limitations under the License.

; build-installer.ps1 supplies paths, version and Store metadata.
[Setup]
AppId=app.bunyi.Bunyi.Desktop
AppName={#ProductName}
AppVersion={#ProductVersion}
#ifdef CudaBuild
AppVerName={#ProductName} {#ProductVersion} (CUDA)
InfoBeforeFile=cuda-info.txt
#endif
AppPublisher={#PublisherName}
AppPublisherURL=https://bunyi.app/
AppSupportURL=https://github.com/shaztechio/bunyi-app/issues
VersionInfoDescription={#ProductDescription}
DefaultDirName={localappdata}\Programs\Bunyi
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputDirectory}
#ifdef CudaBuild
OutputBaseFilename=Bunyi-{#ProductVersion}-win-x64-cuda-setup
#else
OutputBaseFilename=Bunyi-{#ProductVersion}-win-x64-setup
#endif
SetupIconFile={#MetadataDirectory}\bunyi.ico
UninstallDisplayIcon={app}\bunyi.ico
LicenseFile={#LicensePath}
AppMutex=Local\Bunyi.Desktop.Running
CloseApplications=no
RestartApplications=no
SetupLogging=yes
#ifdef SignedBuild
SignTool=bunyi
SignedUninstaller=yes
#endif

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked

#ifndef CudaBuild
; Exact installer-owned files only; switching back to CPU must not retain GPU
; providers from a previous CUDA installation. Never remove user data.
[InstallDelete]
Type: files; Name: "{app}\onnxruntime_providers_cuda.dll"
Type: files; Name: "{app}\onnxruntime_providers_cuda.lib"
Type: files; Name: "{app}\onnxruntime_providers_tensorrt.dll"
Type: files; Name: "{app}\onnxruntime_providers_tensorrt.lib"
#endif

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MetadataDirectory}\bunyi.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#LicensePath}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#ProductName}"; Filename: "{app}\Bunyi.App.exe"; IconFilename: "{app}\bunyi.ico"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\Bunyi.App.exe"; IconFilename: "{app}\bunyi.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\Bunyi.App.exe"; Description: "Launch {#ProductName}"; Flags: nowait postinstall skipifsilent
