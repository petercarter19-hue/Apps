; Chirpy-Setup.exe: the normal Windows installer for Chirpy (Inno Setup 6). Unsigned. Build it with tools/build_chirpy_setup.ps1.
;
; What it does, for the person double-clicking it:
;   - installs for this user only (no administrator), into %LOCALAPPDATA%\Programs\Chirpy unless they choose another folder;
;   - adds "Chirpy" and "Chirpy Read Me" to the Start menu; an optional, unchecked "Start Chirpy when I sign in"; starts Chirpy at the end;
;   - refuses, installing nothing, while the everyday VoiceTool runs (both can't listen for keys at once);
;   - carries words, fixes, history and settings over from an earlier VoiceTool Light install, with setup.ps1's own
;     carry-over code (setup.ps1 -CarryOverOnly): copy, never overwrite, leave the earlier folder alone, say what stayed behind;
;   - puts Chirpy in Windows' Apps list; its uninstaller removes only Chirpy's program files and asks whether to keep the
;     person's words, fixes, history and settings (Yes is the default).
; Command line (silent installs and tests): /VERYSILENT /SUPPRESSMSGBOXES /DIR="..." /TASKS="startatsignin" /NOLAUNCH
;   /FROM="<earlier VoiceTool Light folder>"; the uninstaller takes /VERYSILENT /SUPPRESSMSGBOXES and /DELETEDATA or /KEEPDATA.
#ifndef PackageDir
  #error Pass /DPackageDir=<the built package folder, the one that holds Setup.cmd>; tools/build_chirpy_setup.ps1 does.
#endif
#ifndef ChirpyVersion
  #define ChirpyVersion "1.1.0"
#endif
#ifndef ChirpyBuild
  #define ChirpyBuild "unknown"
#endif
#ifndef NumericVersion
  #define NumericVersion "1.1.0.0"
#endif
#ifndef ImagesDir
  #error Pass /DImagesDir=<folder with the wizard pictures>; tools/build_chirpy_setup.ps1 does.
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
AppId={{1B49448A-3E6E-474E-AF3D-07B9227A08A9}
AppName=Chirpy
AppVersion={#ChirpyVersion}
AppVerName=Chirpy {#ChirpyVersion}
AppPublisher=Chirpy
DefaultDirName={localappdata}\Programs\Chirpy
; the license page (MIT, Chirpy's own code): shown as the first wizard page; its text is also installed as LICENSE.txt
LicenseFile={#PackageDir}\LICENSE.txt
DisableProgramGroupPage=yes
DirExistsWarning=no
; (Inno would otherwise re-tick the earlier run's tasks on a silent reinstall, so an unticked sign-in box or /TASKS="" could never be told from the old choice; the wizard re-ticks it itself when the entry exists)
UsePreviousTasks=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=Chirpy-Setup
SetupIconFile={#PackageDir}\app\voicetool.ico
UninstallDisplayIcon={app}\voicetool.ico
UninstallDisplayName=Chirpy
WizardStyle=modern
WizardImageFile={#ImagesDir}\wizard-large.bmp,{#ImagesDir}\wizard-large-2x.bmp
WizardSmallImageFile={#ImagesDir}\wizard-small.bmp,{#ImagesDir}\wizard-small-2x.bmp
Compression=lzma2/normal
SolidCompression=yes
SetupMutex=Chirpy-Setup-is-running
CloseApplications=no
RestartApplications=no
VersionInfoVersion={#NumericVersion}
VersionInfoCompany=Chirpy
VersionInfoProductName=Chirpy
VersionInfoDescription=Chirpy setup
VersionInfoTextVersion={#ChirpyVersion} ({#ChirpyBuild})
VersionInfoProductTextVersion={#ChirpyVersion} ({#ChirpyBuild})

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; (the stock finish-page line says the app "may be launched by selecting the installed shortcuts", right above a ticked "Start Chirpy now")
FinishedLabel=Setup has finished installing [name] on your computer.

[Tasks]
Name: "startatsignin"; Description: "Start Chirpy when I sign in"; Flags: unchecked

[Files]
; the carry-over code, run from setup.ps1 itself (it is extracted to a temporary folder, never installed)
Source: "{#PackageDir}\setup.ps1"; Flags: dontcopy
; the app (its own settings file is only put down when there is none: a carried-over or earlier one stays, and stays at uninstall)
Source: "{#PackageDir}\app\*"; DestDir: "{app}"; Excludes: "config.json,__pycache__"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\app\config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PackageDir}\python\*"; DestDir: "{app}\python"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\models\*"; DestDir: "{app}\models"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PackageDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PackageDir}\README.txt"; DestDir: "{app}"; Flags: ignoreversion
; the file that marks a folder as Chirpy's (version inside); it stays after an uninstall that keeps the person's own files,
; so installing again into that folder is allowed and finds them
Source: "{#PackageDir}\VERSION.txt"; DestDir: "{app}"; DestName: ".voicetool-light-from-daily"; Flags: ignoreversion uninsneveruninstall

[InstallDelete]
; an earlier Setup.cmd install's own uninstaller: Windows' Apps list has this one's now
Type: files; Name: "{app}\uninstall.ps1"
Type: files; Name: "{app}\Uninstall Chirpy.cmd"

[Icons]
Name: "{autoprograms}\Chirpy"; Filename: "{app}\python\pythonw.exe"; Parameters: "-s -E ""{app}\voicetool.py"" --settings"; WorkingDir: "{app}"; IconFilename: "{app}\voicetool.ico"; Comment: "Chirpy: talk anywhere and it types. Opens the Chirpy window."
Name: "{autoprograms}\Chirpy Read Me"; Filename: "{app}\README.txt"; Comment: "What Chirpy does, how to use it and what stays on your PC."

[Run]
Filename: "{app}\python\pythonw.exe"; Parameters: "-s -E ""{app}\voicetool.py"" --settings"; WorkingDir: "{app}"; Description: "Start Chirpy now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; what Chirpy made by running (the person's own words, fixes, history and settings are not here: they are asked about)
Type: filesandordirs; Name: "{app}\python"
Type: filesandordirs; Name: "{app}\models"
Type: filesandordirs; Name: "{app}\__pycache__"
Type: filesandordirs; Name: "{app}\ui"
Type: files; Name: "{app}\voicetool.log*"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  OldRunName = 'VoiceTool Light';
  Marker = '.voicetool-light-from-daily';
  EverydayText = 'Your everyday VoiceTool is running.' + #13#10 + 'Quit it first (right-click its microphone icon by the clock,' + #13#10 +
    'then choose Quit VoiceTool), then run Setup again.' + #13#10 + 'Nothing was installed.';

var
  CarryPage: TOutputMsgMemoWizardPage;
  CarryNotes: TArrayOfString;
  CarriedSignIn: Boolean;
  SignInPreset: Boolean;
  DeleteData: Boolean;

function ChirpyOpenMutex(Access: Cardinal; Inherit: Integer; Name: String): THandle;
  external 'OpenMutexW@kernel32.dll stdcall';
function ChirpyCloseHandle(Handle: THandle): Integer;
  external 'CloseHandle@kernel32.dll stdcall';
function ChirpySetEnvW(Name, Value: String): Integer;
  external 'SetEnvironmentVariableW@kernel32.dll stdcall';

function HasSwitch(Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
      Result := True;
end;

procedure ChirpySetEnv(Name, Value: String);
begin
  ChirpySetEnvW(Name, Value);
end;

// Did the command line name its tasks at all (/TASKS=..., even an empty list)? ExpandConstant's {param:} can't tell an empty
// list from no switch.
function TasksNamed: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(Copy(ParamStr(I), 1, 7), '/TASKS=') = 0 then
      Result := True;
end;

// Is "Start Chirpy when I sign in" ticked? In the wizard: the box. In a silent install: only when /TASKS= lists it. (Inno's own
// WizardIsTaskSelected says "ticked" in a silent reinstall even for /TASKS="" or no /TASKS, so it can't be used there; found
// by the Sandbox.)
function SignInTicked: Boolean;
begin
  if WizardSilent then
    Result := TasksNamed and (Pos(',startatsignin,', ',' + Lowercase(ExpandConstant('{param:TASKS|}')) + ',') > 0)
  else
    Result := WizardIsTaskSelected('startatsignin');
end;

function Quoted(Text: String): String;
begin
  Result := '"' + Text + '"';
end;

// One command-line argument for a program started without a shell: in quotes, and a trailing backslash (a drive root such as
// D:\) doubled so it can't swallow the closing quote. Windows file names can't hold a quote, so nothing else needs escaping.
function Arg(Text: String): String;
begin
  if (Text <> '') and (Text[Length(Text)] = '\') then
    Text := Text + '\';
  Result := '"' + Text + '"';
end;

// The everyday VoiceTool holds this name while it runs (setup.ps1 looks for the same one).
function EverydayVoiceToolRunning: Boolean;
var
  Handle: THandle;
begin
  Handle := ChirpyOpenMutex($00100000, 0, 'VoiceTool-single-instance');  // SYNCHRONIZE
  Result := Handle <> 0;
  if Result then
    ChirpyCloseHandle(Handle);
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if EverydayVoiceToolRunning then
  begin
    Log(EverydayText);
    SuppressibleMsgBox(EverydayText, mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

function FolderHasFiles(Dir: String): Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if FindFirst(Dir + '\*', Found) then
  try
    repeat
      if (Found.Name <> '.') and (Found.Name <> '..') then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

// A new folder, an empty one, or one Chirpy was installed in: never a folder that already holds someone else's files.
function FolderIsFine(Dir: String): Boolean;
begin
  Result := (not DirExists(Dir)) or (not FolderHasFiles(Dir)) or FileExists(Dir + '\' + Marker);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
    if not FolderIsFine(RemoveBackslash(WizardDirValue)) then
    begin
      SuppressibleMsgBox(RemoveBackslash(WizardDirValue) + ' already has other files in it. Choose an empty folder or a folder Chirpy was installed in.', mbError, MB_OK, IDOK);
      Result := False;
    end;
end;

// Runs setup.ps1 in one of its installer modes (CarryOverOnly, RetireOnly, FindChirpy). Returns its exit code; what it printed
// is in Said, what it wrote to -ResultFile in Lines.
function RunSetupScript(Mode, Extra: String; var Said, Lines: TArrayOfString): Integer;
var
  Script, OutFile, ResultFile: String;
  Code: Integer;
begin
  ExtractTemporaryFile('setup.ps1');
  Script := ExpandConstant('{tmp}\setup.ps1');
  OutFile := ExpandConstant('{tmp}\setup-script-out.txt');
  ResultFile := ExpandConstant('{tmp}\setup-script-result.txt');
  DeleteFile(OutFile);
  DeleteFile(ResultFile);
  SetArrayLength(Said, 0);
  SetArrayLength(Lines, 0);
  // powershell.exe is started directly (never through cmd.exe, which would expand %NAME% inside a folder name): its own words
  // come back through -SaidFile instead of a redirection
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File ' + Arg(Script) + ' ' + Mode + ' -ResultFile ' + Arg(ResultFile) +
      ' -SaidFile ' + Arg(OutFile) + Extra, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  LoadStringsFromFile(OutFile, Said);
  LoadStringsFromFile(ResultFile, Lines);
  Result := Code;
end;

// A Chirpy installed by Setup.cmd (or by this installer) somewhere other than the usual folder: its Start menu or sign-in entry
// names it. Unless the person gave /DIR= or this installer already knows the folder, it is the folder offered, so Setup updates
// it in place instead of making a second Chirpy beside it.
procedure UseExistingChirpyFolder;
var
  Code, At: Integer;
  Dir: String;
  Lines, Said: TArrayOfString;
begin
  if ExpandConstant('{param:DIR|}') <> '' then
    Exit;
  if RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{1B49448A-3E6E-474E-AF3D-07B9227A08A9}_is1', 'Inno Setup: App Path') then
    Exit;
  Code := RunSetupScript('-FindChirpy', '', Said, Lines);
  if (Code = 0) and (GetArrayLength(Lines) > 0) then
  begin
    Dir := Trim(Lines[0]);
    At := Pos(':\', Dir);
    if At > 1 then
      Dir := Copy(Dir, At - 1, Length(Dir));  // (a byte-order mark in front of the drive letter is dropped)
    if (Dir <> '') and DirExists(Dir) and FileExists(Dir + '\' + Marker) then
    begin
      Log('An existing Chirpy was found in ' + Dir + ': it is updated in place.');
      WizardForm.DirEdit.Text := Dir;
    end;
  end;
end;

procedure InitializeWizard;
begin
  UseExistingChirpyFolder;
  CarryPage := CreateOutputMsgMemoPage(wpInstalling, 'Your earlier install', 'What Chirpy did with VoiceTool Light',
    'Setup found an earlier VoiceTool Light install:', '');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = CarryPage.ID) and (GetArrayLength(CarryNotes) = 0);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  I: Integer;
  Text: String;
begin
  // somebody who started VoiceTool Light at sign-in gets the same for Chirpy (they can untick it)
  if (CurPageID = wpSelectTasks) and (not SignInPreset) then
  begin
    SignInPreset := True;
    if RegValueExists(HKCU, RunKey, OldRunName) or RegValueExists(HKCU, RunKey, 'Chirpy') then
      WizardSelectTasks('startatsignin');
  end;
  if CurPageID = CarryPage.ID then
  begin
    Text := '';
    for I := 0 to GetArrayLength(CarryNotes) - 1 do
      Text := Text + CarryNotes[I] + #13#10 + #13#10;
    CarryPage.RichEditViewer.Lines.Text := Text;
  end;
end;

// Runs setup.ps1 -CarryOverOnly before any file is put down: the same checks, the same stopping of a copy that is running, and
// the same carry-over as Setup.cmd (nothing is copied by it; it marks the folder as Chirpy's, so a canceled or failed install
// leaves a folder Setup accepts again). A refusal comes back as plain words and stops the install before it begins.
procedure RunCarryOver;
var
  From, Extra, Text: String;
  Code, I: Integer;
  Lines, Said: TArrayOfString;
begin
  From := ExpandConstant('{param:From|}');
  Extra := ' -InstallDir ' + Arg(RemoveBackslash(WizardDirValue));
  if From <> '' then
    Extra := Extra + ' -From ' + Arg(From);
  Code := RunSetupScript('-CarryOverOnly', Extra, Said, Lines);
  if Code <> 0 then
  begin
    Text := '';
    for I := 0 to GetArrayLength(Said) - 1 do
      if Trim(Said[I]) <> '' then
        Text := Text + Said[I] + #13#10;
    if Text = '' then
      Text := 'Setup could not get the install folder ready. Nothing was installed.';
    Log('Carry-over step failed (' + IntToStr(Code) + '): ' + Text);
    SuppressibleMsgBox(Text, mbError, MB_OK, IDOK);
    Abort;
  end;
  SetArrayLength(CarryNotes, 0);
  for I := 1 to GetArrayLength(Lines) - 1 do
    if Trim(Lines[I]) <> '' then
    begin
      SetArrayLength(CarryNotes, GetArrayLength(CarryNotes) + 1);
      CarryNotes[GetArrayLength(CarryNotes) - 1] := Lines[I];
      Log('Carry-over: ' + Lines[I]);
    end;
end;

// After the install has worked (never before: a canceled or failed install leaves the earlier VoiceTool Light exactly as it
// was): its Start menu entry and sign-in entry give way to Chirpy's. A sign-in entry it had becomes Chirpy's, unless the person
// unticked the box.
procedure RunRetire;
var
  Code: Integer;
  Lines, Said: TArrayOfString;
begin
  CarriedSignIn := False;
  Code := RunSetupScript('-RetireOnly', ' -InstallDir ' + Arg(RemoveBackslash(WizardDirValue)), Said, Lines);
  if Code <> 0 then
    Log('Taking away the earlier VoiceTool Light entries did not finish (' + IntToStr(Code) + '); the new install is complete.')
  else if (GetArrayLength(Lines) > 0) and (Pos('signin=1', Lines[0]) > 0) then
    CarriedSignIn := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  PythonwPath, Script, RunValue: String;
begin
  if CurStep = ssInstall then
    RunCarryOver;
  if CurStep = ssPostInstall then
  begin
    RunRetire;
    PythonwPath := ExpandConstant('{app}\python\pythonw.exe');
    Script := ExpandConstant('{app}\voicetool.py');
    Log('Sign-in entry: silent=' + IntToStr(Ord(WizardSilent)) + ', ticked=' + IntToStr(Ord(SignInTicked)) + ', carried=' + IntToStr(Ord(CarriedSignIn)) + ', tasks named=' + IntToStr(Ord(TasksNamed)) + ', entry present=' + IntToStr(Ord(RegValueExists(HKCU, RunKey, 'Chirpy'))));
    // What the person chose wins over what the earlier install did: the wizard's box, or a silent install's /TASKS= (even an
    // empty one). Only a silent install that names no tasks at all keeps the earlier VoiceTool Light's sign-in entry.
    if SignInTicked or (WizardSilent and (not TasksNamed) and CarriedSignIn) then
      RegWriteStringValue(HKCU, RunKey, 'Chirpy', Quoted(PythonwPath) + ' -s -E ' + Quoted(Script))
    else if ((not WizardSilent) or TasksNamed) and RegQueryStringValue(HKCU, RunKey, 'Chirpy', RunValue) then
      // unticked on a reinstall (in the wizard, or a silent one that names its tasks with /TASKS=): the entry an earlier install
      // of this folder made goes (another folder's entry stays). A silent install that names no tasks leaves it as it is.
      if Pos(Lowercase(RemoveBackslash(ExpandConstant('{app}')) + '\'), Lowercase(RunValue)) > 0 then
        RegDeleteValue(HKCU, RunKey, 'Chirpy');
    // a silent install starts it too, like Setup.cmd does (/NOLAUNCH to not); the wizard asks on its last page
    if WizardSilent and (not HasSwitch('/NOLAUNCH')) then
      Exec(PythonwPath, '-s -E ' + Quoted(Script) + ' --settings', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, Code);
  end;
end;

// ---- uninstall ----

function AskDeleteData: Boolean;
begin
  if HasSwitch('/DELETEDATA') then
    Result := True
  else if HasSwitch('/KEEPDATA') then
    Result := False
  else
    Result := SuppressibleMsgBox('Chirpy is being removed from ' + ExpandConstant('{app}') + '.' + #13#10 + #13#10 +
      'Keep your words, fixes, history and settings?' + #13#10 + #13#10 +
      'Yes keeps them in that folder, so installing Chirpy again finds them.' + #13#10 +
      'No deletes them.', mbConfirmation, MB_YESNO or MB_DEFBUTTON1, IDYES) = IDNO;
end;

procedure DeleteMatching(Dir, Pattern: String);
var
  Found: TFindRec;
begin
  if FindFirst(Dir + '\' + Pattern, Found) then
  try
    repeat
      if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        DeleteFile(Dir + '\' + Found.Name);
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

function AllDigits(S: String; MinLen, MaxLen: Integer): Boolean;
var
  I: Integer;
begin
  Result := (Length(S) >= MinLen) and (Length(S) <= MaxLen);
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then
      Result := False;
end;

function IsHex8(S: String): Boolean;
var
  I: Integer;
begin
  Result := Length(S) = 8;
  for I := 1 to Length(S) do
    if not (((S[I] >= '0') and (S[I] <= '9')) or ((S[I] >= 'a') and (S[I] <= 'f'))) then
      Result := False;
end;

// words.txt.<process id>-<8 lowercase hex>.tmp (or corrections.json.): the temporary file a cut-off save leaves (light_files.py)
function IsGeneratedTemp(Name, Prefix: String): Boolean;
var
  Rest: String;
  Dash: Integer;
begin
  Result := False;
  if (Copy(Name, 1, Length(Prefix)) <> Prefix) or (Copy(Name, Length(Name) - 3, 4) <> '.tmp') then
    Exit;
  Rest := Copy(Name, Length(Prefix) + 1, Length(Name) - Length(Prefix) - 4);
  Dash := Pos('-', Rest);
  Result := (Dash > 1) and (Length(Rest) = Dash + 8) and AllDigits(Copy(Rest, 1, Dash - 1), 1, 10) and IsHex8(Copy(Rest, Dash + 1, 8));
end;

function IsLeapYear(Year: Integer): Boolean;
begin
  Result := ((Year mod 4 = 0) and (Year mod 100 <> 0)) or (Year mod 400 = 0);
end;

function DaysInMonth(Year, Month: Integer): Integer;
begin
  case Month of
    2: if IsLeapYear(Year) then Result := 29 else Result := 28;
    4, 6, 9, 11: Result := 30;
  else
    Result := 31;
  end;
end;

// words.damaged-<yyyymmdd>-<hhmmss>[-<n>].txt (or corrections ... .json): the copy of an unreadable file Chirpy keeps. Exactly the
// name light_files.py makes: a real calendar date (month lengths, leap years) and time, and a suffix only from 2 to 99 (no leading
// zero), never a user's own look-alike.
function IsGeneratedCopy(Name, Stem, Ext: String): Boolean;
var
  Prefix, Rest: String;
  Suffix, Year, Month, Day: Integer;
begin
  Result := False;
  Prefix := Stem + '.damaged-';
  if (Copy(Name, 1, Length(Prefix)) <> Prefix) or (Copy(Name, Length(Name) - Length(Ext) + 1, Length(Ext)) <> Ext) then
    Exit;
  Rest := Copy(Name, Length(Prefix) + 1, Length(Name) - Length(Prefix) - Length(Ext));
  if (Length(Rest) <> 15) and (Length(Rest) <> 17) and (Length(Rest) <> 18) then
    Exit;
  if not (AllDigits(Copy(Rest, 1, 8), 8, 8) and (Rest[9] = '-') and AllDigits(Copy(Rest, 10, 6), 6, 6)) then
    Exit;
  Year := StrToIntDef(Copy(Rest, 1, 4), 0);
  Month := StrToIntDef(Copy(Rest, 5, 2), 0);
  Day := StrToIntDef(Copy(Rest, 7, 2), 0);
  if not ((Year >= 1) and (StrToIntDef(Copy(Rest, 5, 2), 0) >= 1) and (StrToIntDef(Copy(Rest, 5, 2), 0) <= 12) and
          (Day >= 1) and (Day <= DaysInMonth(Year, Month)) and
          (StrToIntDef(Copy(Rest, 10, 2), 99) <= 23) and (StrToIntDef(Copy(Rest, 12, 2), 99) <= 59) and
          (StrToIntDef(Copy(Rest, 14, 2), 99) <= 59)) then
    Exit;
  if Length(Rest) = 15 then
  begin
    Result := True;
    Exit;
  end;
  Suffix := StrToIntDef(Copy(Rest, 17, 2), 0);
  Result := (Rest[16] = '-') and AllDigits(Copy(Rest, 17, Length(Rest) - 16), 1, 2) and (Rest[17] <> '0') and (Suffix >= 2) and (Suffix <= 99);
end;

procedure DeleteGenerated(Dir: String);
var
  Found: TFindRec;
begin
  if FindFirst(Dir + '\*', Found) then
  try
    repeat
      if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        if IsGeneratedTemp(Found.Name, 'words.txt.') or IsGeneratedTemp(Found.Name, 'corrections.json.') or
           IsGeneratedTemp(Found.Name, 'usual_words.json.') or IsGeneratedCopy(Found.Name, 'words', '.txt') or IsGeneratedCopy(Found.Name, 'corrections', '.json') then
          DeleteFile(Dir + '\' + Found.Name);
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir, Value: String;
  Code: Integer;
begin
  Dir := RemoveBackslash(ExpandConstant('{app}'));
  if CurUninstallStep = usUninstall then
  begin
    // stop the copy that is running from this folder (only that one), and take away its sign-in entry
    // (what Stop Chirpy.cmd does, without a command shell: the folder is handed over in an environment variable)
    ChirpySetEnv('CHIRPY_FOLDER', Dir);
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -Command "$here = $env:CHIRPY_FOLDER.TrimEnd(''\''); Get-CimInstance Win32_Process | Where-Object { $_.Name -in ''python.exe'',''pythonw.exe'' -and $_.ExecutablePath -and $_.ExecutablePath.StartsWith($here + ''\python\'', [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(1000);
    if RegQueryStringValue(HKCU, RunKey, 'Chirpy', Value) then
      if Pos(Lowercase(Dir + '\'), Lowercase(Value)) > 0 then
        RegDeleteValue(HKCU, RunKey, 'Chirpy');
    DelTree(ExpandConstant('{%TEMP}') + '\voicetool-light-sounds', True, True, True);
    DeleteData := AskDeleteData;
  end;
  if (CurUninstallStep = usPostUninstall) and DeleteData then
  begin
    DeleteFile(Dir + '\config.json');
    DeleteFile(Dir + '\words.txt');
    DeleteFile(Dir + '\corrections.json');
    DeleteFile(Dir + '\corrections_pending.json');
    DeleteFile(Dir + '\history.jsonl');
    DeleteFile(Dir + '\usual_words.json');
    DeleteMatching(Dir, 'config.damaged-*.json');
    DeleteMatching(Dir, 'config.reset-*.json');
    DeleteMatching(Dir, 'config.backup*');
    DeleteMatching(Dir, 'config.json.*');   // (the save lock file, and a temporary file a cut-off save may leave)
    DeleteGenerated(Dir);   // (only the copies and temporary files Chirpy's own saves name; never a file the person made)
    DelTree(Dir + '\recordings', True, True, True);
    DeleteFile(Dir + '\' + Marker);
    RemoveDir(Dir);
  end;
end;
