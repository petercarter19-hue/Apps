# Tests for uninstall.ps1 (NOT shipped). Everything happens in throwaway folders under %TEMP%, with throwaway task and
# shortcut names ("VoiceTool-unit*"). The real VoiceTool task, shortcut and folder are never touched; this script
# checks at the end that they are unchanged.
#   powershell -NoProfile -ExecutionPolicy Bypass -File test_uninstall.ps1
$ErrorActionPreference = 'Stop'
$here = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src'))   # the app code (the tests live in tools\)
$script = Join-Path $here 'uninstall.ps1'
$root = Join-Path $env:TEMP 'vt-unit'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force "$root\docs" | Out-Null
$programs = [Environment]::GetFolderPath('Programs')
$python = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe'
$global:pass = 0; $global:fail = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { $global:pass++; Write-Host "PASS  $name" } else { $global:fail++; Write-Host "FAIL  $name  $detail" -ForegroundColor Red }
}

# what must stay exactly as it is
$liveTask = Get-ScheduledTask -TaskName 'VoiceTool' -ErrorAction SilentlyContinue
$liveLinkPath = Join-Path $programs 'VoiceTool.lnk'
$liveLinkBefore = if (Test-Path $liveLinkPath) { (Get-Item $liveLinkPath).LastWriteTime.Ticks } else { 0 }
$liveDir = Join-Path $env:LOCALAPPDATA 'VoiceTool\'   # the real, installed VoiceTool (Setup's default folder)
$liveProcs = (Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$liveDir*" -or $_.ExecutablePath -like "$liveDir*" }).ProcessId | Sort-Object

function New-Fake([string]$name) {
    $d = Join-Path $root $name
    New-Item -ItemType Directory -Force "$d\journal", "$d\models", "$d\.venv\Scripts" | Out-Null
    Set-Content "$d\voicetool.py" '# fake'
    Set-Content "$d\.voicetool-installed" $d      # what Setup writes: "this is the real install"
    Set-Content "$d\styles.json" '{"styles":[{"name":"LinkedIn","instructions":"short"}]}'
    Set-Content "$d\shortcuts.json" '[{"say":"my email","text":"sam@example.com"}]'
    Set-Content "$d\corrections_pending.json" '[{"heard":"c","meant":"d"}]'
    Set-Content "$d\words.txt" "# words`nZorblax"
    Set-Content "$d\corrections.json" '[{"heard":"a","meant":"b"}]'
    Set-Content "$d\style.md" '# My writing style'
    Set-Content "$d\history.jsonl" '{"text":"hello"}'
    Set-Content "$d\config.json" '{"dictate_key":"f9"}'
    Set-Content "$d\journal\j1.txt" 'dear diary'
    [IO.File]::WriteAllBytes("$d\models\big.bin", (New-Object byte[] 1048576))
    return $d
}
function New-FakeTask([string]$name, [string]$exe) {
    $a = New-ScheduledTaskAction -Execute $exe -Argument '"x"'
    $t = New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
    Register-ScheduledTask -TaskName $name -Action $a -Trigger $t -Force | Out-Null
}
function New-FakeLink([string]$name, [string]$target) {
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs ($name + '.lnk')))
    $sc.TargetPath = $target; $sc.Arguments = '"x"'; $sc.WorkingDirectory = $root; $sc.Save()
}
function Start-Sleeper([string]$marker) {
    Start-Process -FilePath $python -ArgumentList '-c', '"import time;time.sleep(900)"', "`"$marker`"" -WindowStyle Hidden -PassThru
}
function Run-Uninstall([string[]]$ArgList, [string[]]$Answers = $null) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script) + $ArgList
    if ($Answers) { $out = $Answers | & powershell.exe @a 2>&1 } else { $out = & powershell.exe @a 2>&1 }
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out | Out-String) }
}

try {
    # ---- 1. keep my data ----
    $f1 = New-Fake 'inst1'
    New-FakeTask 'VoiceTool-unit1' "$f1\.venv\Scripts\pythonw.exe"
    New-FakeLink 'VoiceTool-unit1' "$f1\.venv\Scripts\pythonw.exe"
    New-FakeLink 'Uninstall VoiceTool-unit1' "$f1\Uninstall.cmd"
    $sleeper = Start-Sleeper "$f1\voicetool.py"
    $control = Start-Sleeper 'C:\some\other\place\voicetool.py'
    Start-Sleep 2
    Check '1 keep: (setup) both test programs are running before the uninstall' ((Get-Process -Id $sleeper.Id -ErrorAction SilentlyContinue) -and (Get-Process -Id $control.Id -ErrorAction SilentlyContinue))
    $r = Run-Uninstall @('-InstallDir', $f1, '-TaskName', 'VoiceTool-unit1', '-ShortcutName', 'VoiceTool-unit1', '-KeepData', '-Force', '-NoPause', '-BackupRoot', "$root\docs")
    Check '1 keep: exit code 0' ($r.Code -eq 0) $r.Text
    Check '1 keep: install folder is gone' (-not (Test-Path $f1))
    $b = "$root\docs\VoiceTool backup"
    Check '1 keep: backup folder exists' (Test-Path $b)
    Check '1 keep: words kept' ((Get-Content "$b\words.txt" -Raw) -match 'Zorblax')
    Check '1 keep: fixes, style, history, settings kept' ((Test-Path "$b\corrections.json") -and (Test-Path "$b\style.md") -and (Test-Path "$b\history.jsonl") -and (Test-Path "$b\config.json"))
    Check '1 keep: named styles, voice shortcuts and pending fixes kept too' (((Get-Content "$b\styles.json" -Raw) -match 'LinkedIn') -and ((Get-Content "$b\shortcuts.json" -Raw) -match 'my email') -and ((Get-Content "$b\corrections_pending.json" -Raw) -match '"heard"'))
    Check '1 keep: journal kept' ((Get-Content "$b\journal\j1.txt" -Raw) -match 'dear diary')
    Check '1 keep: big model file is NOT copied into the backup' (-not (Test-Path "$b\models"))
    Check '1 keep: task removed' (-not (Get-ScheduledTask -TaskName 'VoiceTool-unit1' -ErrorAction SilentlyContinue))
    Check '1 keep: shortcut removed' (-not (Test-Path (Join-Path $programs 'VoiceTool-unit1.lnk')))
    Check '1 keep: the "Uninstall" Start menu shortcut is removed too' (-not (Test-Path (Join-Path $programs 'Uninstall VoiceTool-unit1.lnk')))
    Check '1 keep: the program running from that folder was stopped' (-not (Get-Process -Id $sleeper.Id -ErrorAction SilentlyContinue))
    Check '1 keep: an unrelated program was left running' ([bool](Get-Process -Id $control.Id -ErrorAction SilentlyContinue))
    Check '1 keep: it says where the backup is' ($r.Text -match 'VoiceTool backup')
    Check '1 keep: it says the Microsoft Visual C++ Runtime stays (other programs use it), and never touches it' (($r.Text -match 'Visual C\+\+ Runtime, if Setup installed it, stays') -and ((Get-Content $script -Raw) -notmatch 'vc_redist|VisualStudio\14\.0'))

    # ---- 2. delete my data too ----
    $f2 = New-Fake 'inst2'
    $r = Run-Uninstall @('-InstallDir', $f2, '-TaskName', 'VoiceTool-unit2', '-ShortcutName', 'VoiceTool-unit2', '-DeleteData', '-Force', '-NoPause', '-BackupRoot', "$root\docs")
    Check '2 delete: exit code 0 and folder gone' ($r.Code -eq 0 -and -not (Test-Path $f2)) $r.Text
    Check '2 delete: no new backup was made' ((Get-ChildItem "$root\docs" -Directory).Count -eq 1)

    # ---- 3. a task and shortcut with that name that belong to some other folder are left alone ----
    $f3 = New-Fake 'inst3'
    New-FakeTask 'VoiceTool-unit3' "$root\elsewhere\pythonw.exe"
    New-FakeLink 'VoiceTool-unit3' "$root\elsewhere\pythonw.exe"
    New-FakeLink 'Uninstall VoiceTool-unit3' "$root\elsewhere\Uninstall.cmd"
    $r = Run-Uninstall @('-InstallDir', $f3, '-TaskName', 'VoiceTool-unit3', '-ShortcutName', 'VoiceTool-unit3', '-DeleteData', '-Force', '-NoPause')
    Check '3 foreign: folder removed' (-not (Test-Path $f3)) $r.Text
    Check '3 foreign: other folder''s task left alone' ([bool](Get-ScheduledTask -TaskName 'VoiceTool-unit3' -ErrorAction SilentlyContinue))
    Check '3 foreign: other folder''s shortcut left alone' (Test-Path (Join-Path $programs 'VoiceTool-unit3.lnk'))
    Check '3 foreign: other folder''s "Uninstall" shortcut left alone' (Test-Path (Join-Path $programs 'Uninstall VoiceTool-unit3.lnk'))
    Remove-Item (Join-Path $programs 'Uninstall VoiceTool-unit3.lnk') -Force
    Unregister-ScheduledTask -TaskName 'VoiceTool-unit3' -Confirm:$false
    Remove-Item (Join-Path $programs 'VoiceTool-unit3.lnk') -Force

    # ---- 4. it refuses things that are not a VoiceTool folder ----
    New-Item -ItemType Directory "$root\notvt" | Out-Null
    Set-Content "$root\notvt\precious.txt" 'keep me'
    $r = Run-Uninstall @('-InstallDir', "$root\notvt", '-DeleteData', '-Force', '-NoPause')
    Check '4 refuse: a folder without voicetool.py is not touched' ($r.Code -eq 1 -and (Test-Path "$root\notvt\precious.txt")) $r.Text
    $r = Run-Uninstall @('-InstallDir', $env:USERPROFILE, '-DeleteData', '-Force', '-NoPause')
    Check '4 refuse: the user profile folder is not touched' ($r.Code -eq 1 -and (Test-Path $env:USERPROFILE)) $r.Text
    $r = Run-Uninstall @('-InstallDir', 'C:\', '-DeleteData', '-Force', '-NoPause')
    Check '4 refuse: a drive root is not touched' ($r.Code -eq 1) $r.Text

    # ---- 4b. the downloaded setup folder (voicetool.py, setup.ps1, Uninstall.cmd, but no marker) is refused ----
    $setupFolder = Join-Path $root 'VoiceTool-download'
    New-Item -ItemType Directory -Force "$setupFolder\ui", "$setupFolder\wheels" | Out-Null
    foreach ($n in 'voicetool.py', 'setup.ps1', 'Uninstall.cmd', 'words.txt', 'config.json') { Set-Content "$setupFolder\$n" 'x' }
    $r = Run-Uninstall @('-InstallDir', $setupFolder, '-DeleteData', '-Force', '-NoPause')
    Check '4b setup folder: refused with the plain message' ($r.Code -eq 1 -and $r.Text -match 'This is the downloaded setup folder, not the installed VoiceTool\. Use Uninstall VoiceTool from the Start menu\.') $r.Text
    Check '4b setup folder: nothing in it was touched' ((Test-Path "$setupFolder\voicetool.py") -and (Test-Path "$setupFolder\setup.ps1") -and (Test-Path "$setupFolder\words.txt") -and (Test-Path "$setupFolder\wheels"))
    $r = Run-Uninstall @('-InstallDir', $setupFolder, '-KeepData', '-NoPause', '-BackupRoot', "$root\docs") @('y', 'y')
    Check '4b setup folder: asking nicely (answering yes) changes nothing either, and no backup is made' ($r.Code -eq 1 -and (Test-Path "$setupFolder\voicetool.py") -and -not (Test-Path "$root\docs\VoiceTool backup 20*")) $r.Text
    # a folder with only the marker (no program) is still not a VoiceTool folder
    New-Item -ItemType Directory "$root\markeronly" | Out-Null
    Set-Content "$root\markeronly\.voicetool-installed" 'x'; Set-Content "$root\markeronly\keep.txt" 'x'
    $r = Run-Uninstall @('-InstallDir', "$root\markeronly", '-DeleteData', '-Force', '-NoPause')
    Check '4b marker alone: not enough (there must be voicetool.py too)' ($r.Code -eq 1 -and (Test-Path "$root\markeronly\keep.txt")) $r.Text
    # an install folder directly under a drive (like C:\VoiceTool, where Setup goes when the user name has accents) is accepted
    $letter = ('Y', 'X', 'W', 'V', 'U' | Where-Object { -not (Test-Path "${_}:\") } | Select-Object -First 1)
    if ($letter) {
        New-Item -ItemType Directory -Force "$root\substroot" | Out-Null
        subst "${letter}:" "$root\substroot"
        try {
            $top = "${letter}:\VoiceTool"
            New-Item -ItemType Directory -Force $top | Out-Null
            Set-Content "$top\voicetool.py" '# fake'; Set-Content "$top\.voicetool-installed" $top; Set-Content "$top\words.txt" 'x'
            $r = Run-Uninstall @('-InstallDir', $top, '-TaskName', 'VoiceTool-unit4', '-ShortcutName', 'VoiceTool-unit4', '-DeleteData', '-Force', '-NoPause')
            Check '4b short path: a marked folder right under a drive (like C:\VoiceTool) can be uninstalled' ($r.Code -eq 0 -and -not (Test-Path $top)) $r.Text
            $r = Run-Uninstall @('-InstallDir', "${letter}:\", '-DeleteData', '-Force', '-NoPause')
            Check '4b short path: the drive itself is still refused' ($r.Code -eq 1) $r.Text
        } finally { subst "${letter}:" /d }
    } else { Write-Host 'SKIP  short-path check (no free drive letter for subst)' }

    # ---- 4c. VoiceTool is stopped BEFORE the backup, so the copy holds what it last wrote ----
    $f8 = New-Fake 'inst8'
    $writer = "$root\writer.py"
    Set-Content $writer @'
import sys, time
hist, progress = sys.argv[2], sys.argv[3]
n = 0
while True:
    n += 1
    with open(hist, "a") as f:
        f.write("line %d\n" % n)
    with open(progress, "w") as f:
        f.write(str(n))
    time.sleep(0.03)
'@
    $wp = Start-Process -FilePath $python -ArgumentList "`"$writer`"", "`"$f8\voicetool.py`"", "`"$f8\history.jsonl`"", "`"$root\progress.txt`"" -WindowStyle Hidden -PassThru
    Start-Sleep 2
    Check '4c order: the writer is running inside the install folder before the uninstall' ((Get-Process -Id $wp.Id -ErrorAction SilentlyContinue) -and (Get-Item "$f8\history.jsonl").Length -gt 100)
    $r = Run-Uninstall @('-InstallDir', $f8, '-TaskName', 'VoiceTool-unit8', '-ShortcutName', 'VoiceTool-unit8', '-KeepData', '-Force', '-NoPause', '-BackupRoot', "$root\docs2")
    $lastWritten = [int](Get-Content "$root\progress.txt" -Raw)
    $copied = @(Get-Content ((Get-ChildItem "$root\docs2" -Directory | Select-Object -First 1).FullName + '\history.jsonl')).Count
    Check '4c order: the program was stopped first (the backup has every line it ever wrote)' ($r.Code -eq 0 -and $copied -eq ($lastWritten + 1) -and $lastWritten -gt 5 -and -not (Get-Process -Id $wp.Id -ErrorAction SilentlyContinue)) "copied=$copied written=$lastWritten $($r.Text)"
    # VoiceTool's own Claude helper (it runs inside claude_workdir, which would keep the folder from being deleted) is stopped too
    $venvDir = Join-Path $here '.venv'   # the development environment in src (see AGENTS.md)
    $launcher = Join-Path $venvDir 'Scripts\python.exe'
    if (Test-Path $launcher) {
        $f9 = New-Fake 'inst9'
        New-Item -ItemType Directory "$f9\claude_workdir", "$root\bin" -Force | Out-Null
        Copy-Item $launcher "$root\bin\claude.exe"
        Copy-Item (Join-Path $venvDir 'pyvenv.cfg') "$root\bin\pyvenv.cfg"
        $claude = Start-Process -FilePath "$root\bin\claude.exe" -ArgumentList '-c', '"import time;time.sleep(900)"', 'stream-json', '--add-dir', "`"$f9\claude_workdir`"" -WindowStyle Hidden -PassThru
        $otherClaude = Start-Process -FilePath "$root\bin\claude.exe" -ArgumentList '-c', '"import time;time.sleep(900)"', 'stream-json', '--add-dir', "`"$root\elsewhere\claude_workdir`"" -WindowStyle Hidden -PassThru
        Start-Sleep 2
        $r = Run-Uninstall @('-InstallDir', $f9, '-TaskName', 'VoiceTool-unit9', '-ShortcutName', 'VoiceTool-unit9', '-DeleteData', '-Force', '-NoPause')
        Check '4c claude: this install''s Claude helper is stopped and the folder is gone' ($r.Code -eq 0 -and -not (Test-Path $f9) -and -not (Get-Process -Id $claude.Id -ErrorAction SilentlyContinue)) $r.Text
        Check '4c claude: another Claude is left running' ([bool](Get-Process -Id $otherClaude.Id -ErrorAction SilentlyContinue))
        Stop-Process -Id $otherClaude.Id -Force -ErrorAction SilentlyContinue
    } else { Write-Host 'SKIP  claude helper check (no virtual-environment launcher to copy)' }

    # ---- 5. a backup that already exists is never overwritten ----
    $f5 = New-Fake 'inst5'
    Set-Content "$root\docs\VoiceTool backup\words.txt" 'OLD BACKUP'
    $r = Run-Uninstall @('-InstallDir', $f5, '-TaskName', 'VoiceTool-unit5', '-ShortcutName', 'VoiceTool-unit5', '-KeepData', '-Force', '-NoPause', '-BackupRoot', "$root\docs")
    Check '5 collision: the old backup is untouched' ((Get-Content "$root\docs\VoiceTool backup\words.txt" -Raw) -match 'OLD BACKUP') $r.Text
    Check '5 collision: the new one went to a dated folder' ((Get-ChildItem "$root\docs" -Directory | Where-Object Name -like 'VoiceTool backup 20*').Count -eq 1)

    # ---- 6. answering the questions by hand ----
    $f6 = New-Fake 'inst6'
    $r = Run-Uninstall @('-InstallDir', $f6, '-TaskName', 'VoiceTool-unit6', '-ShortcutName', 'VoiceTool-unit6', '-NoPause') @('n')
    Check '6 ask: answering "n" to "Remove VoiceTool now?" changes nothing' ($r.Code -eq 0 -and (Test-Path "$f6\words.txt") -and $r.Text -match 'Nothing was changed') $r.Text
    $r = Run-Uninstall @('-InstallDir', $f6, '-TaskName', 'VoiceTool-unit6', '-ShortcutName', 'VoiceTool-unit6', '-NoPause', '-BackupRoot', "$root\docs") @('y', 'n')
    Check '6 ask: yes, then "don''t keep" removes it without a backup' ($r.Code -eq 0 -and -not (Test-Path $f6) -and (Get-ChildItem "$root\docs" -Directory).Count -eq 2) $r.Text
    $f7 = New-Fake 'inst7'
    $r = Run-Uninstall @('-InstallDir', $f7, '-TaskName', 'VoiceTool-unit7', '-ShortcutName', 'VoiceTool-unit7', '-NoPause', '-BackupRoot', "$root\docs") @('', '')
    Check '6 ask: pressing Enter twice means "yes, remove" and "yes, keep a copy"' ($r.Code -eq 0 -and -not (Test-Path $f7) -and (Get-ChildItem "$root\docs" -Directory).Count -eq 3) $r.Text
}
finally {
    foreach ($p in @($sleeper, $control, $wp, $claude, $otherClaude)) { if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    if ($letter -and (Test-Path "${letter}:\")) { subst "${letter}:" /d }
    foreach ($n in 'VoiceTool-unit1', 'VoiceTool-unit2', 'VoiceTool-unit3', 'VoiceTool-unit4', 'VoiceTool-unit5', 'VoiceTool-unit6', 'VoiceTool-unit7', 'VoiceTool-unit8', 'VoiceTool-unit9') {
        Unregister-ScheduledTask -TaskName $n -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $programs ($n + '.lnk')) -Force -ErrorAction SilentlyContinue
        Remove-Item (Join-Path $programs ("Uninstall $n.lnk")) -Force -ErrorAction SilentlyContinue
    }
}

# the real VoiceTool is exactly as it was
$liveTaskAfter = Get-ScheduledTask -TaskName 'VoiceTool' -ErrorAction SilentlyContinue
Check 'live: the VoiceTool task is unchanged' ((($liveTask -eq $null) -eq ($liveTaskAfter -eq $null)) -and ($liveTask.State -eq $liveTaskAfter.State))
$liveLinkAfter = if (Test-Path $liveLinkPath) { (Get-Item $liveLinkPath).LastWriteTime.Ticks } else { 0 }
Check 'live: the VoiceTool.lnk shortcut is unchanged' ($liveLinkBefore -eq $liveLinkAfter)
$liveAfter = (Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$liveDir*" -or $_.ExecutablePath -like "$liveDir*" }).ProcessId | Sort-Object
Check 'live: the running VoiceTool processes are the same' ((@($liveProcs) -join ',') -eq (@($liveAfter) -join ','))

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ("`n{0} passed, {1} failed" -f $global:pass, $global:fail)
exit $(if ($global:fail) { 1 } else { 0 })
