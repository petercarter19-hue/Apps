<#
  VoiceTool uninstall (run by Uninstall.cmd). It removes VoiceTool from this computer and nothing else:
    - the programs it runs (only the ones that live inside the install folder),
    - the start-at-sign-in task and the Start menu shortcuts ("VoiceTool" and "Uninstall VoiceTool"), by their names,
    - the install folder (VoiceTool's files, its private Python, its models).
  It only works on a folder that Setup marked as the real install (a file named .voicetool-installed): the downloaded
  setup folder is refused. It asks first whether to keep a copy of your words, fixes, writing style, learned fixes,
  voice shortcuts and history. If you say yes, VoiceTool is stopped first (so nothing is still being written) and the copy
  goes to Documents\VoiceTool backup before anything is deleted. Your notes in Documents (Voice notes, Meeting notes) are
  never touched. Microsoft's Visual C++ Runtime (which Setup installs if the computer lacks it) is never removed: other
  programs share it.

  Parameters (all optional):
    -InstallDir     the VoiceTool folder (Uninstall.cmd passes the folder it is in)
    -TaskName       name of the sign-in task (default: VoiceTool)
    -ShortcutName   name of the Start menu shortcut (default: VoiceTool)
    -KeepData       keep a copy of your words, fixes, style and history without asking
    -DeleteData     delete them too, without asking
    -Force          don't ask "are you sure?"
    -NoPause        don't wait for Enter at the end
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'VoiceTool'),
    [string]$TaskName = 'VoiceTool',
    [string]$ShortcutName = 'VoiceTool',
    [switch]$KeepData,
    [switch]$DeleteData,
    [switch]$Force,
    [switch]$NoPause,
    [string]$BackupRoot = ''
)

$ErrorActionPreference = 'Stop'

function Say([string]$Text, [string]$Color = 'White') { Write-Host $Text -ForegroundColor $Color }

function Ask-YesNo([string]$Question, [bool]$Default) {
    $hint = if ($Default) { 'Y/n' } else { 'y/N' }
    while ($true) {
        $a = (Read-Host "$Question [$hint]").Trim().ToLowerInvariant()
        if ($a -eq '') { return $Default }
        if ($a -in @('y', 'yes')) { return $true }
        if ($a -in @('n', 'no')) { return $false }
    }
}

function Stop-InstalledProcesses([string]$Dir) {
    # Only VoiceTool's own programs, and only ones that run from inside the install folder. (A virtual-environment
    # Python starts the real interpreter as a child, so the folder can be in the command line instead of the program path.)
    $prefix = ($Dir.TrimEnd('\') + '\').ToLowerInvariant()
    if ($prefix.Length -lt 8) { return $false }     # never match everything
    $names = @('python.exe', 'pythonw.exe', 'llama-server.exe', 'claude.exe')   # (claude.exe: VoiceTool's own Claude helper, which runs in claude_workdir)
    $found = $false
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $names -contains $_.Name.ToLowerInvariant() -and (
            ($_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant().StartsWith($prefix)) -or
            ($_.CommandLine -and $_.CommandLine.ToLowerInvariant().Contains($prefix)))
    } | ForEach-Object {
        $found = $true
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($found) { Start-Sleep -Seconds 2 }
    return $found
}

# The file Setup writes into the install folder. The downloaded setup folder doesn't have it.
$MarkerName = '.voicetool-installed'
$NotInstalledText = 'This is the downloaded setup folder, not the installed VoiceTool. Use Uninstall VoiceTool from the Start menu.'

function Test-IsInstalledMarked([string]$Dir) { Test-Path -LiteralPath (Join-Path $Dir $MarkerName) -PathType Leaf }

function Test-IsVoiceToolFolder([string]$Dir) {
    if (-not (Test-Path -LiteralPath (Join-Path $Dir 'voicetool.py'))) { return $false }
    $full = [IO.Path]::GetFullPath($Dir).TrimEnd('\')
    if ([IO.Path]::GetPathRoot($full).TrimEnd('\') -ieq $full) { return $false }  # a drive (C:\VoiceTool, a folder in the drive's top level, is fine)
    $protected = @($env:USERPROFILE, $env:LOCALAPPDATA, $env:APPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:windir,
                   [Environment]::GetFolderPath('MyDocuments'), [Environment]::GetFolderPath('Desktop'),
                   (Join-Path $env:USERPROFILE 'Downloads')) | Where-Object { $_ }
    foreach ($p in $protected) { if ($full -ieq $p.TrimEnd('\')) { return $false } }
    return $true
}

# What goes into the backup if you choose to keep your data (only what exists).
$BackupItems = @('words.txt', 'corrections.json', 'corrections_pending.json', 'style.md', 'styles.json', 'shortcuts.json',
                'history.jsonl', 'config.json', 'journal', 'recordings')

function Save-Backup([string]$Dir) {
    $docs = if ($BackupRoot) { $BackupRoot } else { [Environment]::GetFolderPath('MyDocuments') }
    $dest = Join-Path $docs 'VoiceTool backup'
    if (Test-Path -LiteralPath $dest) { $dest = Join-Path $docs ('VoiceTool backup ' + (Get-Date -Format 'yyyy-MM-dd HHmmss')) }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $count = 0
    foreach ($name in $BackupItems) {
        $from = Join-Path $Dir $name
        if (-not (Test-Path -LiteralPath $from)) { continue }
        Copy-Item -LiteralPath $from -Destination (Join-Path $dest $name) -Recurse -Force
        # Check the copy before anything gets deleted.
        $a = Get-ChildItem -LiteralPath $from -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum
        if ((Test-Path -LiteralPath $from -PathType Leaf)) { $a = [pscustomobject]@{ Count = 1; Sum = (Get-Item -LiteralPath $from).Length } }
        $to = Join-Path $dest $name
        $b = if (Test-Path -LiteralPath $to -PathType Leaf) { [pscustomobject]@{ Count = 1; Sum = (Get-Item -LiteralPath $to).Length } }
             else { Get-ChildItem -LiteralPath $to -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum }
        if ($a.Count -ne $b.Count -or $a.Sum -ne $b.Sum) { throw "The backup of $name didn't copy completely." }
        $count++
    }
    return @{ Path = $dest; Count = $count }
}

# Only if Setup installed its own Python for VoiceTool: ask Windows to uninstall it properly (so it doesn't linger in the
# Apps list). Used only when Windows's record says that Python lives in this install folder.
function Remove-OwnPython([string]$Dir) {
    $own = Join-Path $Dir 'python'
    $exe = Join-Path $own 'python.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return }
    try {
        $reg = 'HKCU:\Software\Python\PythonCore\3.13\InstallPath'
        $registered = (Get-ItemProperty -LiteralPath $reg -ErrorAction Stop).'(default)'
        if (-not $registered -or ($registered.TrimEnd('\') -ine $own.TrimEnd('\'))) { return }   # that registration belongs to some other Python
        $ver = (& $exe -c 'import platform;print(platform.python_version())' 2>$null) -join ''
        $keys = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction Stop |
                Where-Object { $ver -and ((Get-ItemProperty -LiteralPath $_.PSPath).DisplayName -like "Python $ver*") }
        foreach ($k in $keys) {
            $p = Get-ItemProperty -LiteralPath $k.PSPath
            $cmd = if ($p.QuietUninstallString) { $p.QuietUninstallString } else { $null }
            if ($cmd -and $cmd -match '^"([^"]+)"\s*(.*)$' -and (Test-Path -LiteralPath $Matches[1])) {
                Say '  Removing the Python that Setup installed for VoiceTool...'
                Start-Process -FilePath $Matches[1] -ArgumentList $Matches[2] -Wait -WindowStyle Hidden | Out-Null
            }
        }
    } catch { }   # not important: the folder is deleted next either way
}

function Remove-Folder([string]$Dir) {
    for ($i = 1; $i -le 4; $i++) {
        try { Remove-Item -LiteralPath $Dir -Recurse -Force -ErrorAction Stop; return @() }
        catch { Start-Sleep -Seconds 2 }
    }
    # Whatever is left could not be removed (usually something has it open).
    return @(Get-ChildItem -LiteralPath $Dir -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 5 | ForEach-Object { $_.FullName })
}

function Invoke-Uninstall {
    Write-Host ''
    Say '  VoiceTool uninstall' 'Green'
    Write-Host ''
    try {
        $dir = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')
        Set-Location -LiteralPath $env:TEMP          # so this window isn't "inside" the folder being deleted
        if (-not (Test-IsVoiceToolFolder $dir)) {
            Say "  $dir doesn't look like a VoiceTool folder, so nothing was removed." 'Red'
            return 1
        }
        if (-not (Test-IsInstalledMarked $dir)) {
            Say "  $NotInstalledText" 'Red'
            Say '  Nothing was removed.' 'Red'
            return 1
        }
        Say "  This will remove VoiceTool from this computer:"
        Say "    - the folder $dir (the program, its models and its private Python)"
        Say "    - the Start menu shortcuts ""$ShortcutName"" and ""Uninstall $ShortcutName"", and the start-at-sign-in task ""$TaskName"""
        Say '  Your notes in Documents (Voice notes, Meeting notes) are not touched.'
        Say '  Microsoft''s Visual C++ Runtime, if Setup installed it, stays: other programs use it too.'
        Write-Host ''
        if (-not $Force) { if (-not (Ask-YesNo '  Remove VoiceTool now?' $true)) { Say '  Nothing was changed.'; return 0 } }

        $keep = $false
        $script:Stopped = $false
        if ($KeepData) { $keep = $true }
        elseif ($DeleteData) { $keep = $false }
        else {
            Write-Host ''
            Say '  Your words, fixes, writing style and history can be saved before VoiceTool is removed.'
            Say '  They would be copied to the folder "VoiceTool backup" inside your Documents.'
            $keep = Ask-YesNo '  Keep a copy of them?' $true
        }
        # Stop first: a running VoiceTool could still be writing to the very files that are about to be copied.
        Say '  Stopping VoiceTool...'
        [void](Stop-InstalledProcesses $dir)
        $script:Stopped = $true
        if ($keep) {
            $b = Save-Backup $dir
            Say ("  Saved {0} item(s) to {1}" -f $b.Count, $b.Path) 'Green'
        }

        # The task and the shortcut, by name, and only when they point into this folder.
        $prefix = $dir.TrimEnd('\') + '\'
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($task) {
            $text = ($task.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments) $($_.WorkingDirectory)" }) -join ' '
            if ($text.ToLowerInvariant().Contains($prefix.ToLowerInvariant())) {
                Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
                Say "  Removed the start-at-sign-in task ""$TaskName""."
            } else { Say "  The task ""$TaskName"" belongs to a different folder, so it was left alone." 'Yellow' }
        }
        foreach ($linkName in @($ShortcutName, "Uninstall $ShortcutName")) {
            $link = Join-Path ([Environment]::GetFolderPath('Programs')) ($linkName + '.lnk')
            if (Test-Path -LiteralPath $link) {
                $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($link)
                if (("$($sc.TargetPath) $($sc.Arguments) $($sc.WorkingDirectory)").ToLowerInvariant().Contains($prefix.ToLowerInvariant())) {
                    Remove-Item -LiteralPath $link -Force
                    Say "  Removed the Start menu shortcut ""$linkName""."
                } else { Say "  The Start menu shortcut ""$linkName"" belongs to a different folder, so it was left alone." 'Yellow' }
            }
        }

        Remove-OwnPython $dir
        Say '  Deleting the VoiceTool folder...'
        $left = Remove-Folder $dir
        Write-Host ''
        if ($left.Count -gt 0) {
            Say '  VoiceTool is removed, except for a few files that Windows has open:' 'Yellow'
            $left | ForEach-Object { Say "    $_" 'Yellow' }
            Say '  Restart the computer and delete the folder by hand to finish.' 'Yellow'
        } else {
            Say '  VoiceTool has been removed.' 'Green'
        }
        if ($keep) { Say "  Your words, fixes, style and history are saved in: $($b.Path)" 'Green' }
        return 0
    } catch {
        Write-Host ''
        Say "  Uninstall couldn't finish: $($_.Exception.Message)" 'Red'
        Say '  Nothing more was deleted. You can run Uninstall again.' 'Red'
        if ($script:Stopped) { Say '  (VoiceTool was stopped. To use it again, open it from the Start menu.)' 'Red' }
        return 1
    }
}

$code = Invoke-Uninstall
if (-not $NoPause -and [Environment]::UserInteractive) { Write-Host ''; [void](Read-Host '  Press Enter to close this window') }
# Uninstall.cmd runs a temporary copy of this script from the Temp folder; tidy that copy away.
if ($PSCommandPath -and (Split-Path -Leaf $PSCommandPath) -eq 'voicetool-uninstall.ps1' -and (Split-Path -Parent $PSCommandPath) -ieq $env:TEMP.TrimEnd('\')) {
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
exit $code
