# Shared helpers for Glance's test scripts. Each script loads this with:   . (Join-Path $PSScriptRoot 'TestLib.ps1')
#
# What makes the tests safe to run on your own PC:
#   * Test copy. Every Glance the tests start gets its own made-up name (GLANCE_TEST_NS) and its own settings folder
#     (GLANCE_TEST_DIR), so it has its own single-copy lock, quit signal, Start with Windows entry, Start menu shortcut and
#     window titles. It can't see, replace or quit the Glance you actually use, and it never reads or writes your real settings.
#   * Only its own processes. Anything the tests start (a test copy, the memory-hog test program) is written down with its
#     process number AND start time, and only exactly those are ever stopped. Nothing is stopped by name.
#   * Only its own windows. Clicks and tray events are posted straight to the test copy's own window (and only after
#     checking the window belongs to the test copy's process). No mouse is moved, no key is pressed, and the screen is never
#     captured: the pictures come from the app's own --preview renders.
#   * Your real state is checked afterwards (startup entry, Start menu shortcut, settings, your own running Glance) and put
#     back if a bug ever changed it. A copy of Glance that was already there is left alone.
# tools\safety-lint.ps1 checks that no script outside tools\disposable-vm-only can do any of the things above.

if (-not ('GlanceTestWin' -as [type])) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class GlanceTestWin {
  public struct POINT { public int X, Y; }
  // Per-Monitor-V2 (-4): must match the card's own awareness, or Windows rescales posted click coordinates on a scaled monitor.
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls, string title);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr h, uint f);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int a, bool i, int pid);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
  public static long Created(int pid) {
    IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return 0;
    long c, e, k, u; GetProcessTimes(h, out c, out e, out k, out u); CloseHandle(h); return c;
  }
}
"@
}
[void][GlanceTestWin]::SetProcessDpiAwarenessContext([IntPtr](-4))

$script:RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$script:Results = New-Object System.Collections.ArrayList

function Check([string]$what, [bool]$ok) {
    [void]$script:Results.Add([pscustomobject]@{ Ok = $ok; What = $what })
    Write-Output "$(if ($ok) { 'PASS' } else { 'FAIL' })  $what"
}

function Write-TestSummary {
    Write-Output ''
    Write-Output '==== results ===='
    foreach ($r in $script:Results) { Write-Output "$(if ($r.Ok) { 'PASS' } else { 'FAIL' })  $($r.What)" }
    $failed = @($script:Results | Where-Object { -not $_.Ok }).Count
    Write-Output "$($script:Results.Count - $failed) passed, $failed failed."
    $script:Failed = $failed
}

# ---- a test session: made-up names, own settings folder, own list of processes -------------------------------

function New-GlanceTest {
    param([Parameter(Mandatory)][string]$Exe, [string]$ScratchRoot)
    $Exe = (Resolve-Path $Exe).Path
    if (-not $ScratchRoot) { $ScratchRoot = Join-Path ([IO.Path]::GetTempPath()) 'glance-tests' }
    New-Item -ItemType Directory -Force $ScratchRoot | Out-Null
    $ScratchRoot = (Resolve-Path $ScratchRoot).Path
    $ns = 't' + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $stateDir = Join-Path $ScratchRoot "state-$ns"
    New-Item -ItemType Directory -Force $stateDir | Out-Null

    # What the real Glance on this PC looks like right now, so it can be checked (and restored) at the end.
    $realDir = Join-Path $env:LOCALAPPDATA 'Glance'
    $realLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glance.lnk'
    $realSettings = Join-Path $realDir 'settings.json'
    $existing = @(Get-Process -Name Glance -ErrorAction SilentlyContinue | ForEach-Object {
            try { [pscustomobject]@{ Id = $_.Id; Start = $_.StartTime } } catch { } })
    [pscustomobject]@{
        Exe = $Exe; Ns = $ns; StateDir = $stateDir; ScratchRoot = $ScratchRoot
        RunName = "Glance-test-$ns"
        LinkPath = Join-Path (Join-Path $stateDir 'StartMenu') "Glance-test-$ns.lnk"
        Owned = New-Object System.Collections.ArrayList
        RealLnk = $realLnk; RealSettings = $realSettings
        RealRun = (Get-ItemProperty $script:RunKey -Name Glance -ErrorAction SilentlyContinue).Glance
        RealLnkBytes = if (Test-Path $realLnk) { [IO.File]::ReadAllBytes($realLnk) } else { $null }
        RealSettingsBytes = if (Test-Path $realSettings) { [IO.File]::ReadAllBytes($realSettings) } else { $null }
        ExistingCopies = $existing
    }
}

function Register-Owned($t, [int]$Id, [string]$Kind) {
    $start = $null
    try { $start = [Diagnostics.Process]::GetProcessById($Id).StartTime } catch { return }
    [void]$t.Owned.Add([pscustomobject]@{ Id = $Id; Start = $start; Kind = $Kind })
}

function Test-OwnedAlive($t, $entry) {
    try { return ([Diagnostics.Process]::GetProcessById($entry.Id).StartTime -eq $entry.Start) } catch { return $false }
}

# Stops one process the tests started: only if it is still the same process (same number AND start time).
function Stop-Owned($t, [int]$Id) {
    foreach ($e in @($t.Owned | Where-Object { $_.Id -eq $Id })) {
        if (Test-OwnedAlive $t $e) { Stop-Process -Id $e.Id -Force -ErrorAction SilentlyContinue }
    }
}

# ---- starting and stopping the test copy of Glance -----------------------------------------------------------

function New-GlanceStartInfo($t, [string]$File, [string[]]$ArgumentList) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $File
    $psi.Arguments = ($ArgumentList -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['GLANCE_TEST_NS'] = $t.Ns
    $psi.EnvironmentVariables['GLANCE_TEST_DIR'] = $t.StateDir
    return $psi
}

function Start-GlanceTestApp($t, [string[]]$ArgumentList = @()) {
    $p = [Diagnostics.Process]::Start((New-GlanceStartInfo $t $t.Exe $ArgumentList))
    Register-Owned $t $p.Id 'app'
    return $p
}

# Runs the app with arguments (--quit, --end ...) and waits. Returns the exit code.
function Invoke-GlanceTestApp($t, [string[]]$ArgumentList, [int]$TimeoutSec = 30) {
    $p = [Diagnostics.Process]::Start((New-GlanceStartInfo $t $t.Exe $ArgumentList))
    Register-Owned $t $p.Id 'helper'
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { Stop-Owned $t $p.Id; return -1 }
    return $p.ExitCode
}

# The test copies that are still running (only ones this session started).
function Get-GlanceCopies($t) { @($t.Owned | Where-Object { $_.Kind -eq 'app' -and (Test-OwnedAlive $t $_) }) }
function Get-GlanceCopyCount($t) { return @(Get-GlanceCopies $t).Count }

# Asks the test copy to quit through its own quit signal; stops it by exact number and start time only if it doesn't.
function Stop-GlanceTestApp($t, $Proc) {
    [void](Invoke-GlanceTestApp $t @('--quit') 10)
    if ($Proc -and -not $Proc.WaitForExit(5000)) { Stop-Owned $t $Proc.Id }
}
function Stop-AllGlanceTestApps($t) {
    [void](Invoke-GlanceTestApp $t @('--quit') 10)
    Start-Sleep -Milliseconds 1500
    foreach ($e in @($t.Owned | Where-Object { $_.Kind -eq 'app' })) { Stop-Owned $t $e.Id }
}

# The test program that holds a few GB of memory, so there is a safe row to End. Optionally starts a child of its own.
function Start-GlanceHog($t, [string]$HogExe, [int]$Gb = 3, [string]$ChildExe = '') {
    $a = @("$Gb")
    if ($ChildExe) { $a += '--child'; $a += "`"$ChildExe`"" }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $HogExe; $psi.Arguments = ($a -join ' '); $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $p = [Diagnostics.Process]::Start($psi)
    Register-Owned $t $p.Id 'hog'
    return $p
}

# Writes down a child of one of our own test programs, found by its parent number, so it can be cleaned up by exact identity.
function Register-ChildOf($t, [int]$ParentId, [string]$Name) {
    $kid = Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentId" | Where-Object Name -eq $Name | Select-Object -First 1
    if ($kid) { Register-Owned $t ([int]$kid.ProcessId) 'hog' }
    return $kid
}

# ---- reading the app's own preview, and posting to the app's own windows ---------------------------------------

function Get-GlancePreview($t) {
    try { $f = Join-Path $t.StateDir 'preview.txt'; if (Test-Path $f) { return [IO.File]::ReadAllText($f) } } catch { }
    return ''
}
function Test-PreviewVisible($t) { return ((Get-GlancePreview $t) -match 'visible=True') }
function Test-PreviewPinned($t) { return ((Get-GlancePreview $t) -match 'pinned=True') }

function ConvertTo-Rect([string]$s) {
    if ($s -match 'X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)') {
        return New-Object System.Drawing.Rectangle ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3]), ([int]$Matches[4])
    }
    return [System.Drawing.Rectangle]::Empty
}
function Get-PreviewRect($t, [string]$name) {
    $line = (Get-GlancePreview $t) -split "`r?`n" | Where-Object { $_ -match "(^|\s)$name=\{" } | Select-Object -First 1
    if ($line -and $line -match "$name=(\{[^}]*\})") { return ConvertTo-Rect $Matches[1] }
    return [System.Drawing.Rectangle]::Empty
}

# 'card' or 'tray'. Returns Zero unless the window really belongs to a test copy this session started.
function Find-GlanceWindow($t, [string]$Which) {
    $title = if ($Which -eq 'tray') { "Glance tray [$($t.Ns)]" } else { "Glance [$($t.Ns)]" }
    $h = [GlanceTestWin]::FindWindow([IntPtr]::Zero, $title)
    if ($h -eq [IntPtr]::Zero) { return [IntPtr]::Zero }
    [uint32]$owner = 0
    [void][GlanceTestWin]::GetWindowThreadProcessId($h, [ref]$owner)
    $mine = @($t.Owned | Where-Object { $_.Kind -eq 'app' -and $_.Id -eq $owner -and (Test-OwnedAlive $t $_) })
    if ($mine.Count -eq 0) { return [IntPtr]::Zero }
    return $h
}

# A click on the card, delivered as window messages to the card's own window. Takes a screen point and lets Windows convert
# it to the card's own coordinates.
function Send-CardClick([IntPtr]$hwnd, [int]$sx, [int]$sy) {
    if ($hwnd -eq [IntPtr]::Zero) { return }
    $p = New-Object GlanceTestWin+POINT; $p.X = $sx; $p.Y = $sy; [void][GlanceTestWin]::ScreenToClient($hwnd, [ref]$p)
    $lp = [IntPtr](($p.Y -shl 16) -bor ($p.X -band 0xFFFF))
    [void][GlanceTestWin]::PostMessage($hwnd, 0x200, [IntPtr]0, $lp); Start-Sleep -Milliseconds 80   # WM_MOUSEMOVE
    [void][GlanceTestWin]::PostMessage($hwnd, 0x201, [IntPtr]1, $lp); Start-Sleep -Milliseconds 80   # WM_LBUTTONDOWN
    [void][GlanceTestWin]::PostMessage($hwnd, 0x202, [IntPtr]0, $lp)                                 # WM_LBUTTONUP
}

# The same message Windows' shell sends the tray window: 0x406 = hover (NIN_POPUPOPEN), 0x400 = click (NIN_SELECT).
function Send-TrayEvent([IntPtr]$hwnd, [int]$iconId, [int]$evt, [System.Drawing.Rectangle]$at) {
    if ($hwnd -eq [IntPtr]::Zero) { return }
    $x = $at.X + [int]($at.Width / 2); $y = $at.Y + [int]($at.Height / 2)
    $wp = [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF))
    $lp = [IntPtr](($iconId -shl 16) -bor $evt)
    [void][GlanceTestWin]::PostMessage($hwnd, 0x8001, $wp, $lp)   # WM_APP + 1, Glance's tray callback
}

# The app's own pictures of itself (card.png and tiles.png from --preview), copied to a folder. Never a screen capture.
function Save-GlancePictures($t, [string]$Dir, [string]$Name) {
    New-Item -ItemType Directory -Force $Dir | Out-Null
    foreach ($f in 'card', 'tiles') {
        $src = Join-Path $t.StateDir "$f.png"
        if (Test-Path $src) { Copy-Item $src (Join-Path $Dir "$Name-$f.png") -Force }
    }
}

# ---- afterwards: is your real Glance untouched? --------------------------------------------------------------

# Which parts of your real Glance state differ from how they were when the session started: 'startup entry',
# 'Start menu shortcut', 'settings file'. Empty = nothing changed.
function Get-RealStateChanges($t) {
    $diff = New-Object System.Collections.ArrayList
    $now = (Get-ItemProperty $script:RunKey -Name Glance -ErrorAction SilentlyContinue).Glance
    if ($now -ne $t.RealRun) { [void]$diff.Add('startup entry') }
    $lnkNow = if (Test-Path $t.RealLnk) { [IO.File]::ReadAllBytes($t.RealLnk) } else { $null }
    if (-not (Compare-Bytes $lnkNow $t.RealLnkBytes)) { [void]$diff.Add('Start menu shortcut') }
    $setNow = if (Test-Path $t.RealSettings) { [IO.File]::ReadAllBytes($t.RealSettings) } else { $null }
    if (-not (Compare-Bytes $setNow $t.RealSettingsBytes)) { [void]$diff.Add('settings file') }
    return $diff.ToArray()
}

# Puts back anything Get-RealStateChanges reports (which would be a bug in a test or in Glance). Returns what it had to put back.
function Restore-RealGlanceState($t) {
    $changed = @(Get-RealStateChanges $t)
    if ($changed -contains 'startup entry') {
        if ($null -eq $t.RealRun) { Remove-ItemProperty $script:RunKey -Name Glance -ErrorAction SilentlyContinue }
        else { Set-ItemProperty $script:RunKey -Name Glance -Value $t.RealRun }
    }
    if ($changed -contains 'Start menu shortcut') {
        if ($null -eq $t.RealLnkBytes) { Remove-Item $t.RealLnk -Force -ErrorAction SilentlyContinue }
        else { [IO.File]::WriteAllBytes($t.RealLnk, $t.RealLnkBytes) }
    }
    if ($changed -contains 'settings file') {
        if ($null -eq $t.RealSettingsBytes) { Remove-Item $t.RealSettings -Force -ErrorAction SilentlyContinue }
        else { [IO.File]::WriteAllBytes($t.RealSettings, $t.RealSettingsBytes) }
    }
    return $changed
}

function Compare-Bytes($a, $b) {
    if ($null -eq $a -and $null -eq $b) { return $true }
    if ($null -eq $a -or $null -eq $b) { return $false }
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

# Every copy of Glance that was already running before the test is still the same process.
function Test-ExistingCopiesUntouched($t) {
    foreach ($e in $t.ExistingCopies) {
        try { if ([Diagnostics.Process]::GetProcessById($e.Id).StartTime -ne $e.Start) { return $false } } catch { return $false }
    }
    return $true
}

# Always call this in a finally block: stops what the tests started (exact identity), puts back real state if anything
# changed, removes the test copy's own startup entry, shortcut and settings folder.
function Remove-GlanceTest($t) {
    foreach ($e in @($t.Owned)) { Stop-Owned $t $e.Id }
    Start-Sleep -Milliseconds 300
    $fixed = Restore-RealGlanceState $t
    if ($fixed.Count -gt 0) { Write-Output "WARNING: the test run changed your real $($fixed -join ', '); it has been put back." }
    Remove-ItemProperty $script:RunKey -Name $t.RunName -ErrorAction SilentlyContinue
    $leaf = Split-Path $t.StateDir -Leaf
    if ($leaf -eq "state-$($t.Ns)" -and (Test-Path $t.StateDir) -and ((Resolve-Path $t.StateDir).Path.StartsWith($t.ScratchRoot))) {
        Remove-Item $t.StateDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
