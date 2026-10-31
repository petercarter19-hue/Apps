# End-to-end check of Glance on this PC. Safe by construction: see TestLib.ps1.
#   A. hover:  a test copy of Glance (--preview, card closed). Windows' own "hover" event for the memory tile is posted to the
#              test copy's own tray window -> the card opens. (Nothing moves your mouse.)
#   B. end:    GlanceHog holds 3 GB. A test copy with --preview --pinned. Two clicks, posted to the test copy's own card window,
#              on the hog's End button -> the hog is gone.
# The pictures saved to shots\ are the app's own renders of itself (card.png, tiles.png), never a capture of your screen.
# Only the test copy and the hog are ever stopped, by exact process number and start time; your own Glance is not touched.
# Usage: tools\smoke-test.ps1 [-ExePath <Glance.exe>] [-ScratchRoot <folder>]. Publish tools\Hog into tools\Hog\dist first.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Glance.exe'),
    [string]$ShotDir = (Join-Path $PSScriptRoot 'shots'),
    [string]$ScratchRoot = '',
    [string]$HogExe = (Join-Path $PSScriptRoot 'Hog\dist\GlanceHog.exe')
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'TestLib.ps1')
if (-not (Test-Path $HogExe)) { throw 'publish the hog first (see README, Building)' }
New-Item -ItemType Directory -Force $ShotDir | Out-Null
Get-ChildItem $ShotDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

$t = New-GlanceTest -Exe $ExePath -ScratchRoot $ScratchRoot
try {
    # ---- A. hover ------------------------------------------------------------------------------------------
    Write-Output '== A. hover'
    $app = Start-GlanceTestApp $t @('--preview')
    Start-Sleep 4
    $ram = Get-PreviewRect $t 'ram'; $cpu = Get-PreviewRect $t 'cpu'
    Write-Output "memory tile $ram   cpu tile $cpu"
    Check 'Glance is running and reporting state' ((Get-GlancePreview $t).Length -gt 0)
    Check 'card starts closed' (-not (Test-PreviewVisible $t))
    $tray = Find-GlanceWindow $t 'tray'
    Check 'tray window found (and it belongs to the test copy)' ($tray -ne [IntPtr]::Zero)
    Send-TrayEvent $t $tray 1 0x406 $ram
    $opened = $false
    for ($i = 0; $i -lt 30 -and -not $opened; $i++) { Start-Sleep -Milliseconds 50; $opened = Test-PreviewVisible $t }
    Check 'the shell hover event for the memory tile opens the card' $opened
    if ($opened) { Save-GlancePictures $t $ShotDir 'A1-hover-open' }
    Start-Sleep 2
    Check 'with no pointer over it, the hover card closes again' (-not (Test-PreviewVisible $t))
    Stop-GlanceTestApp $t $app

    # ---- B. end an app -------------------------------------------------------------------------------------
    Write-Output '== B. end'
    $hog = Start-GlanceHog $t $HogExe 3
    Write-Output "hog pid $($hog.Id)"
    Start-Sleep 5
    $app = Start-GlanceTestApp $t @('--preview', '--pinned')
    Start-Sleep 5
    $bounds = Get-PreviewRect $t 'bounds'
    Check 'pinned card is open' ((Test-PreviewVisible $t) -and (-not $bounds.IsEmpty))
    Save-GlancePictures $t $ShotDir 'B1-card-open'
    $lines = (Get-GlancePreview $t) -split "`r?`n"
    $hogLine = $lines | Where-Object { $_ -match "^\d+\|[^|]*\|[^|]*\|(\d+,)*$($hog.Id)(,\d+)*\|" } | Select-Object -First 1
    Check 'memory hog shows up in the top ten' ($null -ne $hogLine)
    $hwnd = Find-GlanceWindow $t 'card'
    Check 'card window found (and it belongs to the test copy)' ($hwnd -ne [IntPtr]::Zero)
    if ($hogLine -and $hwnd -ne [IntPtr]::Zero) {
        Write-Output "hog row: $hogLine"
        $btn = ConvertTo-Rect ($hogLine -replace '.*btn=', '')
        $sx = $btn.X + [int]($btn.Width / 2)
        $sy = $btn.Y + [int]($btn.Height / 2)
        Send-CardClick $t $hwnd $sx $sy
        Start-Sleep -Milliseconds 1700
        Save-GlancePictures $t $ShotDir 'B2-end-armed'
        Check 'one click does not end it' (-not $hog.HasExited)
        Send-CardClick $t $hwnd $sx $sy
        Start-Sleep 2
        Check 'two clicks on End ended the hog' $hog.HasExited
        Save-GlancePictures $t $ShotDir 'B3-after-end'
    }
    Stop-GlanceTestApp $t $app
    Check 'your own Glance (if you have one running) was not touched' (Test-ExistingCopiesUntouched $t)
}
finally {
    Remove-GlanceTest $t
}
Write-TestSummary
exit $script:Failed
