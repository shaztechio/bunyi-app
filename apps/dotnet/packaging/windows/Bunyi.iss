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
;
; The two editions are separate Installed Apps entries (separate AppIds) so that
; package managers can tell them apart, but they share one directory, one set of
; shortcuts and all user data, and installing either removes the other first.
; The uninstall display names carry no version and no parentheses on purpose:
; winget normalises both away when it matches an installed app by name.
[Setup]
#ifdef CudaBuild
AppId=app.bunyi.Bunyi.Desktop.Cuda
UninstallDisplayName={#ProductName} CUDA
AppVerName={#ProductName} {#ProductVersion} (CUDA)
InfoBeforeFile=cuda-info.txt
#else
AppId=app.bunyi.Bunyi.Desktop
UninstallDisplayName={#ProductName}
#endif
AppName={#ProductName}
AppVersion={#ProductVersion}
AppPublisher={#PublisherName}
AppPublisherURL=https://bunyi.app/
AppSupportURL=https://github.com/shaztechio/bunyi-app/issues
VersionInfoDescription={#ProductDescription}
DefaultDirName={code:GetDefaultDir}
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

[Code]
const
#ifdef CudaBuild
  OtherAppId = 'app.bunyi.Bunyi.Desktop';
#else
  OtherAppId = 'app.bunyi.Bunyi.Desktop.Cuda';
#endif
  UninstallRoot = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\';

function OtherEditionKey: String;
begin
  Result := UninstallRoot + OtherAppId + '_is1';
end;

{ Inno only reuses the previous directory for the same AppId, so adopt the other
  edition's. An explicit /DIR still wins over this default. }
function GetDefaultDir(Param: String): String;
var
  Location: String;
begin
  Result := ExpandConstant('{localappdata}\Programs\Bunyi');
  if RegQueryStringValue(HKCU, OtherEditionKey, 'Inno Setup: App Path', Location) then
  begin
    Location := RemoveBackslashUnlessRoot(Location);
    if (Location <> '') and DirExists(Location) then
      Result := Location;
  end;
end;

{ Keep the desktop shortcut across an edition switch, unless /TASKS says otherwise. }
procedure InitializeWizard;
var
  Tasks: String;
begin
  if (ExpandConstant('{param:TASKS|}') = '') and
     RegQueryStringValue(HKCU, OtherEditionKey, 'Inno Setup: Selected Tasks', Tasks) and
     (Pos('desktopicon', Tasks) > 0) then
    WizardSelectTasks('desktopicon');
end;

{ Removes the other edition, which also migrates a CUDA install that predates
  the split and is registered under the standard AppId. Its uninstaller leaves
  models, voices, recordings and settings alone. Setup stops if it fails. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Command: String;
  ResultCode, Attempt: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, OtherEditionKey, 'UninstallString', Command) then
    Exit;
  Command := RemoveQuotes(Command);
  if not Exec(Command, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE,
              ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Result := 'Setup could not remove the other Bunyi edition. Close Bunyi, then try again.';
    Exit;
  end;
  { The uninstaller finishes deleting itself just after it exits. }
  for Attempt := 1 to 40 do
  begin
    if not RegKeyExists(HKCU, OtherEditionKey) then
      Exit;
    Sleep(250);
  end;
  Result := 'The other Bunyi edition is still registered after its uninstaller ran.';
end;
