# Checks that an armed End ("End?") is cancelled whenever the card goes away, so reopening the card never finds an End still primed:
#   A. pinned card: click End once (arms it), close the card with a tray click, reopen it with a tray click, click End once more
#      -> the test hog must still be running (the second click only arms it again). Two fresh clicks then end it.
#   B. hover card: arm End, let the card close because no pointer is over it, hover again, click End once
#      -> the test hog must still be running.
# Safe on your own PC (see TestLib.ps1): a test copy with its own names, events posted to its own windows only, nothing moves your
# mouse, no screen pictures, and only the test hog and test copy are ever stopped (exact process number and start time).
# Usage: tools\cancel-test.ps1 [-ExePath <Glance.exe>] [-HogExe <GlanceHog.exe>] [-ScratchRoot <folder>]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Glance.exe'),
    [string]$HogExe = (Join-Path $PSScriptRoot 'Hog\dist\GlanceHog.exe'),
    [string]$ScratchRoot = ''
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'TestLib.ps1')
if (-not (Test-Path $HogExe)) { throw 'publish the hog first (see README, Building)' }

# The hog's row in the app's own preview: its End button rectangle and whether the row is armed.
function Get-HogRow($t, [int]$HogId) {
    $line = ((Get-GlancePreview $t) -split "`r?`n") | Where-Object { $_ -match "^\d+\|[^|]*\|[^|]*\|(\d+,)*$HogId(,\d+)*\|" } | Select-Object -First 1
    if (-not $line) { return $null }
    [pscustomobject]@{ Line = $line; Armed = ($line -match '\|[^|]*armed[^|]*\|btn='); Button = (ConvertTo-Rect ($line -replace '.*btn=', '')) }
}
function Click-HogButton($t, [IntPtr]$hwnd, $row) {
    Send-CardClick $t $hwnd ($row.Button.X + [int]($row.Button.Width / 2)) ($row.Button.Y + [int]($row.Button.Height / 2))
}
function Wait-HogArmed($t, [int]$HogId, [bool]$Armed, [int]$Ms = 3000) {
    $until = (Get-Date).AddMilliseconds($Ms)
    while ((Get-Date) -lt $until) { $r = Get-HogRow $t $HogId; if ($r -and $r.Armed -eq $Armed) { return $true }; Start-Sleep -Milliseconds 60 }
    return $false
}
function Wait-Preview($t, [bool]$Visible, [int]$Ms = 3000) {
    $until = (Get-Date).AddMilliseconds($Ms)
    while ((Get-Date) -lt $until) { if ((Test-PreviewVisible $t) -eq $Visible) { return $true }; Start-Sleep -Milliseconds 40 }
    return $false
}

$t = New-GlanceTest -Exe $ExePath -ScratchRoot $ScratchRoot
try {
    $hog = Start-GlanceHog $t $HogExe 3
    Start-Sleep 5

    # ---- A. pinned card -------------------------------------------------------------------------------------------
    # A pinned card also closes on any real click or Esc anywhere on the PC (that is the product's click-away rule), so if you
    # happen to click while this runs the scenario is simply run again, up to five times; a genuine failure fails every time.
    Write-Output '== A. pinned card: close and reopen'
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $mark = $script:Results.Count
        $app = Start-GlanceTestApp $t @('--preview', '--pinned')
        [void](Wait-Preview $t $true 8000)
        $hwnd = Find-GlanceWindow $t 'card'; $tray = Find-GlanceWindow $t 'tray'
        Check 'card and tray windows found (and they belong to the test copy)' (($hwnd -ne [IntPtr]::Zero) -and ($tray -ne [IntPtr]::Zero))
        $row = Get-HogRow $t $hog.Id
        Check 'the test hog is in the top ten, not armed' (($null -ne $row) -and (-not $row.Armed))
        if ($row -and $hwnd -ne [IntPtr]::Zero) {
            Click-HogButton $t $hwnd $row
            Check 'one click arms the hog row' (Wait-HogArmed $t $hog.Id $true 2500)
            $ram = Get-PreviewRect $t 'ram'
            Send-TrayEvent $t $tray 1 0x400 $ram                    # click the tile: a pinned card closes
            Check 'the pinned card closed' (Wait-Preview $t $false 2000)
            Send-TrayEvent $t $tray 1 0x400 $ram                    # click again: it reopens pinned
            Check 'the card reopened' (Wait-Preview $t $true 2000)
            Start-Sleep -Milliseconds 150
            $row = Get-HogRow $t $hog.Id
            Check 'after reopening, the hog row is no longer armed' (($null -ne $row) -and (-not $row.Armed))
            if ($row) {
                Click-HogButton $t $hwnd $row
                Start-Sleep -Milliseconds 1200
                Check 'one click after reopening does not end it (it only arms it again)' (-not $hog.HasExited)
                $row = Get-HogRow $t $hog.Id
                if ($row) { Click-HogButton $t $hwnd $row }
                Start-Sleep -Milliseconds 1500
                Check 'a second click right after that ends it (normal two-click End still works)' $hog.HasExited
            }
        }
        Stop-GlanceTestApp $t $app
        $failedNow = @($script:Results | Select-Object -Skip $mark | Where-Object { -not $_.Ok }).Count
        if ($failedNow -eq 0 -or $attempt -eq 5) { break }
        Write-Output "  (attempt $attempt had a failure; running it again in case something else closed the card)"
        while ($script:Results.Count -gt $mark) { $script:Results.RemoveAt($script:Results.Count - 1) }
        if ($hog.HasExited) { $hog = Start-GlanceHog $t $HogExe 3; Start-Sleep 5 }
    }
    if ($hog.HasExited) { $hog = Start-GlanceHog $t $HogExe 3; Start-Sleep 5 }

    # ---- B. hover card ---------------------------------------------------------------------------------------------
    Write-Output '== B. hover card: it closes by itself, then reopens'
    $app = Start-GlanceTestApp $t @('--preview')
    Start-Sleep 4
    $hwnd = Find-GlanceWindow $t 'card'; $tray = Find-GlanceWindow $t 'tray'
    $ram = Get-PreviewRect $t 'ram'
    Send-TrayEvent $t $tray 1 0x406 $ram                        # hover the memory tile
    Check 'hover opens the card' (Wait-Preview $t $true)
    $row = Get-HogRow $t $hog.Id
    Check 'the test hog is in the hover card' ($null -ne $row)
    if ($row) {
        Click-HogButton $t $hwnd $row                            # arm it, quickly, before the card closes by itself
        $closed = Wait-Preview $t $false 4000
        Check 'with no pointer over it the hover card closes' $closed
        Send-TrayEvent $t $tray 1 0x406 $ram                    # hover again
        Check 'hovering again reopens it' (Wait-Preview $t $true)
        Start-Sleep -Milliseconds 100
        $row = Get-HogRow $t $hog.Id
        if ($row) {
            Click-HogButton $t $hwnd $row
            Start-Sleep -Milliseconds 300
            Check 'one click after the card came back does not end the hog' (-not $hog.HasExited)
        }
    }
    Stop-GlanceTestApp $t $app
    Check 'your own Glance (if you have one running) was not touched' (Test-ExistingCopiesUntouched $t)
}
finally {
    Remove-GlanceTest $t
}
Write-TestSummary
exit $script:Failed
