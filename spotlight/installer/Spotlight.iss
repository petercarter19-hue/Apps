; Spotlight installer (Inno Setup 6). Per-user, no admin. Build with tools\build.ps1.
#define AppName "Spotlight"
#define AppVersion "1.3.1"
#define AppExe "Spotlight.exe"

[Setup]
AppId={{6C1F0E5A-3B7D-4C59-9A21-5D8E2F4B7A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Pete Carter
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
LicenseFile=..\LICENSE
SetupIconFile=..\spotlight.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=out
OutputBaseFilename=Spotlight-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
DisableDirPage=auto
UsePreviousAppDir=yes
CloseApplications=no

[Tasks]
Name: startup; Description: "Start {#AppName} when I sign in"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
; The same per-user entry the tray menu's "Start at sign-in" uses; uninstall removes it either way.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; Flags: dontcreatekey uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Spotlight's own settings and log only; the folder goes if that leaves it empty.
Type: files; Name: "{localappdata}\Spotlight\settings.json"
Type: files; Name: "{localappdata}\Spotlight\log.txt"
Type: files; Name: "{localappdata}\Spotlight\settings.bad.json"
Type: files; Name: "{localappdata}\Spotlight\settings.json.tmp"
Type: dirifempty; Name: "{localappdata}\Spotlight"

[Code]
// Stops only OUR copies of Spotlight (the exact installed exe path), never another program that happens to share the file name.
// Returns how many are still running afterwards (a Spotlight process in this Windows session whose path cannot be read counts as still running).
function StopOurCopies(): Integer;
var
  code: Integer;
  exePath, args: String;
begin
  exePath := ExpandConstant('{app}' + Chr(92) + '{#AppExe}');
  StringChangeEx(exePath, '''', '''''', True);
  StringChangeEx(exePath, Chr($2018), Chr($2018) + Chr($2018), True); // PowerShell also treats curly quotes as quote marks
  StringChangeEx(exePath, Chr($2019), Chr($2019) + Chr($2019), True);
  StringChangeEx(exePath, Chr($201A), Chr($201A) + Chr($201A), True);
  StringChangeEx(exePath, Chr($201B), Chr($201B) + Chr($201B), True);
  args := '-NoProfile -ExecutionPolicy Bypass -Command "$p = @(Get-Process -Name Spotlight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' + exePath + ''' }); $p | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 700; exit @(Get-Process -Name Spotlight -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq ''' + exePath + ''' -or ($_.SessionId -eq (Get-Process -Id $PID).SessionId -and [string]::IsNullOrEmpty($_.Path)) }).Count"';
  if Exec(ExpandConstant('{sys}' + Chr(92) + 'WindowsPowerShell' + Chr(92) + 'v1.0' + Chr(92) + 'powershell.exe'), args, '', SW_HIDE, ewWaitUntilTerminated, code) then
    Result := code
  else
  begin
    // PowerShell could not be started: we cannot check. Only call it fine when the polite quit really released our locks.
    if CheckForMutexes('Spotlight.SingleInstance.v1,Spotlight.Settings.v1,Spotlight.Picker.v1') then Result := 1 else Result := 0;
  end;
end;

// Asks a running copy to quit, waits (up to 15 seconds) until its locks are gone, then stops any of OUR copies still running (for example an
// open area-picker sheet). False = something of ours is still running and could not be stopped.
function AskRunningCopyToQuit(): Boolean;
var
  exe: String;
  code, i: Integer;
begin
  Result := True;
  exe := ExpandConstant('{app}' + Chr(92) + '{#AppExe}');
  if FileExists(exe) then
  begin
    Exec(exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, code);
    for i := 1 to 75 do
    begin
      if not CheckForMutexes('Spotlight.SingleInstance.v1,Spotlight.Settings.v1,Spotlight.Picker.v1') then break;
      Sleep(200);
    end;
    Sleep(500);
    if StopOurCopies() <> 0 then Result := False;
    Sleep(300);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not AskRunningCopyToQuit() then
    Result := 'Spotlight is still running and could not be closed. Please quit it from its tray icon (right-click, Quit Spotlight), then run setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := AskRunningCopyToQuit();
  if not Result then
    MsgBox('Spotlight is still running and could not be closed. Please quit it from its tray icon (right-click, Quit Spotlight), then run the uninstaller again.', mbError, MB_OK);
end;
