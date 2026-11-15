# Installs Chirpy from this folder (no administrator, no internet).
#   Setup.cmd                     install to %LOCALAPPDATA%\Programs\Chirpy, add a Start menu entry, start it
#   Setup.cmd -InstallDir <path>  install somewhere else
#   -StartAtSignIn                also start it each time you sign in (it can be turned on or off in Settings later)
#   -NoStartAtSignIn              do NOT start it when you sign in, even when the earlier VoiceTool Light did, and take away
#                                 this folder's own sign-in entry if an earlier install made one (works through Setup.cmd)
#   -NoShortcut                   no Start menu entry
#   -NoLaunch                     don't start it at the end
#   -From <folder>                carry your words, fixes, history and settings over from that earlier VoiceTool Light folder
#   -CarryOverOnly                (Chirpy-Setup.exe uses this; it copies the program itself) only the checks, stopping a copy that is
#                                 running, and the carry-over from the earlier install. It marks the folder as Chirpy's first, so an
#                                 install that is then canceled or fails leaves a folder Setup accepts again. -ResultFile <path>
#                                 lists what was carried over, one line each
#   -RetireOnly                   (Chirpy-Setup.exe, after its install worked) takes the earlier VoiceTool Light's Start menu entry and
#                                 sign-in entry away; -ResultFile <path> says whether it started at sign-in ("signin=1")
#   -SaidFile <path>              (Chirpy-Setup.exe) also writes everything Setup says (a refusal, an error) to that file, so the
#                                 installer needs no command shell to collect it
#   -FindChirpy                   (Chirpy-Setup.exe) writes the folder of a Chirpy that is already installed (its Start menu entry or
#                                 sign-in entry names it, and it has Setup's marker file) to -ResultFile, or nothing
# Installing again over an earlier copy keeps your words, fixes, history and settings. Chirpy was called VoiceTool Light before:
# if Setup finds that earlier install (its Start menu entry, its sign-in entry, or its usual folder), it carries those over too.
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Chirpy'),
    [string]$From = '',
    [switch]$StartAtSignIn,
    [switch]$NoStartAtSignIn,
    [switch]$NoShortcut,
    [switch]$NoLaunch,
    [switch]$CarryOverOnly,
    [switch]$RetireOnly,
    [switch]$FindChirpy,
    [string]$ResultFile = '',
    [string]$SaidFile = ''
)
$ErrorActionPreference = 'Stop'
# (any error nobody planned for is written to -SaidFile too, then Setup stops)
trap { if ($SaidFile) { try { Add-Content -LiteralPath $SaidFile -Encoding UTF8 -Value "Setup stopped: $($_.Exception.Message)" } catch { } }; Write-Host "Setup stopped: $($_.Exception.Message)"; exit 1 }
$here = $PSScriptRoot
$marker = '.voicetool-light-from-daily'
$keep = @('config.json', 'words.txt', 'corrections.json', 'corrections_pending.json', 'history.jsonl')

# ---- carry-over from the earlier name (tested on their own in test_light_edition.py) ----
$oldName = 'VoiceTool Light'
# The install folder named in a command line such as: "C:\...\pythonw.exe" -s -E "C:\...\voicetool.py"
function Get-FolderOfScript([string]$command) {
    if ($command -match '(?i)"?([a-z]:\\[^"]*?)\\voicetool\.py"?') { return $Matches[1] }
    return $null
}
# The earlier install folder: the one in -From, else the one its Start menu entry or sign-in entry points to, else its usual
# folder. Only a folder that carries Setup's own marker file counts, so Setup never reads some other folder.
function Find-OldInstall([string]$startMenuDir, [string]$runCommand, [string]$defaultDir, [string]$explicit) {
    $candidates = New-Object System.Collections.ArrayList
    if ($explicit) { [void]$candidates.Add($explicit) }
    $lnk = Join-Path $startMenuDir "$oldName.lnk"
    if (Test-Path -LiteralPath $lnk) {
        try { $dir = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).WorkingDirectory; if ($dir) { [void]$candidates.Add($dir) } } catch { }
    }
    $fromRun = Get-FolderOfScript $runCommand
    if ($fromRun) { [void]$candidates.Add($fromRun) }
    [void]$candidates.Add($defaultDir)
    foreach ($dir in $candidates) {
        $full = $null
        try { $full = [IO.Path]::GetFullPath($dir).TrimEnd('\') } catch { }
        if ($full -and (Test-Path -LiteralPath (Join-Path $full $marker))) { return $full }
    }
    return $null
}
# What each of the person's own files is called in Setup's messages.
$friendly = @{ 'config.json' = 'settings'; 'words.txt' = 'words'; 'corrections.json' = 'fixes'
               'corrections_pending.json' = 'pending fixes'; 'history.jsonl' = 'history' }
function Get-FriendlyList([string[]]$names) { (@($names | ForEach-Object { "$($friendly[$_]) ($_)" })) -join ', ' }
# Copies the person's own files (words, fixes, history, settings) that the new folder doesn't have yet. Nothing is overwritten
# and nothing in the earlier folder is changed or deleted. A settings file that can't be read is not copied (Chirpy would not
# start from it); the first line of a copied word list that still names the earlier program is renamed in the copy only.
# Returns what was Copied, what was Skipped (the new folder already had its own) and what was Unreadable (and so stayed behind).
function Copy-OldData([string]$from, [string]$to, [string[]]$names) {
    $copied = @(); $skipped = @(); $unreadable = @()
    foreach ($name in $names) {
        $src = Join-Path $from $name
        $dst = Join-Path $to $name
        if (-not (Test-Path -LiteralPath $src)) { continue }
        if (Test-Path -LiteralPath $dst) { $skipped += $name; continue }
        if ($name -eq 'config.json') {
            $good = $false
            try { $good = ((Get-Content -LiteralPath $src -Raw -Encoding UTF8 | ConvertFrom-Json) -is [System.Management.Automation.PSCustomObject]) } catch { }
            if (-not $good) { $unreadable += $name; continue }
        }
        # (never over a file that is already there, even one that appeared a moment ago: .NET's copy with overwrite off)
        try { [IO.File]::Copy($src, $dst, $false) } catch [IO.IOException] { if (Test-Path -LiteralPath $dst) { $skipped += $name; continue }; throw }
        $copied += $name
        if ($name -eq 'words.txt') { Rename-WordListHeader $dst }
    }
    return [pscustomobject]@{ Copied = $copied; Skipped = $skipped; Unreadable = $unreadable }
}
function Rename-WordListHeader([string]$path) {
    try {
        $bytes = [IO.File]::ReadAllBytes($path)
        $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $text = [Text.Encoding]::UTF8.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
        $oldHeader = "# One word or name per line. $oldName spells these exactly like this."
        if ($text.StartsWith($oldHeader)) {
            $text = '# One word or name per line. Chirpy spells these exactly like this.' + $text.Substring($oldHeader.Length)
            [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $bom))
        }
    } catch { }   # (the header is cosmetic: leave the copy as it is)
}
# What Setup says about the carry-over, as lines. The earlier folder is only suggested for removal when nothing stayed in it.
function Get-CarryOverLines($result, [string]$old) {
    $lines = @()
    if (@($result.Copied).Count) { $lines += "Carried over your $(Get-FriendlyList $result.Copied) from $old." }
    if (@($result.Skipped).Count) { $lines += "Not carried over, because Chirpy already had its own: $(Get-FriendlyList $result.Skipped). They are still in $old." }
    if (@($result.Unreadable).Count) { $lines += "Your earlier settings file (config.json) couldn't be read, so Chirpy starts with its usual settings. The file is still in $old." }
    if (@($result.Skipped).Count -or @($result.Unreadable).Count) {
        $lines += "(That earlier $oldName folder was left as it is, with the files named above still in it.)"
    } else {
        $lines += "(That earlier $oldName folder was left as it is. When you like, run Uninstall $oldName.cmd in it.)"
    }
    return $lines
}
# Takes the earlier name's Start menu entry and sign-in entry away (only when they point at the earlier folder), so the new
# name's entries replace them and nothing opens the earlier copy. Returns 'startmenu' and/or 'signin' for what it removed.
function Remove-OldEntries([string]$startMenuDir, [string]$runKey, [string]$old) {
    $removed = @()
    $lnk = Join-Path $startMenuDir "$oldName.lnk"
    if (Test-Path -LiteralPath $lnk) {
        $dir = ''
        try { $dir = [string](New-Object -ComObject WScript.Shell).CreateShortcut($lnk).WorkingDirectory } catch { }
        if ($dir.TrimEnd('\') -ieq $old) { Remove-Item -LiteralPath $lnk -Force; $removed += 'startmenu' }
    }
    $value = (Get-ItemProperty -Path $runKey -Name $oldName -ErrorAction SilentlyContinue).$oldName
    if ($value -and $value.IndexOf($old + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Remove-ItemProperty -Path $runKey -Name $oldName
        $removed += 'signin'
    }
    return $removed
}
# What to do with Chirpy's sign-in entry: 'add', 'remove' (only an entry that points into this folder) or 'keep'. An explicit
# -NoStartAtSignIn wins over everything; -StartAtSignIn adds; otherwise an earlier VoiceTool Light that started at sign-in
# is followed (its entry was just taken away, see above).
function Get-SignInAction([bool]$start, [bool]$noStart, [bool]$inherited, [string]$existing, [string]$dir) {
    if ($noStart) {
        if ($existing -and $existing.IndexOf($dir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0) { return 'remove' }
        return 'keep'
    }
    if ($start -or $inherited) { return 'add' }
    return 'keep'
}
# A Chirpy that is already installed somewhere: the folder its Start menu entry or its sign-in entry names, if that folder has
# Setup's marker file (so Chirpy-Setup.exe updates it in place instead of making a second one).
function Find-ExistingChirpy([string]$startMenuDir, [string]$runCommand) {
    $candidates = New-Object System.Collections.ArrayList
    $lnk = Join-Path $startMenuDir 'Chirpy.lnk'
    if (Test-Path -LiteralPath $lnk) {
        try { $dir = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).WorkingDirectory; if ($dir) { [void]$candidates.Add($dir) } } catch { }
    }
    $fromRun = Get-FolderOfScript $runCommand
    if ($fromRun) { [void]$candidates.Add($fromRun) }
    foreach ($dir in $candidates) {
        $full = $null
        try { $full = [IO.Path]::GetFullPath($dir).TrimEnd('\') } catch { }
        if ($full -and (Test-Path -LiteralPath (Join-Path $full $marker))) { return $full }
    }
    return $null
}
# ---- end of carry-over functions ----

if ($FindChirpy) {
    $found = Find-ExistingChirpy (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') ((Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Chirpy' -ErrorAction SilentlyContinue).Chirpy)
    if ($ResultFile) { Set-Content -LiteralPath $ResultFile -Encoding UTF8 -Value @(if ($found) { $found } else { '' }) }
    exit 0
}

function Say([string]$text) { Write-Host $text; if ($SaidFile) { Add-Content -LiteralPath $SaidFile -Encoding UTF8 -Value $text } }
# A console breaks a long line wherever the window ends, even in the middle of a word: so lines are cut at spaces, 70 wide.
function Wrap([string]$text, [int]$width = 70) {
    foreach ($paragraph in ($text -split "`n")) {
        $line = ''
        foreach ($word in ($paragraph -split ' ')) {
            if ($line -and ($line.Length + 1 + $word.Length) -gt $width) { $line; $line = $word }
            elseif ($line) { $line = "$line $word" }
            else { $line = $word }
        }
        $line
    }
}
function Refuse([string]$text) { Write-Host ''; Wrap $text | ForEach-Object { Write-Host $_; if ($SaidFile) { Add-Content -LiteralPath $SaidFile -Encoding UTF8 -Value $_ } }; Write-Host ''; exit 1 }   # (a plain message, not a PowerShell error)

# Both can't listen for keys at once: nothing is installed while the everyday VoiceTool runs.
$daily = $null
if ([System.Threading.Mutex]::TryOpenExisting('VoiceTool-single-instance', [System.Security.AccessControl.MutexRights]::Synchronize, [ref]$daily)) {
    $daily.Dispose()
    Refuse "Your everyday VoiceTool is running.`nQuit it first (right-click its microphone icon by the clock,`nthen choose Quit VoiceTool), then run Setup again.`nNothing was installed."
}

# (checked before anything is stopped or copied; the license and the notices are installed beside Chirpy, so they must be here)
foreach ($need in @('app\voicetool.py', 'python\pythonw.exe', 'models\parakeet-tdt-0.6b-v2\encoder-model.int8.onnx', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'README.txt')) {
    if (-not ($CarryOverOnly -or $RetireOnly) -and -not (Test-Path -LiteralPath (Join-Path $here $need) -PathType Leaf)) {
        Refuse "This folder is incomplete ($need is missing). Extract the whole package again."
    }
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
if (-not ($CarryOverOnly -or $RetireOnly) -and ($InstallDir.StartsWith($here.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or $InstallDir -ieq $here.TrimEnd('\'))) {
    Refuse 'Choose an install folder outside the package folder.'
}
if ((Test-Path -LiteralPath $InstallDir) -and (Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 1) -and
        -not (Test-Path -LiteralPath (Join-Path $InstallDir $marker))) {
    Refuse "$InstallDir already has other files in it. Choose an empty folder or a folder Chirpy was installed in."
}

# The earlier install (this program's old name), if there is one.
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$oldRun = (Get-ItemProperty -Path $runKey -Name $oldName -ErrorAction SilentlyContinue).$oldName
$old = Find-OldInstall $programs $oldRun (Join-Path $env:LOCALAPPDATA "Programs\$oldName") $From
if ($From -and -not $old) { Refuse "$From is not a folder VoiceTool Light was installed in (its .voicetool-light-from-daily file is missing). Nothing was installed." }

# The earlier name's own files in this folder, and its Start menu and sign-in entries, give way to the new name's.
# Returns 'startmenu' and/or 'signin' for the entries it took away.
function Retire-Old {
    foreach ($stale in "Start $oldName.cmd", "Stop $oldName.cmd", "Uninstall $oldName.cmd") {
        Remove-Item -LiteralPath (Join-Path $InstallDir $stale) -Force -ErrorAction SilentlyContinue
    }
    $retired = @()
    foreach ($dir in @(@($old, $InstallDir) | Where-Object { $_ } | Select-Object -Unique)) { $retired += @(Remove-OldEntries $programs $runKey $dir) }
    return $retired
}

if ($RetireOnly) {   # (Chirpy-Setup.exe, after its install worked)
    $retired = @(Retire-Old)
    if ($ResultFile) { Set-Content -LiteralPath $ResultFile -Encoding UTF8 -Value "signin=$(if ($retired -contains 'signin') { 1 } else { 0 })" }
    exit 0
}

# A copy already running from this folder (or from the earlier folder) is stopped first (only those copies).
$stopDirs = @($InstallDir) + @(if ($old) { $old })
$running = Get-CimInstance Win32_Process | Where-Object {
    $exe = $_.ExecutablePath
    $_.Name -in 'python.exe', 'pythonw.exe' -and $exe -and
    ($stopDirs | Where-Object { $exe.StartsWith($_ + '\python\', [StringComparison]::OrdinalIgnoreCase) }) }
foreach ($p in $running) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
if ($running) { Start-Sleep -Seconds 1; Say 'Stopped the copy that was running.' }

Say "Installing Chirpy to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# The folder is Chirpy's from the moment it exists: if the install is canceled or fails at any later point (even in the
# carry-over just below), a reinstall into it is not refused.
if ($CarryOverOnly -and -not (Test-Path -LiteralPath (Join-Path $InstallDir $marker))) { Set-Content -LiteralPath (Join-Path $InstallDir $marker) -Encoding ASCII -Value 'Chirpy (from daily) setup under way' }
$carryLines = @()
if ($old -and $old -ine $InstallDir) {
    $carryLines = @(Get-CarryOverLines (Copy-OldData $old $InstallDir $keep) $old)
    foreach ($line in $carryLines) { Say $line }
}
if ($CarryOverOnly) {   # (Chirpy-Setup.exe copies the program itself and makes its own Start menu entry)
    # (the folder was marked as Chirpy's above, before the carry-over.) The earlier VoiceTool Light's Start menu and sign-in
    # entries stay until the install has worked (-RetireOnly).
    if ($ResultFile) { Set-Content -LiteralPath $ResultFile -Encoding UTF8 -Value (@('signin=0') + $carryLines) }
    exit 0
}
foreach ($part in 'python', 'models') {
    $null = robocopy (Join-Path $here $part) (Join-Path $InstallDir $part) /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1
    if ($LASTEXITCODE -ge 8) { Refuse "Copying $part failed (robocopy $LASTEXITCODE). Nothing else was changed; run Setup again." }
}
$app = Join-Path $here 'app'
Get-ChildItem -LiteralPath $app -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($app.Length + 1)
    $dest = Join-Path $InstallDir $rel
    if ($keep -contains $rel -and (Test-Path -LiteralPath $dest)) { return }   # your own settings stay as they are
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    if ($keep -contains $rel) {   # (a first copy only: never over your own file, even one that appeared a moment ago)
        try { [IO.File]::Copy($_.FullName, $dest, $false) } catch [IO.IOException] { if (-not (Test-Path -LiteralPath $dest)) { throw } }
        return
    }
    Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
}
Copy-Item -LiteralPath (Join-Path $here 'uninstall.ps1') -Destination (Join-Path $InstallDir 'uninstall.ps1') -Force
foreach ($doc in 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'README.txt') {   # Chirpy's MIT license, the third-party notices and this guide stay with the install
    Copy-Item -LiteralPath (Join-Path $here $doc) -Destination (Join-Path $InstallDir $doc) -Force
}
Set-Content -LiteralPath (Join-Path $InstallDir 'Uninstall Chirpy.cmd') -Encoding ASCII -Value @(
    '@echo off',
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"',
    'pause')
Set-Content -LiteralPath (Join-Path $InstallDir $marker) -Encoding ASCII -Value (Get-Content -LiteralPath (Join-Path $here 'VERSION.txt'))
$retired = @(Retire-Old)
$signIn = Get-SignInAction $StartAtSignIn.IsPresent $NoStartAtSignIn.IsPresent ($retired -contains 'signin') (Get-ItemProperty -Path $runKey -Name 'Chirpy' -ErrorAction SilentlyContinue).'Chirpy' $InstallDir

$pythonw = Join-Path $InstallDir 'python\pythonw.exe'
$script = Join-Path $InstallDir 'voicetool.py'
if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $programs 'Chirpy.lnk'))
    $link.TargetPath = $pythonw
    $link.Arguments = "-s -E `"$script`" --settings"
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation = (Join-Path $InstallDir 'voicetool.ico')
    $link.Description = 'Chirpy: talk anywhere and it types. Opens the Chirpy window.'
    $link.Save()
    Say 'Added Chirpy to the Start menu.'
}
if ($signIn -eq 'add') {
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'Chirpy' -Value "`"$pythonw`" -s -E `"$script`""
    Say 'Chirpy will start when you sign in.'
}
elseif ($signIn -eq 'remove') {
    Remove-ItemProperty -Path $runKey -Name 'Chirpy'
    Say 'Chirpy will not start when you sign in.'
}
Say 'Done.'
if (-not $NoLaunch) {
    Start-Process -FilePath $pythonw -ArgumentList '-s', '-E', "`"$script`"", '--settings' -WorkingDirectory $InstallDir
    Say 'Starting Chirpy (it beeps twice when it is ready, in about 10 seconds).'
}
