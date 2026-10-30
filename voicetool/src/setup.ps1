<#
  VoiceTool one-click setup (run by Setup.cmd). Everything stays in one folder. The only thing that needs administrator
  rights is Microsoft's Visual C++ Runtime, and only if the computer doesn't have it (Windows asks once).

  What it does, in order:
    1. Checks the computer (Windows 64-bit, free space, internet) and copies VoiceTool into the install folder.
    2. Makes sure Microsoft's free Visual C++ Runtime is there (the speech and tidy-up models need it). If not, it is
       downloaded from Microsoft, its signature is checked, and it is installed; Windows asks once for permission.
    3. Finds Python 3.13 (or installs it just for VoiceTool, per user, with no PATH change).
    4. Makes VoiceTool's private Python environment and installs the pinned packages (requirements-friend.txt).
    5. Runs fetch_models.py: downloads (and checks) the tidy-up model and its engine, and the speech model if it is missing.
    6. Detects the graphics card, writes this computer's settings into config.json.
    7. Adds a Start menu shortcut ("VoiceTool"), an "Uninstall VoiceTool" shortcut and a sign-in task for the current user,
       writes a small marker file (.voicetool-installed) into the install folder so Uninstall knows it is the real install,
       starts VoiceTool and opens its window.

  Run it again at any time to repair: your words, fixes, style, settings and history are never overwritten.
  Setup never takes over a start-up task or Start menu shortcut that already has the same name but belongs to something else:
  it stops and says so (add -Force to replace them on purpose).

  Parameters (all optional):
    -InstallDir      where VoiceTool goes (default: %LOCALAPPDATA%\VoiceTool)
    -TaskName        name of the sign-in task (default: VoiceTool)
    -ShortcutName    name of the Start menu shortcut (default: VoiceTool)
    -SkipTask        don't create the sign-in task
    -SkipShortcut    don't create the Start menu shortcut
    -NoLaunch        don't start VoiceTool or open any window (download progress shows in this console instead)
    -DownloadCache   a folder where downloaded files are kept and reused when their SHA-256 matches
    -Force           replace a start-up task or Start menu shortcut of the same name that points somewhere else
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'VoiceTool'),
    [string]$TaskName = 'VoiceTool',
    [string]$ShortcutName = 'VoiceTool',
    [switch]$SkipTask,
    [switch]$SkipShortcut,
    [switch]$NoLaunch,
    [string]$DownloadCache = '',
    [switch]$Force
)

$script:InstallDirGiven = $PSBoundParameters.ContainsKey('InstallDir')   # (if not, a plain folder may replace an unusable default)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# ---- pinned downloads (official sources; each one is checked against this SHA-256 before it is used) ----
$PythonVersion = '3.13.17'
$PythonUrl     = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-amd64.exe"
$PythonSha256  = '5da012101ba2e17113a9a1cd901e9ce1441086f2b7ecb5de06ec94163899a190'
$PythonSize    = 29941280
$MinFreeGBFresh  = 6
$MinFreeGBRepair = 1

$script:Src = $PSScriptRoot
$script:Log = $null
$script:Warnings = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------- small helpers

function Write-Log([string]$Text) {
    if ($script:Log) { try { Add-Content -LiteralPath $script:Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $Text) -Encoding UTF8 } catch { } }
}
function Say([string]$Text, [string]$Color = 'White') { Write-Host $Text -ForegroundColor $Color; Write-Log $Text }
$script:StepCount = 8
function Step([int]$N, [string]$Text) { Write-Host ''; Say ("Step ${N} of ${script:StepCount}: $Text") 'Cyan' }
function Warn([string]$Text) { [void]$script:Warnings.Add($Text); Say ("  Note: $Text") 'Yellow' }
function Fail([string]$Text) { throw $Text }

function Format-Quoted([string[]]$ArgList) {
    ($ArgList | ForEach-Object { if ($_ -match '[\s"]' -or $_ -eq '') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}

# Runs a program, waits, keeps a dot moving so the window never looks frozen, logs everything. Returns the exit code.
function Invoke-Native([string]$Exe, [string[]]$ArgList, [switch]$Heartbeat, [string]$WorkDir = $null) {
    $out = [IO.Path]::GetTempFileName(); $err = [IO.Path]::GetTempFileName()
    try {
        $params = @{ FilePath = $Exe; ArgumentList = (Format-Quoted $ArgList); PassThru = $true; NoNewWindow = $true
                     RedirectStandardOutput = $out; RedirectStandardError = $err }
        if ($WorkDir) { $params.WorkingDirectory = $WorkDir }
        $p = Start-Process @params
        $null = $p.Handle
        $started = Get-Date; $lastDot = $started
        while (-not $p.HasExited) {
            Start-Sleep -Milliseconds 500
            if ($Heartbeat -and ((Get-Date) - $lastDot).TotalSeconds -ge 10) {
                Write-Host -NoNewline '.'; $lastDot = Get-Date
            }
        }
        $p.WaitForExit()
        if ($Heartbeat -and ((Get-Date) - $started).TotalSeconds -ge 10) { Write-Host '' }
        Write-Log ("$Exe $($ArgList -join ' ')  ->  exit " + $p.ExitCode)
        foreach ($f in @($out, $err)) { $t = Get-Content -LiteralPath $f -ErrorAction SilentlyContinue; if ($t) { Write-Log ($t -join "`n") } }
        return [int]$p.ExitCode
    } finally {
        Remove-Item -LiteralPath $out, $err -Force -ErrorAction SilentlyContinue
    }
}

function Get-FileSha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# Downloads a file, checks its SHA-256, retries a few times. Reuses a copy that is already there and matches.
function Get-VerifiedFile([string]$Url, [string]$Sha256, [string]$Dest, [string]$What) {
    if ((Test-Path -LiteralPath $Dest) -and ((Get-FileSha256 $Dest) -eq $Sha256)) {
        Say "  Using the copy of $What that is already downloaded (checked)."
        return
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Dest) | Out-Null
    $lastProblem = ''
    for ($try = 1; $try -le 3; $try++) {
        try {
            Say "  Downloading $What (try $try of 3)..."
            Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing -TimeoutSec 600
            if ((Get-FileSha256 $Dest) -eq $Sha256) { Say "  $What downloaded and checked."; return }
            $lastProblem = 'the downloaded file did not pass its safety check'
            Remove-Item -LiteralPath $Dest -Force -ErrorAction SilentlyContinue
        } catch {
            $lastProblem = $_.Exception.Message
            Write-Log "download error: $lastProblem"
        }
        Start-Sleep -Seconds (4 * $try)
    }
    Fail "Couldn't download $What ($lastProblem). Please check your internet connection and run Setup again."
}

# ---------------------------------------------------------------- checks

function Test-Environment {
    if (-not [Environment]::Is64BitOperatingSystem) { Fail 'VoiceTool needs a 64-bit version of Windows 10 or 11.' }
    if ([Environment]::OSVersion.Version.Major -lt 10) { Fail 'VoiceTool needs Windows 10 or Windows 11.' }
    if ($script:Src -match '\\Temp\\Temp\d+_[^\\]*\.zip\\') {
        Fail 'It looks like Setup was started from inside the zip file. Close this window, right-click the zip file, choose "Extract All", and then double-click Setup in the new folder.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $script:Src 'voicetool.py'))) {
        Fail "Setup can't find VoiceTool's files next to it. Make sure you unzipped the whole folder before running Setup."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $script:Src 'requirements-friend.txt'))) { Fail 'The setup list (requirements-friend.txt) is missing from this folder.' }
    if (-not (Get-ChildItem -LiteralPath (Join-Path $script:Src 'wheels') -Filter '*.whl' -ErrorAction SilentlyContinue)) {
        Fail 'The "wheels" folder that comes with VoiceTool is missing or empty. Make sure you unzipped the whole folder before running Setup.'
    }
    if ($InstallDir.Length -gt 90) { Fail 'The install folder name is too long. Please choose a shorter one.' }
    if ($InstallDir -match '[^\p{IsBasicLatin}]') { Warn 'The install folder has accented or non-English letters in its name. If setup stops, run it again with a plain folder name.' }
}

# The install folder to use. Some of VoiceTool's parts can't cope with accented or non-English letters in a folder name, and
# the default folder sits under the Windows user name. If the folder was not chosen (no -InstallDir) and the default has such
# letters, VoiceTool goes into C:\VoiceTool instead. A folder that was chosen on purpose is left as it is.
function Get-SafeInstallDir([string]$Dir, [bool]$Chosen) {
    if (-not $Chosen -and $Dir -match '[^\p{IsBasicLatin}]') { return (Join-Path $env:SystemDrive 'VoiceTool') }
    return $Dir
}

function Test-DiskSpace([bool]$Repair) {
    $root = [IO.Path]::GetPathRoot($InstallDir)
    $need = if ($Repair) { $MinFreeGBRepair } else { $MinFreeGBFresh }
    $free = $null
    try { $free = ([IO.DriveInfo]$root).AvailableFreeSpace / 1GB } catch { Write-Log "disk check skipped: $_" }
    if ($null -eq $free) { return }
    if ($free -lt $need) {
        Fail ("There isn't enough free space on drive $root. VoiceTool needs about $need GB free and only {0:N1} GB is available. Free up some space and run Setup again." -f $free)
    }
    Say ("  Free space on ${root}: {0:N1} GB (needs about $need GB). Good." -f $free)
}

function Test-Internet {
    foreach ($u in @('https://pypi.org/simple/', 'https://github.com')) {
        try { Invoke-WebRequest -Uri $u -Method Head -UseBasicParsing -TimeoutSec 20 | Out-Null; return }
        catch { Write-Log "internet check $u : $($_.Exception.Message)" }
    }
    Fail "Setup can't reach the internet. VoiceTool needs it once, to download its parts. Please connect, check that a firewall isn't blocking it, and run Setup again."
}

# ---------------------------------------------------------------- not taking over what belongs to something else

function Test-PointsIntoInstall([string]$Text) {
    if (-not $Text) { return $false }
    $prefix = ($InstallDir.TrimEnd('\') + '\').ToLowerInvariant()
    return $Text.ToLowerInvariant().Contains($prefix)
}

# A start-up task or Start menu shortcut with one of our names that does NOT point into this install folder belongs to
# something else (an older VoiceTool somewhere else, another program). Stop and say so; -Force replaces it on purpose.
function Test-NameConflicts {
    $problems = New-Object System.Collections.ArrayList
    if (-not $SkipTask) {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($task) {
            $text = (@($task.Actions) | ForEach-Object { "$($_.Execute) $($_.Arguments) $($_.WorkingDirectory)" }) -join ' '
            if (-not (Test-PointsIntoInstall $text)) { [void]$problems.Add("a start-up task named ""$TaskName"" (it runs: $($text.Trim()))") }
        }
    }
    if (-not $SkipShortcut) {
        foreach ($name in @($ShortcutName, "Uninstall $ShortcutName")) {
            $link = Join-Path ([Environment]::GetFolderPath('Programs')) ($name + '.lnk')
            if (Test-Path -LiteralPath $link) {
                $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
                if (-not (Test-PointsIntoInstall "$($sc.TargetPath) $($sc.Arguments) $($sc.WorkingDirectory)")) {
                    [void]$problems.Add("a Start menu shortcut named ""$name"" (it opens: $($sc.TargetPath))")
                }
            }
        }
    }
    if ($problems.Count -eq 0) { return }
    if ($Force) { Warn ('Replacing ' + ($problems -join ' and ') + ', because -Force was given.'); return }
    Fail ("This computer already has " + ($problems -join ' and ') + ", and it doesn't belong to the VoiceTool folder being set up now, so Setup stopped rather than replace it. " +
          "If you installed VoiceTool before and want this copy to take its place, run Setup again with -Force: open a Command Prompt in this folder and type   Setup.cmd -Force   " +
          "(or ask whoever gave you VoiceTool).")
}

# ---------------------------------------------------------------- stopping a running VoiceTool (only ones inside the install folder)

function Stop-InstalledProcesses {
    # Only VoiceTool's own programs, and only ones that run from inside the install folder. (A virtual-environment
    # Python starts the real interpreter as a child, so the folder can be in the command line instead of the program path.)
    $prefix = ($InstallDir.TrimEnd('\') + '\').ToLowerInvariant()
    if ($prefix.Length -lt 8) { return }     # never match everything
    $names = @('python.exe', 'pythonw.exe', 'llama-server.exe', 'claude.exe')   # (claude.exe: VoiceTool's own Claude helper, which runs in claude_workdir)
    $found = $false
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $names -contains $_.Name.ToLowerInvariant() -and (
            ($_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant().StartsWith($prefix)) -or
            ($_.CommandLine -and $_.CommandLine.ToLowerInvariant().Contains($prefix)))
    } | ForEach-Object {
        $found = $true
        Write-Log "stopping $($_.Name) (pid $($_.ProcessId))"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($found) { Start-Sleep -Seconds 2 }
}

# ---------------------------------------------------------------- step: copy the app

$UserFiles = @('config.json', 'words.txt', 'style.md', 'corrections.json')   # copied only when the install doesn't have them yet

function Copy-App {
    if ((Resolve-Path -LiteralPath $script:Src).Path.TrimEnd('\') -ieq $InstallDir.TrimEnd('\')) {
        Say '  Setup was started from the install folder itself; nothing to copy.'
        return
    }
    $s = $script:Src.TrimEnd('\')
    $xd = @('.venv', 'python', 'runtime', '_downloads', '__pycache__', '.pytest_cache', 'rehearsal', 'backups', 'recordings',
            'journal', 'claude_workdir', '.git') | ForEach-Object { Join-Path $s $_ }
    $xd += (Join-Path $s 'models\qwen3-4b-instruct-2507')
    $xd += $InstallDir.TrimEnd('\')
    # File exclusions are FULL paths: robocopy matches a bare name anywhere in the tree, which once skipped the speech
    # model's own models\parakeet-tdt-0.6b-v2\config.json because the user's config.json was excluded by name. Only the
    # top-level user files and development files are left out; the extension patterns (never real data) match anywhere.
    $topLevel = @('dryrun_check.py', 'ROUND1_DONE.txt', 'history.jsonl', 'usage.jsonl', 'voicetool.log', 'fetch_models.log',
                  'setup.log', '.voicetool-installed') + $UserFiles
    # (robocopy takes a wildcard only in a bare name, not in a full path, so the top-level test files are listed one by one)
    $tests = @(Get-ChildItem -LiteralPath $s -File -Filter 'test_*' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.py', '.ps1' } | ForEach-Object { $_.FullName })
    $xf = $tests + @($topLevel | ForEach-Object { Join-Path $s $_ }) + @('*.pyc', '*.lock', '*.new')
    $roboArgs = @($s, $InstallDir.TrimEnd('\'), '/E', '/R:2', '/W:2', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XD') + $xd + @('/XF') + $xf
    $code = Invoke-Native 'robocopy.exe' $roboArgs
    if ($code -ge 8) { Fail "Couldn't copy VoiceTool into $InstallDir (copy error $code). Check that the folder isn't open elsewhere and that you have space, then run Setup again." }
    foreach ($f in $UserFiles) {
        $from = Join-Path $s $f; $to = Join-Path $InstallDir $f
        if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $to)) { Copy-Item -LiteralPath $from -Destination $to }
    }
}

# The marker that tells Uninstall "this folder is the real, installed VoiceTool" (not the downloaded setup folder).
# It holds the install folder's path. Never copied from the setup folder.
$MarkerName = '.voicetool-installed'
function Write-InstallMarker {
    [IO.File]::WriteAllText((Join-Path $InstallDir $MarkerName), $InstallDir + "`r`n", (New-Object Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------- step: Microsoft Visual C++ Runtime
# onnxruntime (the speech model) and llama.cpp (the tidy-up model) are built with Microsoft's compiler and need its
# "Visual C++ 2015-2022 x64 Redistributable". A clean Windows doesn't have it, and Python's own copy of the runtime
# isn't enough for them.
#
# How "is it there?" is decided: by the files themselves, not the registry. msvcp140.dll and vcruntime140_1.dll in
# System32 are what the two programs actually load, and the registry key
# (HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64) can say "Installed" after the files were removed or while an
# older build is still in place. The file version of msvcp140.dll (14.40 or newer) says whether the runtime is new
# enough for the current onnxruntime builds; older 2015-2019 runtimes have the same file names but lack newer functions.
$VcRedistUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
$VcMinVersion = [version]'14.40'

# What is in System32 now: @{ Msvcp = <version or $null>; Vcruntime1 = <$true if vcruntime140_1.dll is there> }. (Overridden in tests.)
function Get-VcRuntimeInfo {
    $sys = Join-Path $env:windir 'System32'
    $info = @{ Msvcp = $null; Vcruntime1 = (Test-Path -LiteralPath (Join-Path $sys 'vcruntime140_1.dll')) }
    $msvcp = Join-Path $sys 'msvcp140.dll'
    if (Test-Path -LiteralPath $msvcp) {
        try {
            $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($msvcp)
            $info.Msvcp = [version]('{0}.{1}.{2}.{3}' -f $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart, $v.FilePrivatePart)
        } catch { Write-Log "could not read the version of msvcp140.dll: $_" }
    }
    return $info
}

# $true when the runtime is installed and new enough.
function Test-VcRuntime {
    $i = Get-VcRuntimeInfo
    return [bool]($i.Vcruntime1 -and $i.Msvcp -and $i.Msvcp -ge $VcMinVersion)
}

function Test-IsElevated {
    try { return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    catch { return $false }
}

# The file must be Microsoft's: a valid Authenticode signature whose signer is Microsoft Corporation. (The download link is
# a "latest version" link, so its bytes change; a signature check is the right test instead of a fixed fingerprint.)
function Test-MicrosoftSignature([string]$Path) {
    $sig = Get-AuthenticodeSignature -LiteralPath $Path
    return [bool]($sig.Status -eq 'Valid' -and $sig.SignerCertificate -and $sig.SignerCertificate.Subject -match 'O=Microsoft Corporation')
}

# Runs the installer quietly and returns its exit code, or $null if the person said No to Windows' permission question
# (or Windows couldn't start it). When this window is already running as administrator it just runs; otherwise the
# installer is started with "Run as administrator", which is the one prompt the person sees.
function Start-VcRedist([string]$Path) {
    $redistArgs = @('/install', '/quiet', '/norestart')
    try {
        if (Test-IsElevated) { $p = Start-Process -FilePath $Path -ArgumentList $redistArgs -Wait -PassThru }
        else { $p = Start-Process -FilePath $Path -ArgumentList $redistArgs -Verb RunAs -Wait -PassThru }
        return [int]$p.ExitCode
    } catch {
        Write-Log "the Visual C++ installer did not start: $_"
        return $null
    }
}

function Install-VcRuntime {
    $why = "VoiceTool can't run without it. Run Setup again and click Yes when Windows asks."
    Say '  VoiceTool needs a free Microsoft component (Visual C++ Runtime). Windows will ask for permission to install it: click Yes.' 'Yellow'
    $cacheDir = Join-Path $env:TEMP 'VoiceToolSetup'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $file = Join-Path $cacheDir 'vc_redist.x64.exe'
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue      # never reuse an old copy of a "latest" download
    $lastProblem = ''
    for ($try = 1; $try -le 3; $try++) {
        try {
            Say "  Downloading the Microsoft component (about 18 MB, try $try of 3)..."
            Invoke-WebRequest -Uri $VcRedistUrl -OutFile $file -UseBasicParsing -TimeoutSec 300
            if (Test-Path -LiteralPath $file) { $lastProblem = ''; break }
        } catch { $lastProblem = $_.Exception.Message; Write-Log "download error: $lastProblem" }
        Start-Sleep -Seconds (3 * $try)
    }
    if ($lastProblem -or -not (Test-Path -LiteralPath $file)) {
        Fail "Couldn't download the Microsoft component ($lastProblem). Please check your internet connection and run Setup again."
    }
    if (-not (Test-MicrosoftSignature $file)) {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        Fail "The download didn't carry Microsoft's signature, so Setup stopped and did not run it. Please run Setup again; if it keeps happening, tell whoever sent you VoiceTool."
    }
    Say '  Installing it now. Please click Yes if Windows asks (the question can appear behind this window).'
    $code = Start-VcRedist $file
    Write-Log "vc_redist exit: $code"
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    if ($null -eq $code) { Fail $why }
    if ($code -notin @(0, 1638, 3010)) { Fail ("The Microsoft component didn't install (code $code). " + $why) }
    if (-not (Test-VcRuntime)) {
        if ($code -eq 3010) { Fail 'Windows needs a restart to finish installing the Microsoft component. Please restart the computer, then run Setup again.' }
        Fail ("The Microsoft component seems to be installed, but VoiceTool still can't find it. " + $why)
    }
    if ($code -eq 3010) { Warn 'Windows asked for a restart to finish installing the Microsoft component. VoiceTool can keep going now; please restart the computer when it is convenient.' }
    elseif ($code -eq 1638) { Say '  A newer version of the component was already installed. Good.' }
    else { Say '  The Microsoft component is installed.' }
}

function Confirm-VcRuntime {
    if (Test-VcRuntime) { Say '  The Microsoft Visual C++ Runtime is already on this computer. Good.'; return }
    Install-VcRuntime
}

# ---------------------------------------------------------------- step: Python 3.13

function Test-PythonExe([string]$Exe) {
    if (-not $Exe -or -not (Test-Path -LiteralPath $Exe)) { return $false }
    if ($Exe -match '\\WindowsApps\\') { return $false }     # the Microsoft Store copy behaves differently inside virtual environments
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
        $r = & $Exe -c "import sys,struct;ok=sys.version_info[:2]==(3,13) and struct.calcsize('P')==8;import tkinter,venv,ensurepip;print('VT_OK' if ok else 'VT_NO')" 2>$null
        return ($LASTEXITCODE -eq 0 -and ($r -join '') -eq 'VT_OK')
    } catch { return $false } finally { $ErrorActionPreference = $old }
}

# Python 3.13 installs that are not ours (the py launcher's, python.org's default folder, and whatever Windows has registered).
# Overridden in tests.
function Get-OtherPython313Paths {
    $own = [IO.Path]::GetFullPath((Join-Path $InstallDir 'python\python.exe'))
    $paths = New-Object System.Collections.ArrayList
    $py = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($py) {
        $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { $p = & $py.Source -3.13 -c "import sys;print(sys.executable)" 2>$null; if ($LASTEXITCODE -eq 0 -and $p) { [void]$paths.Add(($p -join '').Trim()) } } catch { } finally { $ErrorActionPreference = $old }
    }
    [void]$paths.Add((Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe'))
    foreach ($key in 'HKCU:\Software\Python\PythonCore\3.13\InstallPath', 'HKLM:\SOFTWARE\Python\PythonCore\3.13\InstallPath',
                     'HKLM:\SOFTWARE\WOW6432Node\Python\PythonCore\3.13\InstallPath') {
        try { $p = (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).'(default)'; if ($p) { [void]$paths.Add((Join-Path $p 'python.exe')) } } catch { }
    }
    return @($paths | Where-Object { $_ -and (Test-Path -LiteralPath $_) -and $_ -notmatch '\\WindowsApps\\' -and
                                     ([IO.Path]::GetFullPath($_) -ine $own) } | Select-Object -Unique)
}

# A Python 3.13 that is already on this computer but that VoiceTool can't use (for example it was installed without Tcl/Tk).
# Running python.org's installer over it could damage it, so Setup stops instead. Returns its path, or $null.
function Find-UnusablePython313 {
    foreach ($p in (Get-OtherPython313Paths)) { if (-not (Test-PythonExe $p)) { return $p } }
    return $null
}

# Finds a usable Python 3.13, or installs VoiceTool's own; never installs over a 3.13 that is already there.
function Resolve-BasePython {
    $base = Find-Python313
    if ($base) { Say "  Found Python 3.13: $base"; return $base }
    $unusable = Find-UnusablePython313
    if ($unusable) {
        Fail ("Python 3.13 is already on this computer ($unusable), but VoiceTool can't use it (it is missing a part VoiceTool needs, such as Tcl/Tk). " +
              "Setup stopped instead of installing another copy over it. To fix this, open Windows Settings, then Apps, find ""Python 3.13"", choose Modify, " +
              "and make sure ""tcl/tk and IDLE"" is ticked, then run Setup again. (Or remove that Python 3.13 and run Setup again; Setup then installs its own.)")
    }
    $base = Install-OwnPython
    Say "  Python is ready: $base"
    return $base
}

function Find-Python313 {
    $candidates = New-Object System.Collections.ArrayList
    [void]$candidates.Add((Join-Path $InstallDir 'python\python.exe'))        # one we installed on an earlier run
    $py = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($py) {
        $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { $p = & $py.Source -3.13 -c "import sys;print(sys.executable)" 2>$null; if ($LASTEXITCODE -eq 0 -and $p) { [void]$candidates.Add(($p -join '').Trim()) } } catch { } finally { $ErrorActionPreference = $old }
    }
    [void]$candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\python.exe'))
    foreach ($c in $candidates) { if (Test-PythonExe $c) { return $c } }
    return $null
}

# The arguments for python.org's installer: just for this user, quiet, into <install>\python, with Tcl/Tk (the download
# window needs it), without the py launcher, without touching PATH or file associations.
function Get-PythonInstallerArgs([string]$TargetDir, [string]$LogPath) {
    @('/quiet', 'InstallAllUsers=0', "TargetDir=$TargetDir", 'Include_launcher=0', 'InstallLauncherAllUsers=0', 'Include_tcltk=1',
      'Include_pip=1', 'Include_test=0', 'Include_doc=0', 'Include_dev=0', 'Include_debug=0', 'Include_symbols=0',
      'PrependPath=0', 'AssociateFiles=0', 'Shortcuts=0', 'CompileAll=0', '/log', $LogPath)
}

function Install-OwnPython {
    $cacheDir = if ($DownloadCache) { $DownloadCache } else { Join-Path $env:TEMP 'VoiceToolSetup' }
    $installer = Join-Path $cacheDir "python-$PythonVersion-amd64.exe"
    Say "  Python 3.13 isn't on this computer yet, so Setup will install it just for VoiceTool (about 30 MB, for your Windows account only). It shows up as ""Python 3.13"" in Windows' list of apps; Uninstall VoiceTool removes it again. Setup doesn't change your PATH or any other program."
    Get-VerifiedFile $PythonUrl $PythonSha256 $installer 'Python 3.13'
    $sig = Get-AuthenticodeSignature -LiteralPath $installer
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Python Software Foundation') {
        Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
        Fail "The Python download didn't carry the Python Software Foundation's signature, so Setup stopped. Please run Setup again; if it keeps happening, tell whoever sent you VoiceTool."
    }
    Say '  Installing Python (a minute or two)...'
    $code = Invoke-Native $installer (Get-PythonInstallerArgs (Join-Path $InstallDir 'python') (Join-Path $InstallDir 'python-install.log')) -Heartbeat
    if (-not $DownloadCache) { Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue }
    if ($code -ne 0 -and $code -ne 3010) { Fail "Python's installer didn't finish (code $code). Details are in $InstallDir\python-install.log. Please run Setup again." }
    $found = Find-Python313
    if (-not $found) { Fail "Python installed but Setup can't use it. Details are in $InstallDir\python-install.log." }
    return $found
}

# ---------------------------------------------------------------- step: virtual environment + packages

function Initialize-Venv([string]$BasePython) {
    $venvPy = Join-Path $InstallDir '.venv\Scripts\python.exe'
    $healthy = $false
    if (Test-Path -LiteralPath $venvPy) {
        $healthy = Test-PythonExe $venvPy
        if (-not $healthy) { Say '  The old private environment is damaged; rebuilding it.' }
    }
    if (-not $healthy) {
        Say '  Creating VoiceTool''s private Python environment...'
        $a = @('-m', 'venv', (Join-Path $InstallDir '.venv'))
        if (Test-Path -LiteralPath (Join-Path $InstallDir '.venv')) { $a += '--clear' }
        $code = Invoke-Native $BasePython $a
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $venvPy)) { Fail "Couldn't create VoiceTool's Python environment (code $code). Details are in $script:Log." }
    }
    return $venvPy
}

# pip's command line: every package must match the SHA-256 fingerprint in the requirements file (--require-hashes), only
# ready-built wheels are used (nothing is built or run from a download), nothing is kept in pip's cache, and the wheels
# folder that comes with VoiceTool supplies the one package PyPI has no wheel for.
function Get-PipInstallArgs([string]$Req) {
    $a = @('-m', 'pip', 'install', '--disable-pip-version-check', '--progress-bar', 'off', '--no-cache-dir', '--require-hashes', '--only-binary=:all:')
    $wheels = Join-Path $InstallDir 'wheels'
    if (Test-Path -LiteralPath $wheels) { $a += @('--find-links', $wheels) }
    return $a + @('-r', $Req)
}

function Install-Packages([string]$VenvPy) {
    $req = Join-Path $InstallDir 'requirements-friend.txt'
    Say '  Installing the parts VoiceTool is built from. This takes a few minutes; dots mean it is still working.'
    $code = -1
    for ($try = 1; $try -le 3 -and $code -ne 0; $try++) {
        if ($try -gt 1) { Say "  That didn't work. Trying again ($try of 3)..." 'Yellow'; Start-Sleep -Seconds (5 * $try) }
        $code = Invoke-Native $VenvPy (Get-PipInstallArgs $req) -Heartbeat -WorkDir $InstallDir
    }
    if ($code -ne 0) {
        Fail "Couldn't download VoiceTool's Python parts. Please check your internet connection (and that a firewall or antivirus isn't blocking pip) and run Setup again. Details are in $script:Log."
    }
    # Leave out the ASIO build of PortAudio (not needed; its license is a problem we avoid by not carrying it).
    Get-ChildItem -LiteralPath (Join-Path $InstallDir '.venv\Lib\site-packages\_sounddevice_data\portaudio-binaries') -Filter '*asio*.dll' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue; Write-Log "removed $($_.Name)" }
    $chk = Invoke-Native $VenvPy @('-c', 'import numpy, onnx_asr, openai, pystray, pynput, sounddevice, webview, win32api, docx, PIL') -WorkDir $InstallDir
    if ($chk -ne 0) { Fail "The Python parts installed but one of them doesn't load. Details are in $script:Log." }
}

# ---------------------------------------------------------------- step: models + settings

function Install-Models([string]$VenvPy) {
    $defaults = Join-Path $script:Src 'config.json'
    $cfgArgs = @((Join-Path $InstallDir 'fetch_models.py'), '--configure', '--root', $InstallDir)
    if (Test-Path -LiteralPath $defaults) { $cfgArgs += @('--defaults', $defaults) }
    [void](Invoke-Native $VenvPy $cfgArgs -WorkDir $InstallDir)      # settings first, so the app has them even if a download fails
    Say '  Checking and downloading the speech and tidy-up models (about 2.6 GB if this is a first install).'
    Say '  Please keep this running; you can watch the progress below.'
    $fetch = @((Join-Path $InstallDir 'fetch_models.py'), '--root', $InstallDir)
    if ($DownloadCache) { $fetch += @('--cache', $DownloadCache) }
    if (-not $NoLaunch) { $fetch += '--window' }
    # Started with Start-Process so it shares this console (the progress line can redraw in place) and the exit code comes back clean.
    $p = Start-Process -FilePath $VenvPy -ArgumentList (Format-Quoted $fetch) -NoNewWindow -Wait -PassThru -WorkingDirectory $InstallDir
    $null = $p.Handle
    $code = $p.ExitCode
    Write-Log "fetch_models exit $code"
    if ($code -eq 0) { return $true }
    Warn "The tidy-up model didn't finish downloading. VoiceTool will still type what you say as you said it. Open VoiceTool and click ""Finish setup"" to try again."
    return $false
}

function Read-Backend {
    try { (Get-Content -LiteralPath (Join-Path $InstallDir 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json).offline_backend } catch { $null }
}

# ---------------------------------------------------------------- step: shortcut, sign-in task, launch

function New-StartMenuShortcut([string]$PythonW) {
    $programs = [Environment]::GetFolderPath('Programs')
    $link = Join-Path $programs ($ShortcutName + '.lnk')
    $sh = New-Object -ComObject WScript.Shell
    $sc = $sh.CreateShortcut($link)
    $sc.TargetPath = $PythonW
    $sc.Arguments = '"' + (Join-Path $InstallDir 'voicetool.py') + '" --settings'
    $sc.WorkingDirectory = $InstallDir
    $ico = Join-Path $InstallDir 'voicetool.ico'
    if (Test-Path -LiteralPath $ico) { $sc.IconLocation = $ico }
    $sc.Description = 'VoiceTool: hold Left Alt to talk. Opens the VoiceTool window.'
    $sc.Save()
    Say "  Start menu shortcut: $link"
}

# "Uninstall VoiceTool" in the Start menu: runs Uninstall.cmd in the install folder. (Uninstall removes it again.)
function New-UninstallShortcut {
    $cmd = Join-Path $InstallDir 'Uninstall.cmd'
    if (-not (Test-Path -LiteralPath $cmd)) { Write-Log 'no Uninstall.cmd to point a shortcut at'; return }
    $link = Join-Path ([Environment]::GetFolderPath('Programs')) ("Uninstall $ShortcutName.lnk")
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
    $sc.TargetPath = $cmd
    $sc.WorkingDirectory = $InstallDir
    $ico = Join-Path $InstallDir 'voicetool.ico'
    if (Test-Path -LiteralPath $ico) { $sc.IconLocation = $ico }
    $sc.Description = 'Removes VoiceTool from this computer. It asks before it deletes anything.'
    $sc.Save()
    Say "  Start menu shortcut: $link"
}

function Remove-SetupTemp {
    if (-not $DownloadCache) {
        Remove-Item -LiteralPath (Join-Path $env:TEMP 'VoiceToolSetup') -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function New-LogonTask([string]$PythonW) {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    try {
        $action = New-ScheduledTaskAction -Execute $PythonW -Argument ('"' + (Join-Path $InstallDir 'voicetool.py') + '"') -WorkingDirectory $InstallDir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $me
        $trigger.Delay = 'PT20S'
        $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force `
            -Description 'Starts VoiceTool when you sign in to Windows.' | Out-Null
        Say "  Start-at-sign-in task: $TaskName (only for $me)"
    } catch {
        Write-Log "task error: $_"
        Warn "Couldn't set VoiceTool to start when you sign in (Windows said: $($_.Exception.Message)). You can start it from the Start menu instead."
    }
}

function Start-VoiceTool([string]$PythonW) {
    $app = Join-Path $InstallDir 'voicetool.py'
    Say '  Starting VoiceTool...'
    Start-Process -FilePath $PythonW -ArgumentList ('"' + $app + '"') -WorkingDirectory $InstallDir
    $prefix = ($InstallDir.TrimEnd('\') + '\').ToLowerInvariant()
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        $up = Get-CimInstance Win32_Process -Filter "Name='pythonw.exe'" -ErrorAction SilentlyContinue |
              Where-Object { $_.CommandLine -and $_.CommandLine.ToLowerInvariant().Contains($prefix + 'voicetool.py') }
        if ($up) { break }
    }
    Start-Sleep -Seconds 6     # it beeps twice when it is ready
    Start-Process -FilePath $PythonW -ArgumentList ('"' + $app + '" --settings') -WorkingDirectory $InstallDir
}

# ---------------------------------------------------------------- main

function Invoke-Setup {
    Write-Host ''
    Write-Host '  VoiceTool setup' -ForegroundColor Green
    Write-Host '  This installs VoiceTool for your Windows account only. It takes about 15 to 40 minutes in all, most of it downloading about 2.6 GB.'
    Write-Host '  Please leave this window open until it says VoiceTool is ready.'
    Write-Host ''
    try {
        $InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
        $safeDir = Get-SafeInstallDir $InstallDir $script:InstallDirGiven
        if ($safeDir -ne $InstallDir) {
            Say "  Your Windows user name has letters outside the English alphabet, which some of VoiceTool's parts can't handle in a folder name." 'Yellow'
            Say "  So VoiceTool will be installed in $safeDir instead. Nothing else is needed from you." 'Yellow'
            $InstallDir = $safeDir
        }
        Set-Variable -Name InstallDir -Value $InstallDir -Scope Script
        Test-Environment
        Test-NameConflicts
        $repair = Test-Path -LiteralPath (Join-Path $InstallDir '.venv\Scripts\python.exe')
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        $script:Log = Join-Path $InstallDir 'setup.log'
        Write-Log "setup starting: install=$InstallDir repair=$repair task=$TaskName shortcut=$ShortcutName skipTask=$SkipTask skipShortcut=$SkipShortcut noLaunch=$NoLaunch cache=$DownloadCache"
        if ($DownloadCache) { New-Item -ItemType Directory -Force -Path $DownloadCache | Out-Null }

        Step 1 'Checking this computer and copying VoiceTool'
        if ($repair) { Say '  VoiceTool is already installed here, so this will repair it. Your words, fixes, style and history stay as they are.' }
        Test-DiskSpace $repair
        Test-Internet
        Stop-InstalledProcesses
        Copy-App
        Write-InstallMarker
        Say "  VoiceTool's files are in $InstallDir"

        Step 2 'Checking the Microsoft Visual C++ Runtime'
        Confirm-VcRuntime

        Step 3 'Finding Python 3.13'
        $base = Resolve-BasePython

        Step 4 'Setting up VoiceTool''s private Python environment'
        $venvPy = Initialize-Venv $base

        Step 5 'Installing the parts VoiceTool is built from'
        Install-Packages $venvPy
        $pythonw = Join-Path $InstallDir '.venv\Scripts\pythonw.exe'

        Step 6 'Downloading the speech and tidy-up models'
        $modelsOk = Install-Models $venvPy

        Step 7 'Checking your graphics card and saving settings'
        $backend = Read-Backend
        if ($backend -eq 'vulkan') { Say '  Found a graphics card that can run the tidy-up model. It will use it.' }
        elseif ($backend -eq 'cpu') { Say '  No suitable graphics card found, so the tidy-up model will run on the processor. That works fine, just a little slower.' }
        else { Say '  The tidy-up model isn''t installed yet, so the graphics card check will happen when it is.' }
        Say '  Settings saved.'

        Step 8 'Adding the Start menu shortcuts and the start-at-sign-in task'
        if ($SkipShortcut) { Say '  Skipping the Start menu shortcuts.' } else { New-StartMenuShortcut $pythonw; New-UninstallShortcut }
        if ($SkipTask) { Say '  Skipping the sign-in task.' } else { New-LogonTask $pythonw }
        if ($NoLaunch) { Say '  Not starting VoiceTool (asked not to).' } else { Start-VoiceTool $pythonw }

        Write-Host ''
        Say '  VoiceTool is ready.' 'Green'
        Say '  Hold the Left Alt key, talk, and let go: your words are typed where the cursor is.' 'Green'
        Say '  Hold the Left Ctrl key and say what you want done, such as "make this shorter" (Ctrl+C, Ctrl+V and the rest work as usual).' 'Green'
        if ($SkipShortcut) {
            Say "  VoiceTool is in $InstallDir (in File Explorer, click the address bar and type that folder name to open it). To remove it, double-click Uninstall there." 'Green'
        } else {
            Say "  To open VoiceTool again, use ""$ShortcutName"" in the Start menu. To remove it, use ""Uninstall $ShortcutName"" in the Start menu." 'Green'
        }
        if ($script:Warnings.Count -gt 0) {
            Write-Host ''
            Say '  Things to know:' 'Yellow'
            foreach ($w in $script:Warnings) { Say "   - $w" 'Yellow' }
        }
        Write-Log 'setup finished'
        Remove-SetupTemp
        exit 0
    } catch {
        $msg = $_.Exception.Message
        Write-Log "FAILED: $msg`n$($_.ScriptStackTrace)"
        Write-Host ''
        Write-Host "  Setup couldn't finish: $msg" -ForegroundColor Red
        if ($script:Log) { Write-Host "  Details were saved in: $script:Log" -ForegroundColor Red }
        Write-Host '  You can run Setup again at any time; it picks up where it left off.' -ForegroundColor Red
        Remove-SetupTemp
        exit 1
    }
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-Setup }
