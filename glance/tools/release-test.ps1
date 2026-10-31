# Release checks for a Glance build, run from a test folder. Safe on your own PC (see TestLib.ps1):
#   * The Glance it starts is a TEST copy with its own made-up lock, quit signal, settings folder, Start with Windows entry and
#     Start menu shortcut. Your own Glance is never stopped, replaced or changed.
#   * No real mouse moves, no key presses, no screen pictures; clicks are posted to the test copy's own card window.
#   * End only ever touches the test hogs, and only the exact processes the test started (number and start time).
#   * When it finishes, even if a check throws, a finally block stops what it started, removes its own entries and checks
#     (and, if anything differs, restores) your real startup entry, Start menu shortcut and settings.
# The test copy never touches the real Start with Windows key at all (it keeps its own record in its settings folder), and the
# test asserts that the real key is exactly as it was.
# Usage: tools\release-test.ps1 [-Exe <Glance.exe>] [-HogDir <folder with GlanceHog.exe>] [-ScratchRoot <folder>] [-LeakSeconds 60]
# Publish tools\Hog into tools\Hog\dist first. Run it from a normal folder, not from inside a temp folder: Glance deliberately
# doesn't register a copy that lives in a temp folder.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\Glance.exe'),
    [string]$HogDir = (Join-Path $PSScriptRoot 'Hog\dist'),
    [string]$ScratchRoot = '',
    [int]$LeakSeconds = 60
)
$Exe = (Resolve-Path $Exe).Path
if (-not (Test-Path (Join-Path $HogDir 'GlanceHogKid.exe'))) { Copy-Item (Join-Path $HogDir 'GlanceHog.exe') (Join-Path $HogDir 'GlanceHogKid.exe') }
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'TestLib.ps1')
if ($Exe.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Run this with Glance.exe in a normal folder, not under $([IO.Path]::GetTempPath())"
}

$t = New-GlanceTest -Exe $Exe -ScratchRoot $ScratchRoot
$settings = Join-Path $t.StateDir 'settings.json'
$log = Join-Path $t.StateDir 'log.txt'
$lnk = $t.LinkPath
$startupFile = $t.StartupFile
function RunValue { if (Test-Path $startupFile) { [IO.File]::ReadAllText($startupFile) } else { $null } }
$mySid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

try {
    # Make sure the test copy starts from nothing (it has its own names, so this only touches the test copy's own entries).
    Remove-Item $settings, $lnk, $startupFile -ErrorAction SilentlyContinue

    # ---- 1. first run on a PC: settings created, Start with Windows on, Start menu entry made ----------------
    Write-Output '== 1. first run'
    $first = Start-GlanceTestApp $t
    Start-Sleep 4
    Check 'first run: Glance is running' ((Get-GlanceCopyCount $t) -eq 1)
    Check 'first run: settings file created' (Test-Path $settings)
    $run = RunValue
    Check "first run: Start with Windows turned on in the test copy's own record ($run)" ($run -eq "`"$Exe`"")
    Check 'first run: nothing was written to the real Start with Windows key' ((@(Get-RealStateChanges $t) -notcontains 'startup entry') -and (@((Get-GlanceRunValues).Keys | Where-Object { $_ -like 'Glance-test-*' }).Count -eq 0))
    $sh = New-Object -ComObject WScript.Shell
    $target = if (Test-Path $lnk) { $sh.CreateShortcut($lnk).TargetPath } else { '' }
    Check "first run: Start menu entry points at this copy ($target)" ($target -eq $Exe)
    Check 'first run: the test copy used its own names, not the real Glance entry' ($lnk -notlike '*\Programs\Glance.lnk' -and $startupFile -like "$($t.StateDir)*")

    # A bad test setting is refused, never silently turned into the real names (exit code 2, nothing started).
    Check 'bad test name is refused (exit 2)' ((Invoke-GlanceWithEnvironment $t 'bad name!' '') -eq 2)
    Check 'a relative test folder is refused (exit 2)' ((Invoke-GlanceWithEnvironment $t 'okname1' 'relative\folder') -eq 2)
    Check 'a test folder without a test name is refused (exit 2)' ((Invoke-GlanceWithEnvironment $t '' (Join-Path $t.ScratchRoot 'x')) -eq 2)
    Check 'those refusals started nothing' ((Get-GlanceCopyCount $t) -eq 1)

    # ---- 2. opening it again restarts it, and only one copy is left ------------------------------------------
    Write-Output '== 2. open again = restart'
    $a = (Get-GlanceCopies $t)[0].Id
    $second = Start-GlanceTestApp $t
    Start-Sleep 5
    $now = @(Get-GlanceCopies $t)
    Check "restart: the old copy ($a) quit" (-not (@($now | Where-Object { $_.Id -eq $a }).Count))
    Check "restart: exactly one copy running ($(($now | ForEach-Object { $_.Id }) -join ','))" ($now.Count -eq 1)
    Start-Sleep 5
    Check 'restart: the new copy is still running 10 s later' ((Get-GlanceCopyCount $t) -eq 1)
    [void](Start-GlanceTestApp $t); Start-Sleep 5
    Check 'restart twice in a row: still exactly one copy' ((Get-GlanceCopyCount $t) -eq 1)

    # ---- 3. --quit ---------------------------------------------------------------------------------------------
    Write-Output '== 3. quit'
    [void](Invoke-GlanceTestApp $t @('--quit')); Start-Sleep 3
    Check '--quit closes it' ((Get-GlanceCopyCount $t) -eq 0)

    # ---- 4. a moved copy fixes its startup entry -----------------------------------------------------------
    Write-Output '== 4. moved file'
    [IO.File]::WriteAllText($startupFile, '"C:\Old\Place\Glance.exe"')
    $moved = Start-GlanceTestApp $t; Start-Sleep 4
    $run = RunValue
    Check "startup entry repointed to this copy ($run)" ($run -eq "`"$Exe`"")
    Stop-GlanceTestApp $t $moved

    # ---- 5. "As admin" helper ends only the exact process it was given -------------------------------------------
    Write-Output '== 5. --end checks the start time'
    $hog = Start-GlanceHog $t (Join-Path $HogDir 'GlanceHog.exe') 1
    Start-Sleep 3
    $created = [GlanceTestWin]::Created($hog.Id)
    $code = Invoke-GlanceTestApp $t @('--end', '--user', $mySid, "$($hog.Id):$($created + 1)")
    Check "wrong start time: hog left alone (exit $code)" (-not $hog.HasExited)
    $code = Invoke-GlanceTestApp $t @('--end', "$($hog.Id):$created")
    Check "no --user: hog left alone, exit code says refused (exit $code)" ((-not $hog.HasExited) -and $code -ge 1)
    $code = Invoke-GlanceTestApp $t @('--end', '--user', 'S-1-5-21-1-2-3-4', "$($hog.Id):$created")
    Check "--user is someone else's account: hog left alone (exit $code)" ((-not $hog.HasExited) -and $code -ge 1)
    $code = Invoke-GlanceTestApp $t @('--end', '--user', 'S-1-5-18', "$($hog.Id):$created")
    Check "--user is SYSTEM, not a person: hog left alone (exit $code)" ((-not $hog.HasExited) -and $code -ge 1)
    $code = Invoke-GlanceTestApp $t @('--end', '--user', $mySid, "$($hog.Id):$created")
    Start-Sleep 1
    Check "right start time: hog ended (exit $code)" $hog.HasExited
    Check '--end did not leave a Glance running' ((Get-GlanceCopyCount $t) -eq 0)

    # ---- 6. two clicks on End end the app and the child it started ---------------------------------------------
    Write-Output '== 6. End takes the children'
    $hog = Start-GlanceHog $t (Join-Path $HogDir 'GlanceHog.exe') 3 (Join-Path $HogDir 'GlanceHogKid.exe')
    Start-Sleep 6
    $kid = Register-ChildOf $t $hog.Id 'GlanceHogKid.exe'
    Check "test hog $($hog.Id) started its child $($kid.ProcessId)" ($null -ne $kid)
    $app = Start-GlanceTestApp $t @('--preview', '--pinned')
    Start-Sleep 5
    $lines = (Get-GlancePreview $t) -split "`r?`n"
    $hogLine = $lines | Where-Object { $_ -match "^\d+\|[^|]*\|[^|]*\|(\d+,)*$($hog.Id)(,\d+)*\|" } | Select-Object -First 1
    Check 'hog row is in the top ten' ($null -ne $hogLine)
    $hwnd = Find-GlanceWindow $t 'card'
    Check 'card window found (and it belongs to the test copy)' ($hwnd -ne [IntPtr]::Zero)
    if ($hogLine -and $hwnd -ne [IntPtr]::Zero) {
        $btn = ConvertTo-Rect ($hogLine -replace '.*btn=', '')
        $sx = $btn.X + [int]($btn.Width / 2); $sy = $btn.Y + [int]($btn.Height / 2)
        Send-CardClick $t $hwnd $sx $sy; Start-Sleep -Milliseconds 800
        Check 'one click does not end it' (-not $hog.HasExited)
        Send-CardClick $t $hwnd $sx $sy; Start-Sleep 2
        Check 'two clicks ended the hog' $hog.HasExited
        $kidGone = $true
        if ($kid) { try { [void][Diagnostics.Process]::GetProcessById([int]$kid.ProcessId); $kidGone = $false } catch { } }
        Check 'and its child' $kidGone
        Check 'Glance is still running after End' ((Get-GlanceCopyCount $t) -eq 1)
    }

    # ---- 7. no handle leak with the card open ------------------------------------------------------------------
    Write-Output "== 7. leak check ($LeakSeconds s, card open)"
    $g = [Diagnostics.Process]::GetProcessById((Get-GlanceCopies $t)[0].Id)
    $gdi0 = [GlanceTestWin]::GetGuiResources($g.Handle, 0); $h0 = $g.HandleCount
    Start-Sleep $LeakSeconds
    $g.Refresh()
    $gdi1 = [GlanceTestWin]::GetGuiResources($g.Handle, 0); $h1 = $g.HandleCount
    Check "graphics handles steady over $LeakSeconds s ($gdi0 -> $gdi1)" (($gdi1 - $gdi0) -lt 20)
    Check "process handles steady over $LeakSeconds s ($h0 -> $h1)" (($h1 - $h0) -lt 50)
    Stop-AllGlanceTestApps $t

    # ---- 8. no errors logged -----------------------------------------------------------------------------------
    $logSize = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
    Check 'no errors in the test copy''s log' ($logSize -eq 0)
    if ($logSize -gt 0) { Get-Content $log -Tail 20 }

    # ---- 9. your own Glance and settings were never touched ---------------------------------------------------------
    Write-Output '== 9. your real Glance'
    $changed = @(Get-RealStateChanges $t)
    Check "your real Start with Windows entry, Start menu shortcut and settings are exactly as before ($($changed -join ', '))" ($changed.Count -eq 0)
    Check 'your own Glance (if you have one running) is still the same process' (Test-ExistingCopiesUntouched $t)
}
catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    Check 'the test run finished without an unexpected error' $false
}
finally {
    # Always: stop only what this run started, remove the test copy's own entries, check and restore your real state.
    Remove-GlanceTest $t
}
Write-TestSummary
exit $script:Failed
