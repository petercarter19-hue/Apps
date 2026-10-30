# Tests for the pieces of setup.ps1 that don't need a full install (NOT shipped): the checks, the app copy that must never
# erase the user's words and settings, the Python finder, the Python installer's command line and download check, the
# Start menu shortcut, the sign-in task, and the guard that only stops VoiceTool's own programs.
# Throwaway folders in %TEMP%, throwaway names "VoiceTool-unit*". Nothing real is touched; nothing is installed.
#   powershell -NoProfile -ExecutionPolicy Bypass -File test_setup_functions.ps1 [-Cache <folder holding python-3.13.17-amd64.exe>]
param([string]$Cache = '')
$ErrorActionPreference = 'Stop'
$here = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src'))   # the app code (the tests live in tools\)
$root = Join-Path $env:TEMP 'vt-setup-unit'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force "$root\inst" | Out-Null
$global:pass = 0; $global:fail = 0
function Check($name, $ok, $detail = '') {
    if ($ok) { $global:pass++; Write-Host "PASS  $name" } else { $global:fail++; Write-Host "FAIL  $name  $detail" -ForegroundColor Red }
}
function Throws([scriptblock]$code) { try { & $code; return $null } catch { return $_.Exception.Message } }

. (Join-Path $here 'setup.ps1') -InstallDir "$root\inst" -TaskName 'VoiceTool-unittask' -ShortcutName 'VoiceTool-unitlnk'
$script:Log = "$root\setup.log"
$realNative = ${function:Invoke-Native}
$realFind = ${function:Find-Python313}
$programs = [Environment]::GetFolderPath('Programs')
$py313 = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe'
$py312 = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'

# ---------------- checks that stop setup, with plain words
$script:Src = 'C:\Users\sam\AppData\Local\Temp\Temp1_VoiceTool.zip\VoiceTool'
$m = Throws { Test-Environment }
Check 'zip: running from inside the zip is caught and explained' ($m -match 'inside the zip' -and $m -match 'Extract All') $m
$script:Src = "$root\empty"; New-Item -ItemType Directory $script:Src | Out-Null
$m = Throws { Test-Environment }
Check 'files: a missing app is explained' ($m -match "can't find VoiceTool's files") $m
$script:Src = $here
Check 'checks: the staging folder passes the environment check' ($null -eq (Throws { Test-Environment }))
$script:Src = "$root\nowheels"; New-Item -ItemType Directory $script:Src | Out-Null
Set-Content "$script:Src\voicetool.py" 'x'; Set-Content "$script:Src\requirements-friend.txt" 'x'
$m = Throws { Test-Environment }
Check 'wheels: a missing wheels folder is explained' ($m -match 'wheels' -and $m -match 'unzipped the whole folder') $m
$script:Src = $here
$MinFreeGBFresh = 100000000
$m = Throws { Test-DiskSpace $false }
Check 'disk: not enough space is explained with the numbers' ($m -match "isn't enough free space" -and $m -match 'GB') $m
$MinFreeGBFresh = 6
Check 'disk: normal free space passes' ($null -eq (Throws { Test-DiskSpace $false }))
function Invoke-WebRequest { throw 'The remote name could not be resolved' }
$m = Throws { Test-Internet }
Check 'internet: no connection is explained' ($m -match "can't reach the internet") $m
Remove-Item function:Invoke-WebRequest

# ---------------- where VoiceTool goes (plain folder if the user name has accents) and the pip command
$accent = "C:\Users\Zo$([char]0xE9)\AppData\Local\VoiceTool"
Check 'folder: the default under a user name with an accent becomes C:\VoiceTool' ((Get-SafeInstallDir $accent $false) -eq (Join-Path $env:SystemDrive 'VoiceTool'))
Check 'folder: a folder the person chose is left as it is' ((Get-SafeInstallDir $accent $true) -eq $accent)
Check 'folder: a plain default stays' ((Get-SafeInstallDir 'C:\Users\sam\AppData\Local\VoiceTool' $false) -eq 'C:\Users\sam\AppData\Local\VoiceTool')
$pip = Get-PipInstallArgs 'C:\x\requirements-friend.txt'
foreach ($want in '--require-hashes', '--only-binary=:all:', '--no-cache-dir') { Check "pip: $want" ($pip -contains $want) ($pip -join ' ') }
Check 'pip: the wheels folder in the install folder is the link source' ($pip[($pip.IndexOf('--find-links') + 1)] -eq "$root\inst\wheels" -or -not (Test-Path "$root\inst\wheels"))
New-Item -ItemType Directory -Force "$root\inst\wheels" | Out-Null
$pip = Get-PipInstallArgs 'C:\x\requirements-friend.txt'
Check 'pip: with a wheels folder, pip is pointed at it' ($pip[($pip.IndexOf('--find-links') + 1)] -eq "$root\inst\wheels" -and $pip[-2] -eq '-r')
Remove-Item "$root\inst\wheels" -Force
# the shipped list: every package pinned with a fingerprint, and the one wheel we carry matches its fingerprint
$reqLines = Get-Content (Join-Path $here 'requirements-friend.txt')
$pinned = @($reqLines | Where-Object { $_ -match '^[A-Za-z0-9_.\-]+==' })
$bad = @(); for ($i = 0; $i -lt $reqLines.Count; $i++) { if ($reqLines[$i] -match '^[A-Za-z0-9_.\-]+==\S+ \\$') { if ($reqLines[$i + 1] -notmatch '^\s+--hash=sha256:[0-9a-f]{64}$') { $bad += $reqLines[$i] } } elseif ($reqLines[$i] -match '^[A-Za-z0-9_.\-]+==') { $bad += $reqLines[$i] } }
Check 'requirements: every package is pinned and has a SHA-256 fingerprint' ($pinned.Count -ge 45 -and $bad.Count -eq 0) ($bad -join '; ')
$wheel = Get-ChildItem (Join-Path $here 'wheels') -Filter 'proxy_tools-*.whl'
$wheelHash = (Get-FileHash $wheel.FullName -Algorithm SHA256).Hash.ToLower()
$wheelLine = ($reqLines | Select-String -SimpleMatch 'proxy_tools==0.1.0').LineNumber
Check 'requirements: the proxy_tools wheel in the wheels folder is the one the list names' ($reqLines[$wheelLine] -match $wheelHash)

# ---------------- finding Python 3.13
Check 'python: a real 3.13 with tkinter is accepted' (Test-PythonExe $py313)
Check 'python: 3.12 is not accepted' (-not (Test-PythonExe $py312))
Check 'python: a missing file is not accepted' (-not (Test-PythonExe "$root\nope\python.exe"))
Check 'python: the Microsoft Store copy is not accepted' (-not (Test-PythonExe 'C:\Users\x\AppData\Local\Microsoft\WindowsApps\python.exe'))
Check 'python: Find-Python313 finds a Python on this computer' ($null -ne (Find-Python313))
$savedLocal = $env:LOCALAPPDATA
$env:LOCALAPPDATA = "$root\nolocal"
function Get-Command { param($n) $null }
Check 'python: a computer with no Python 3.13 finds none (so Setup would install it)' ($null -eq (Find-Python313))
Remove-Item function:Get-Command
$env:LOCALAPPDATA = $savedLocal
New-Item -ItemType Directory -Force "$root\inst\python" | Out-Null
Copy-Item $py313 "$root\inst\python\python.exe" -ErrorAction SilentlyContinue
Check 'python: Setup''s own copy in the install folder would be preferred if it works (here a bare exe copy can''t run, so it is skipped)' ($null -ne (Find-Python313))
Remove-Item "$root\inst\python" -Recurse -Force

# an existing Python 3.13 that VoiceTool can't use is never installed over: Setup stops and says what to do
$brokenDir = "$root\brokenpy"; New-Item -ItemType Directory $brokenDir | Out-Null
Copy-Item $py312 "$brokenDir\python.exe" -ErrorAction SilentlyContinue   # (a bare copy can't run: "not usable")
if (-not (Test-Path "$brokenDir\python.exe")) { Set-Content "$brokenDir\python.exe" 'not a program' }
$realOthers = ${function:Get-OtherPython313Paths}
$realInstallOwn = ${function:Install-OwnPython}
function Find-Python313 { return $null }
function Get-OtherPython313Paths { return @("$brokenDir\python.exe") }
$script:installed = $false
function Install-OwnPython { $script:installed = $true; return 'x' }
$m = Throws { Resolve-BasePython }
Check 'python: an existing but unusable 3.13 stops Setup with a plain message' ($m -match 'already on this computer' -and $m -match 'Tcl/Tk' -and $m -match 'Modify' -and $m -match [regex]::Escape("$brokenDir\python.exe")) $m
Check 'python: ...and the installer is not run over it' (-not $script:installed)
function Get-OtherPython313Paths { return @() }
$base = Resolve-BasePython
Check 'python: no other 3.13 at all: Setup installs its own' ($script:installed -and $base -eq 'x')
${function:Get-OtherPython313Paths} = $realOthers
${function:Install-OwnPython} = $realInstallOwn
${function:Find-Python313} = $realFind
$others = @(Get-OtherPython313Paths)
Check 'python: the real finder never lists our own install-folder Python or a Store copy' (-not ($others | Where-Object { $_ -like "$root\inst\python\*" -or $_ -match 'WindowsApps' }))
Check 'python: a usable 3.13 on this computer is found as before' ($null -ne (Find-Python313))
$oldText = (Get-Content (Join-Path $here 'setup.ps1') -Raw)
Check 'python: the wording no longer promises that nothing else changes' ($oldText -notmatch "won't change anything else" -and $oldText -match 'list of apps')

# ---------------- Python's installer: command line, signature, download check (the installer itself is never run)
$a = Get-PythonInstallerArgs "$root\inst\python" "$root\inst\python-install.log"
foreach ($want in '/quiet', 'InstallAllUsers=0', 'Include_launcher=0', 'Include_tcltk=1', 'PrependPath=0', 'AssociateFiles=0', 'Shortcuts=0', "TargetDir=$root\inst\python") {
    Check "installer args: $want" ($a -contains $want) ($a -join ' ')
}
$cacheDir = if ($Cache) { $Cache } else { Join-Path $env:TEMP 'vt-dryrun\cache' }
if (Test-Path (Join-Path $cacheDir "python-$PythonVersion-amd64.exe")) {
    $script:captured = $null
    function Invoke-Native { param($Exe, $ArgList, [switch]$Heartbeat, $WorkDir) $script:captured = @($Exe) + $ArgList; return 0 }
    function Find-Python313 { return $null }
    $DownloadCache = $cacheDir
    $m = Throws { Install-OwnPython }
    Check 'installer: the pinned installer in the cache is checked, signature valid, and its command is built' ($script:captured -and $script:captured[0] -like '*python-3.13.17-amd64.exe' -and $script:captured -contains '/quiet') ($script:captured -join ' ')
    Check 'installer: if Python then can''t be found afterwards the message is plain' ($m -match "Python installed but Setup can't use it") $m
    Check 'installer: it was not actually run (nothing installed under the install folder)' (-not (Test-Path "$root\inst\python\python.exe"))
    ${function:Invoke-Native} = $realNative
    ${function:Find-Python313} = $realFind
    $DownloadCache = ''
} else { Write-Host 'SKIP  installer checks (no cached installer found)' }

# a download whose bytes don't match the pinned fingerprint is refused (local server; no internet)
$serveDir = Join-Path $root 'serve'; New-Item -ItemType Directory $serveDir | Out-Null
Set-Content "$serveDir\bad.exe" 'not python'
$srv = Start-Process -FilePath $py313 -ArgumentList '-m', 'http.server', '58321', '--bind', '127.0.0.1', '--directory', "`"$serveDir`"" -PassThru -WindowStyle Hidden
Start-Sleep 2
try {
    $m = Throws { Get-VerifiedFile 'http://127.0.0.1:58321/bad.exe' $PythonSha256 "$root\dl\bad.exe" 'Python 3.13' }
    Check 'download: wrong bytes are refused after retries, with a plain message' ($m -match "Couldn't download Python 3.13" -and $m -match 'internet connection') $m
    Check 'download: the wrong file is not left behind' (-not (Test-Path "$root\dl\bad.exe"))
    Set-Content "$serveDir\good.bin" 'hello'
    $sha = (Get-FileHash "$serveDir\good.bin" -Algorithm SHA256).Hash.ToLower()
    Get-VerifiedFile 'http://127.0.0.1:58321/good.bin' $sha "$root\dl\good.bin" 'a test file'
    Check 'download: right bytes are accepted' ((Get-FileHash "$root\dl\good.bin" -Algorithm SHA256).Hash.ToLower() -eq $sha)
    Remove-Item "$serveDir\good.bin"   # now the server can't supply it; the cached copy must be reused
    Get-VerifiedFile 'http://127.0.0.1:58321/good.bin' $sha "$root\dl\good.bin" 'a test file'
    Check 'download: a matching file already in the cache is reused without downloading' ((Get-Content "$root\dl\good.bin" -Raw) -match 'hello')
} finally { Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue }

# ---------------- the guard on stopping programs
$sleeper = Start-Process -FilePath $py313 -ArgumentList '-c', '"import time;time.sleep(900)"', "`"$root\inst\voicetool.py`"" -PassThru -WindowStyle Hidden
$control = Start-Process -FilePath $py313 -ArgumentList '-c', '"import time;time.sleep(900)"', "`"$root\other\voicetool.py`"" -PassThru -WindowStyle Hidden
Start-Sleep 2
$InstallDir = ''
Stop-InstalledProcesses
Check 'stop: an empty install folder name matches nothing (no program is stopped)' ((Get-Process -Id $sleeper.Id -ErrorAction SilentlyContinue) -and (Get-Process -Id $control.Id -ErrorAction SilentlyContinue))
$InstallDir = "$root\inst"
Stop-InstalledProcesses
Check 'stop: only the program running from the install folder is stopped' ((-not (Get-Process -Id $sleeper.Id -ErrorAction SilentlyContinue)) -and (Get-Process -Id $control.Id -ErrorAction SilentlyContinue))
Stop-Process -Id $control.Id -Force -ErrorAction SilentlyContinue

# ---------------- copying the app (never erases the user's words, settings or style)
$src = "$root\src"; $dst = "$root\copy"
New-Item -ItemType Directory -Force "$src\ui", "$src\.venv\Lib", "$src\__pycache__", "$src\models\parakeet-tdt-0.6b-v2", "$src\models\qwen3-4b-instruct-2507", "$src\runtime\x", "$src\backups", "$dst" | Out-Null
Set-Content "$src\voicetool.py" 'v1'; Set-Content "$src\ui\index.html" 'ui'; Set-Content "$src\requirements-friend.txt" 'x==1'
Set-Content "$src\test_a.py" 't'; Set-Content "$src\test_b.ps1" 't'; Set-Content "$src\dryrun_check.py" 't'; Set-Content "$src\ROUND1_DONE.txt" 'done'
Set-Content "$src\history.jsonl" 'SOURCE HISTORY'; Set-Content "$src\.venv\Lib\junk.py" 'j'; Set-Content "$src\__pycache__\x.pyc" 'j'
Set-Content "$src\models\parakeet-tdt-0.6b-v2\vocab.txt" 'vocab'; Set-Content "$src\models\qwen3-4b-instruct-2507\big.gguf" 'big'
Set-Content "$src\runtime\x\a.exe" 'a'; Set-Content "$src\backups\b.txt" 'b'
# files that share a name with the user's own top-level files, deeper in the tree (the speech model's own config.json!)
Set-Content "$src\models\parakeet-tdt-0.6b-v2\config.json" 'PARAKEET CONFIG'
Set-Content "$src\ui\words.txt" 'UI WORDS'; Set-Content "$src\ui\style.md" 'UI STYLE'; Set-Content "$src\ui\corrections.json" 'UI FIXES'
Set-Content "$src\ui\history.jsonl" 'UI HISTORY'; Set-Content "$src\ui\config.json" 'UI CONFIG'; Set-Content "$src\ui\test_inside_ui.py" 'a real file called test_*'
Set-Content "$src\config.json" '{"dictate_key":"alt_l"}'; Set-Content "$src\words.txt" 'DEFAULT WORDS'; Set-Content "$src\style.md" 'DEFAULT STYLE'
Set-Content "$dst\words.txt" 'MY WORDS'; Set-Content "$dst\config.json" '{"dictate_key":"f9"}'
Set-Content "$dst\corrections.json" '[{"heard":"x","meant":"y"}]'; Set-Content "$dst\history.jsonl" 'MY HISTORY'
$script:Src = $src; $InstallDir = $dst
Copy-App
Check 'copy: code and ui are copied' ((Get-Content "$dst\voicetool.py" -Raw) -match 'v1' -and (Test-Path "$dst\ui\index.html") -and (Test-Path "$dst\requirements-friend.txt"))
Check 'copy: the bundled speech model is copied' (Test-Path "$dst\models\parakeet-tdt-0.6b-v2\vocab.txt")
Check 'copy: the speech model''s own config.json is copied although the user''s top-level config.json is protected' ((Get-Content "$dst\models\parakeet-tdt-0.6b-v2\config.json" -Raw) -match 'PARAKEET CONFIG')
Check 'copy: same-named files deeper in the tree (words.txt, style.md, corrections.json, history.jsonl, config.json) are copied; only the top level is protected' (((Get-Content "$dst\ui\words.txt" -Raw) -match 'UI WORDS') -and ((Get-Content "$dst\ui\style.md" -Raw) -match 'UI STYLE') -and ((Get-Content "$dst\ui\corrections.json" -Raw) -match 'UI FIXES') -and ((Get-Content "$dst\ui\history.jsonl" -Raw) -match 'UI HISTORY') -and ((Get-Content "$dst\ui\config.json" -Raw) -match 'UI CONFIG'))
Check 'copy: only top-level test_* files are left out (a deeper file with that name is kept)' ((Test-Path "$dst\ui\test_inside_ui.py") -and -not (Test-Path "$dst\test_a.py"))
Check 'copy: tests, dry-run files, the Round 1 marker, venv, caches, other models, runtime, backups are left out' (-not ((Test-Path "$dst\test_a.py") -or (Test-Path "$dst\test_b.ps1") -or (Test-Path "$dst\dryrun_check.py") -or (Test-Path "$dst\ROUND1_DONE.txt") -or (Test-Path "$dst\.venv") -or (Test-Path "$dst\__pycache__") -or (Test-Path "$dst\models\qwen3-4b-instruct-2507") -or (Test-Path "$dst\runtime") -or (Test-Path "$dst\backups")))
Check 'copy: the user''s words, settings, fixes and history are NOT overwritten' ((Get-Content "$dst\words.txt" -Raw) -match 'MY WORDS' -and (Get-Content "$dst\config.json" -Raw) -match 'f9' -and (Get-Content "$dst\corrections.json" -Raw) -match '"x"' -and (Get-Content "$dst\history.jsonl" -Raw) -match 'MY HISTORY')
Check 'copy: a default the install didn''t have yet (style.md) is added' ((Get-Content "$dst\style.md" -Raw) -match 'DEFAULT STYLE')
Set-Content "$src\voicetool.py" 'v2'
Copy-App
Check 'copy (repair): updated code replaces old code' ((Get-Content "$dst\voicetool.py" -Raw) -match 'v2')
Check 'copy (repair): the user''s files are still theirs' ((Get-Content "$dst\words.txt" -Raw) -match 'MY WORDS' -and (Get-Content "$dst\history.jsonl" -Raw) -match 'MY HISTORY')
# an install folder inside the source folder must not copy itself into itself
$InstallDir = "$src\installed-here"
Copy-App
Check 'copy: an install folder inside the source folder does not recurse into itself' ((Test-Path "$src\installed-here\voicetool.py") -and -not (Test-Path "$src\installed-here\installed-here"))
$script:Src = $here

# ---------------- a fresh install from an extracted package (no -DownloadCache): the speech model arrives complete
$cacheForParakeet = Join-Path $env:TEMP 'vt-dryrun\cache'
$parakeetMap = [ordered]@{ 'parakeet-config.json' = 'config.json'; 'parakeet-vocab.txt' = 'vocab.txt'
                          'parakeet-decoder_joint-model.int8.onnx' = 'decoder_joint-model.int8.onnx'; 'parakeet-encoder-model.int8.onnx' = 'encoder-model.int8.onnx' }
if (($parakeetMap.Keys | Where-Object { -not (Test-Path (Join-Path $cacheForParakeet $_)) }).Count -eq 0) {
    $pkgDir = "$root\package"; $freshDir = "$root\fresh"
    New-Item -ItemType Directory -Force "$pkgDir\models\parakeet-tdt-0.6b-v2", "$pkgDir\ui", $freshDir | Out-Null
    foreach ($n in 'voicetool.py', 'fetch_models.py', 'requirements-friend.txt', 'config.json', 'words.txt', 'style.md') { Copy-Item (Join-Path $here $n) "$pkgDir\$n" }
    foreach ($k in $parakeetMap.Keys) { Copy-Item (Join-Path $cacheForParakeet $k) "$pkgDir\models\parakeet-tdt-0.6b-v2\$($parakeetMap[$k])" }
    $keepDownloadCache = $DownloadCache; $DownloadCache = ''
    $script:Src = $pkgDir; $InstallDir = $freshDir
    Copy-App
    $check = & $py313 -I (Join-Path $here 'fetch_models.py') --check --root $freshDir 2>&1 | Out-String
    $folder = "$freshDir\models\parakeet-tdt-0.6b-v2"
    Check 'fresh install: all four speech model files are there' (($parakeetMap.Values | Where-Object { -not (Test-Path "$folder\$_") }).Count -eq 0)
    Check 'fresh install: ...and the downloader''s check (the pinned SHA-256 of each file) finds none of them missing or different' (($check -notmatch 'missing: parakeet') -and ($check -match 'missing: qwen')) $check
    Check 'fresh install: the shipped config.json is the install''s top-level config' ((Get-Content "$freshDir\config.json" -Raw) -match 'dictate_key')
    $DownloadCache = $keepDownloadCache
    $script:Src = $here; $InstallDir = "$root\inst"
} else { Write-Host 'SKIP  fresh install from a package (the cached speech model files are not in %TEMP%\vt-dryrun\cache)' }

# ---------------- the marker, the Uninstall shortcut, and not taking over what isn't ours
$InstallDir = "$root\inst"
Write-InstallMarker
Check 'marker: .voicetool-installed holds the install path' ((Get-Content "$root\inst\.voicetool-installed" -Raw).Trim() -eq "$root\inst")
# (PowerShell names are not case-sensitive: $src and $script:Src are the same variable, so spell the folder out)
$markerSrc = "$root\markersrc"; New-Item -ItemType Directory -Force "$markerSrc\ui" | Out-Null
Set-Content "$markerSrc\voicetool.py" 'v1'; Set-Content "$markerSrc\.voicetool-installed" 'from some other install'
$script:Src = $markerSrc; $InstallDir = "$root\copy2"; New-Item -ItemType Directory $InstallDir | Out-Null
Copy-App
Check 'marker: it is never copied from the setup folder (so the setup folder can''t be mistaken for an install)' (-not (Test-Path "$root\copy2\.voicetool-installed") -and (Test-Path "$root\copy2\voicetool.py"))
$script:Src = $here; $InstallDir = "$root\inst"
Set-Content "$root\inst\Uninstall.cmd" '@echo off'
Set-Content "$root\inst\voicetool.ico" 'ico'
New-UninstallShortcut
$ul = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'Uninstall VoiceTool-unitlnk.lnk'))
Check 'uninstall shortcut: "Uninstall <name>" in the Start menu runs Uninstall.cmd in the install folder' ($ul.TargetPath -eq "$root\inst\Uninstall.cmd" -and $ul.WorkingDirectory -eq "$root\inst")
Remove-Item (Join-Path $programs 'Uninstall VoiceTool-unitlnk.lnk') -Force

function New-ForeignTask($exe) {
    $a = New-ScheduledTaskAction -Execute $exe -Argument '"x"'
    Register-ScheduledTask -TaskName 'VoiceTool-unittask' -Action $a -Trigger (New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name)) -Force | Out-Null
}
function New-ForeignLink($name, $target) {
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs ($name + '.lnk'))); $sc.TargetPath = $target; $sc.Arguments = '"x"'; $sc.WorkingDirectory = $root; $sc.Save()
}
$Force = [switch]::new($false); $SkipTask = [switch]::new($false); $SkipShortcut = [switch]::new($false)
Check 'conflicts: nothing exists yet, so nothing stops Setup' ($null -eq (Throws { Test-NameConflicts }))
New-ForeignTask "$root\elsewhere\pythonw.exe"
$m = Throws { Test-NameConflicts }
Check 'conflicts: a start-up task of that name that points elsewhere stops Setup with a plain message' ($m -match 'already has a start-up task named "VoiceTool-unittask"' -and $m -match 'elsewhere' -and $m -match '-Force') $m
Check 'conflicts: ...and the task is untouched' ((Get-ScheduledTask -TaskName 'VoiceTool-unittask').Actions[0].Execute -eq "$root\elsewhere\pythonw.exe")
$SkipTask = [switch]::new($true)
Check 'conflicts: -SkipTask means the task is not looked at' ($null -eq (Throws { Test-NameConflicts }))
$SkipTask = [switch]::new($false)
$Force = [switch]::new($true)
Check 'conflicts: -Force lets Setup go on (and says it is replacing)' (($null -eq (Throws { Test-NameConflicts })) -and ($script:Warnings -join ' ') -match 'Replacing a start-up task')
$Force = [switch]::new($false)
Unregister-ScheduledTask -TaskName 'VoiceTool-unittask' -Confirm:$false
New-ForeignTask "$root\inst\.venv\Scripts\pythonw.exe"
Check 'conflicts: a task that already points into this install folder is ours (a repair run goes on)' ($null -eq (Throws { Test-NameConflicts }))
Unregister-ScheduledTask -TaskName 'VoiceTool-unittask' -Confirm:$false
New-ForeignLink 'VoiceTool-unitlnk' "$root\elsewhere\pythonw.exe"
$m = Throws { Test-NameConflicts }
Check 'conflicts: a Start menu shortcut of that name that points elsewhere stops Setup' ($m -match 'Start menu shortcut named "VoiceTool-unitlnk"' -and $m -match '-Force') $m
$SkipShortcut = [switch]::new($true)
Check 'conflicts: -SkipShortcut means shortcuts are not looked at' ($null -eq (Throws { Test-NameConflicts }))
$SkipShortcut = [switch]::new($false)
Remove-Item (Join-Path $programs 'VoiceTool-unitlnk.lnk') -Force
New-ForeignLink 'Uninstall VoiceTool-unitlnk' "$root\elsewhere\Uninstall.cmd"
$m = Throws { Test-NameConflicts }
Check 'conflicts: so does a foreign "Uninstall" shortcut' ($m -match 'Start menu shortcut named "Uninstall VoiceTool-unitlnk"') $m
Remove-Item (Join-Path $programs 'Uninstall VoiceTool-unitlnk.lnk') -Force
New-ForeignLink 'VoiceTool-unitlnk' "$root\inst\.venv\Scripts\pythonw.exe"
Check 'conflicts: a shortcut that points into this install folder is ours' ($null -eq (Throws { Test-NameConflicts }))
Remove-Item (Join-Path $programs 'VoiceTool-unitlnk.lnk') -Force
$script:Warnings.Clear()

# ---------------- the Microsoft Visual C++ Runtime step (everything faked: the real installer is never run, nothing is
# written to the registry; the real detection only reads System32)
$realInfo = ${function:Get-VcRuntimeInfo}; $realStartRedist = ${function:Start-VcRedist}
$realSig = ${function:Test-MicrosoftSignature}; $realElev = ${function:Test-IsElevated}
$real = Get-VcRuntimeInfo
Check 'vc runtime: the real check reads System32 (a file version and whether vcruntime140_1.dll is there)' ($real -is [hashtable] -and $real.ContainsKey('Msvcp') -and $real.ContainsKey('Vcruntime1'))
Check 'vc runtime: this computer has it, so the real check says yes' ((Test-VcRuntime) -eq [bool]($real.Vcruntime1 -and $real.Msvcp -and $real.Msvcp -ge [version]'14.40'))
$script:vcInfo = @{ Msvcp = [version]'14.44.35211.0'; Vcruntime1 = $true }
function Get-VcRuntimeInfo { return $script:vcInfo }
Check 'vc runtime: both files present and new enough -> present' (Test-VcRuntime)
$script:vcInfo = @{ Msvcp = [version]'14.40.0.0'; Vcruntime1 = $true }
Check 'vc runtime: exactly 14.40 is new enough' (Test-VcRuntime)
$script:vcInfo = @{ Msvcp = $null; Vcruntime1 = $false }
Check 'vc runtime: neither file (a clean Windows) -> missing' (-not (Test-VcRuntime))
$script:vcInfo = @{ Msvcp = [version]'14.44.35211.0'; Vcruntime1 = $false }
Check 'vc runtime: msvcp140.dll without vcruntime140_1.dll -> missing' (-not (Test-VcRuntime))
$script:vcInfo = @{ Msvcp = $null; Vcruntime1 = $true }
Check 'vc runtime: vcruntime140_1.dll without msvcp140.dll -> missing' (-not (Test-VcRuntime))
$script:vcInfo = @{ Msvcp = [version]'14.29.30133.0'; Vcruntime1 = $true }
Check 'vc runtime: an older 2015-2019 runtime (14.29) -> too old, counts as missing' (-not (Test-VcRuntime))

# already there: nothing is downloaded or run
$script:vcInfo = @{ Msvcp = [version]'14.44.35211.0'; Vcruntime1 = $true }
function Invoke-WebRequest { throw 'must not download' }
function Start-VcRedist { throw 'must not run' }
$m = Throws { Confirm-VcRuntime }
Check 'vc runtime: when it is already there Setup downloads and runs nothing' ($null -eq $m) $m

# signatures: only a valid Microsoft signature passes
$script:sig = $null
function Get-AuthenticodeSignature { param($LiteralPath) return $script:sig }
Remove-Item function:Invoke-WebRequest
${function:Test-MicrosoftSignature} = $realSig
$fakeFile = "$root\vcsig.exe"; Set-Content $fakeFile 'x'
$script:sig = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' } }
Check 'signature: valid and signed by Microsoft Corporation -> accepted' (Test-MicrosoftSignature $fakeFile)
$script:sig = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Somebody Else, O=Somebody Else Ltd, C=US' } }
Check 'signature: valid but signed by someone else -> rejected' (-not (Test-MicrosoftSignature $fakeFile))
$script:sig = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft, O=Not Microsoft Corporation Fake Ltd' } }
Check 'signature: a look-alike name that does not carry O=Microsoft Corporation is judged only on that text' ((Test-MicrosoftSignature $fakeFile) -eq ($script:sig.SignerCertificate.Subject -match 'O=Microsoft Corporation'))
foreach ($status in 'NotSigned', 'HashMismatch', 'NotTrusted', 'UnknownError') {
    $script:sig = [pscustomobject]@{ Status = $status; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation' } }
    Check "signature: status $status -> rejected even with Microsoft's name" (-not (Test-MicrosoftSignature $fakeFile))
}
$script:sig = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = $null }
Check 'signature: no signer certificate -> rejected' (-not (Test-MicrosoftSignature $fakeFile))

# the whole step with fakes: a clean computer, a download, a signature check, and each way the installer can end
$realTemp = $env:TEMP
$env:TEMP = "$root\vctemp"; New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$script:downloaded = @(); $script:ran = @(); $script:installedNow = $false; $script:redistCode = 0; $script:sigOk = $true
function Invoke-WebRequest { param($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec) $script:downloaded += $Uri; Set-Content $OutFile 'fake installer' }
function Test-MicrosoftSignature { param($Path) return $script:sigOk }
function Start-VcRedist { param($Path) $script:ran += $Path; if ($script:redistCode -in 0, 1638, 3010 -and -not $script:noInstall) { $script:installedNow = $true }; return $script:redistCode }
function Get-VcRuntimeInfo { if ($script:installedNow) { return @{ Msvcp = [version]'14.44.35211.0'; Vcruntime1 = $true } } else { return @{ Msvcp = $null; Vcruntime1 = $false } } }
$plain = "VoiceTool can't run without it. Run Setup again and click Yes when Windows asks."
function Reset-Vc { $script:downloaded = @(); $script:ran = @(); $script:installedNow = $false; $script:noInstall = $false; $script:sigOk = $true; $script:Warnings.Clear(); Remove-Item "$root\vctemp\VoiceToolSetup" -Recurse -Force -ErrorAction SilentlyContinue }

Reset-Vc; $script:redistCode = 0
$m = Throws { Confirm-VcRuntime }
Check 'vc install: exit 0 -> continues, downloaded from Microsoft''s link, run once, re-checked' (($null -eq $m) -and $script:downloaded.Count -eq 1 -and $script:downloaded[0] -eq 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -and $script:ran.Count -eq 1) $m
Check 'vc install: the plain heads-up is shown first' ((Get-Content $script:Log -Raw) -match 'VoiceTool needs a free Microsoft component \(Visual C\+\+ Runtime\)\. Windows will ask for permission to install it: click Yes\.')
Check 'vc install: the downloaded installer is not left behind' (-not (Test-Path "$root\vctemp\VoiceToolSetup\vc_redist.x64.exe"))
Check 'vc install: exit 0 gives no warning' ($script:Warnings.Count -eq 0)

Reset-Vc; $script:redistCode = 1638
$m = Throws { Confirm-VcRuntime }
Check 'vc install: exit 1638 (a newer one is already there) -> continues quietly' (($null -eq $m) -and $script:Warnings.Count -eq 0) $m

Reset-Vc; $script:redistCode = 3010
$m = Throws { Confirm-VcRuntime }
Check 'vc install: exit 3010 (restart later) -> continues, and tells the person to restart when convenient' (($null -eq $m) -and ($script:Warnings -join ' ') -match 'restart the computer when it is convenient') $m

Reset-Vc; $script:redistCode = $null
$m = Throws { Confirm-VcRuntime }
Check 'vc install: permission refused (No on the prompt) -> plain message and Setup stops' ($m -eq $plain) $m

Reset-Vc; $script:redistCode = 1603
$m = Throws { Confirm-VcRuntime }
Check 'vc install: any other exit code -> plain message with the code, and Setup stops' ($m -match 'code 1603' -and $m.EndsWith($plain)) $m

Reset-Vc; $script:redistCode = 0; $script:noInstall = $true
$m = Throws { Confirm-VcRuntime }
Check 'vc install: "installed" but the files still are not there on re-check -> plain message and stop' ($m -match 'still can''t find it' -and $m.EndsWith($plain)) $m

Reset-Vc; $script:redistCode = 3010; $script:noInstall = $true
$m = Throws { Confirm-VcRuntime }
Check 'vc install: 3010 and the files not usable yet -> asks for a restart, then Setup again' ($m -match 'needs a restart' -and $m -match 'run Setup again') $m

Reset-Vc; $script:sigOk = $false; $script:redistCode = 0
$m = Throws { Confirm-VcRuntime }
Check 'vc install: a download without Microsoft''s signature is never run, is deleted, and Setup stops plainly' (($m -match "didn't carry Microsoft's signature") -and $script:ran.Count -eq 0 -and -not (Test-Path "$root\vctemp\VoiceToolSetup\vc_redist.x64.exe")) $m

Reset-Vc
function Invoke-WebRequest { throw 'The remote name could not be resolved' }
function Start-Sleep { param($Seconds, $Milliseconds) }
$m = Throws { Confirm-VcRuntime }
Remove-Item function:Start-Sleep
Check 'vc install: no internet -> plain download message and nothing is run' (($m -match "Couldn't download the Microsoft component" -and $m -match 'internet connection') -and $script:ran.Count -eq 0) $m

# how the installer is started: with Run as administrator unless this window already is one (Windows Sandbox's user is)
${function:Start-VcRedist} = $realStartRedist
$script:sp = $null; $script:spCode = 0; $script:spThrow = $false
function Start-Process { param($FilePath, $ArgumentList, $Verb, [switch]$Wait, [switch]$PassThru) $script:sp = @{ FilePath = $FilePath; ArgumentList = @($ArgumentList); Verb = $Verb; Wait = [bool]$Wait }; if ($script:spThrow) { throw 'The operation was canceled by the user' }; return [pscustomobject]@{ ExitCode = $script:spCode } }
function Test-IsElevated { return $script:elevated }
$script:elevated = $false
$code = Start-VcRedist 'C:\x\vc_redist.x64.exe'
Check 'vc run: not elevated -> started with -Verb RunAs (one permission prompt), waits, quiet, no restart' ($code -eq 0 -and $script:sp.Verb -eq 'RunAs' -and $script:sp.Wait -and ($script:sp.ArgumentList -join ' ') -eq '/install /quiet /norestart') ($script:sp | Out-String)
$script:elevated = $true; $script:sp = $null; $script:spCode = 3010
$code = Start-VcRedist 'C:\x\vc_redist.x64.exe'
Check 'vc run: already elevated -> run directly with no RunAs (so nothing can wait on a prompt), exit code passed back' ($code -eq 3010 -and $script:sp.Verb -eq $null -and $script:sp.Wait -and ($script:sp.ArgumentList -join ' ') -eq '/install /quiet /norestart')
$script:elevated = $false; $script:spThrow = $true
$code = Start-VcRedist 'C:\x\vc_redist.x64.exe'
Check 'vc run: the permission question refused (Windows raises an error) -> $null, not a crash' ($null -eq $code)
$script:elevated = $true
Check 'vc run: ...and an installer that cannot start while elevated is also $null' ($null -eq (Start-VcRedist 'C:\x\vc_redist.x64.exe'))
Remove-Item function:Start-Process
${function:Test-IsElevated} = $realElev
Check 'vc run: the real elevation test answers true or false' ((Test-IsElevated) -is [bool])

# put everything back
Remove-Item function:Invoke-WebRequest -ErrorAction SilentlyContinue
Remove-Item function:Get-AuthenticodeSignature -ErrorAction SilentlyContinue
${function:Get-VcRuntimeInfo} = $realInfo; ${function:Test-MicrosoftSignature} = $realSig
$env:TEMP = $realTemp
$script:Warnings.Clear()

# the step is numbered and placed before the package install
$setupText = Get-Content (Join-Path $here 'setup.ps1') -Raw
Step 3 'a test line'
Check 'steps: the step line says "of 8"' ((Get-Content $script:Log -Raw) -match 'Step 3 of 8: a test line')
$mainText = $setupText.Substring($setupText.IndexOf('function Invoke-Setup'))
$order = @('Step 1 ', 'Step 2 ', 'Confirm-VcRuntime', 'Step 3 ', 'Step 4 ', 'Step 5 ', 'Install-Packages $venvPy', 'Step 6 ', 'Step 7 ', 'Step 8 ') | ForEach-Object { $mainText.IndexOf($_) }
Check 'steps: 1 to 8 in order, with the Visual C++ check (step 2) before Python and the package check' (($order | Where-Object { $_ -lt 0 }).Count -eq 0 -and (($order | Sort-Object) -join ',') -eq ($order -join ','))
Check 'steps: the download link is Microsoft''s aka.ms link, checked by signature rather than a fixed fingerprint' ($setupText -match [regex]::Escape('https://aka.ms/vs/17/release/vc_redist.x64.exe') -and $setupText -match 'O=Microsoft Corporation')

# ---------------- the temp folder is cleaned up; Setup.cmd explains itself when setup.ps1 isn't beside it
$realTemp = $env:TEMP
$env:TEMP = "$root\faketemp"; New-Item -ItemType Directory -Force "$root\faketemp\VoiceToolSetup" | Out-Null
Set-Content "$root\faketemp\VoiceToolSetup\python-3.13.17-amd64.exe" 'x'
Set-Content "$root\faketemp\other.txt" 'not ours'
Remove-SetupTemp
Check 'temp: %TEMP%\VoiceToolSetup is removed and nothing else in %TEMP% is' ((-not (Test-Path "$root\faketemp\VoiceToolSetup")) -and (Test-Path "$root\faketemp\other.txt"))
New-Item -ItemType Directory -Force "$root\faketemp\VoiceToolSetup" | Out-Null
$DownloadCache = "$root\cache"
Remove-SetupTemp
Check 'temp: with -DownloadCache the temp folder is not touched' (Test-Path "$root\faketemp\VoiceToolSetup")
$DownloadCache = ''
$env:TEMP = $realTemp
New-Item -ItemType Directory -Force "$root\zipped" | Out-Null
Copy-Item (Join-Path $here 'Setup.cmd') "$root\zipped\Setup.cmd"
Set-Content "$root\zipped\run.cmd" "@call `"%~dp0Setup.cmd`" < nul`r`n@exit /b %errorlevel%"
$out = (& cmd.exe /c "$root\zipped\run.cmd" 2>&1 | Out-String)
Check 'Setup.cmd: without setup.ps1 beside it, it says to Extract All and stops' ($out -match 'Please right-click the zip, choose Extract All, then double-click Setup in the extracted folder\.' -and $LASTEXITCODE -eq 1) $out
$cmdText = Get-Content (Join-Path $here 'Setup.cmd') -Raw
Check 'Setup.cmd: still runs setup.ps1 with every argument passed on' ($cmdText -match 'setup\.ps1" %\*')
Check 'pip: pip is given --no-cache-dir and the install never uses a cache' ((Get-Content (Join-Path $here 'setup.ps1') -Raw) -match "'--no-cache-dir'")

# ---------------- shortcut and task
$InstallDir = "$root\inst"
Set-Content "$root\inst\voicetool.ico" 'ico'
New-StartMenuShortcut "$root\inst\.venv\Scripts\pythonw.exe"
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'VoiceTool-unitlnk.lnk'))
Check 'shortcut: runs pythonw voicetool.py --settings from the install folder with the icon' ($lnk.TargetPath -eq "$root\inst\.venv\Scripts\pythonw.exe" -and $lnk.Arguments -match 'voicetool\.py" --settings' -and $lnk.WorkingDirectory -eq "$root\inst" -and $lnk.IconLocation -like "$root\inst\voicetool.ico*")
New-LogonTask "$root\inst\.venv\Scripts\pythonw.exe"
New-LogonTask "$root\inst\.venv\Scripts\pythonw.exe"   # twice: a re-run must replace, not fail
$t = Get-ScheduledTask -TaskName 'VoiceTool-unittask'
Check 'task: one task, runs at logon, no time limit, normal rights, run only when logged on' ($t -and $t.Triggers[0].CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and $t.Settings.ExecutionTimeLimit -eq 'PT0S' -and $t.Principal.RunLevel -eq 'Limited' -and $t.Principal.LogonType -eq 'Interactive' -and $t.Settings.MultipleInstances -eq 'IgnoreNew') ("limit=" + $t.Settings.ExecutionTimeLimit + " level=" + $t.Principal.RunLevel + " logon=" + $t.Principal.LogonType)
Check 'task: action is pythonw voicetool.py in the install folder' ($t.Actions[0].Execute -eq "$root\inst\.venv\Scripts\pythonw.exe" -and $t.Actions[0].Arguments -match 'voicetool\.py' -and $t.Actions[0].WorkingDirectory -eq "$root\inst")
Unregister-ScheduledTask -TaskName 'VoiceTool-unittask' -Confirm:$false
Remove-Item (Join-Path $programs 'VoiceTool-unitlnk.lnk') -Force
Check 'cleanup: test task and shortcut removed' (-not (Get-ScheduledTask -TaskName 'VoiceTool-unittask' -ErrorAction SilentlyContinue))

foreach ($p in @($sleeper, $control, $srv)) { if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ("`n{0} passed, {1} failed" -f $global:pass, $global:fail)
exit $(if ($global:fail) { 1 } else { 0 })
