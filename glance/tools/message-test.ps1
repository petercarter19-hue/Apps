# Verifies the tray-event wiring by posting the same messages the shell sends to a TEST copy of Glance's tray window:
#   NIN_POPUPOPEN (hover)  -> card opens next to the memory tile (and, with no pointer over it, auto-closes after ~0.5 s)
#   NIN_SELECT (click)     -> card opens pinned and stays; a second NIN_SELECT closes it
# The test copy has its own made-up names and settings folder (see TestLib.ps1); your own Glance is never stopped or changed,
# and nothing moves your mouse or captures your screen. Pictures are the app's own renders.
# Usage: tools\message-test.ps1 [-ExePath <Glance.exe>] [-ScratchRoot <folder>]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Glance.exe'),
    [string]$ScratchRoot = ''
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'TestLib.ps1')

$t = New-GlanceTest -Exe $ExePath -ScratchRoot $ScratchRoot
try {
    $app = Start-GlanceTestApp $t @('--preview')
    Start-Sleep 4

    $tray = Find-GlanceWindow $t 'tray'
    Check 'tray window found (and it belongs to the test copy)' ($tray -ne [IntPtr]::Zero)
    $ram = Get-PreviewRect $t 'ram'
    Write-Output "memory tile rect: $ram"

    # hover: NIN_POPUPOPEN = 0x406, icon id 1 = memory tile
    Send-TrayEvent $tray 1 0x406 $ram
    $seen = $false; $bounds = [System.Drawing.Rectangle]::Empty
    for ($i = 0; $i -lt 30 -and -not $seen; $i++) { Start-Sleep -Milliseconds 50; if (Test-PreviewVisible $t) { $seen = $true; $bounds = Get-PreviewRect $t 'bounds' } }
    Check 'shell hover message opens the card' $seen
    if ($seen) {
        Write-Output "card bounds: $bounds (tile at $ram)"
        $onScreen = $bounds.Bottom -le $ram.Top -or $bounds.Right -le $ram.Left -or $bounds.X -ge $ram.Right
        Check 'card sits beside the tile, not on top of it' $onScreen
        Check 'card is not pinned after a hover' (-not (Test-PreviewPinned $t))
    }
    Start-Sleep 2
    Check 'hover card closed again (no pointer over it)' (-not (Test-PreviewVisible $t))

    # click: NIN_SELECT = 0x400 -> pinned
    Send-TrayEvent $tray 1 0x400 $ram
    Start-Sleep 1
    Check 'clicking the tile opens the card pinned' ((Test-PreviewVisible $t) -and (Test-PreviewPinned $t))
    Start-Sleep 2
    Check 'pinned card stays open' (Test-PreviewVisible $t)
    Send-TrayEvent $tray 1 0x400 $ram
    Start-Sleep 1
    Check 'clicking the tile again closes it' (-not (Test-PreviewVisible $t))

    # CPU tile click sorts by CPU
    Send-TrayEvent $tray 2 0x400 (Get-PreviewRect $t 'cpu')
    Start-Sleep 2
    Save-GlancePictures $t (Join-Path $PSScriptRoot 'shots') 'C1-by-cpu'
    Check 'CPU tile opens the card' (Test-PreviewVisible $t)
    Send-TrayEvent $tray 2 0x400 (Get-PreviewRect $t 'cpu')
    Start-Sleep 1

    Stop-GlanceTestApp $t $app
    Check 'your own Glance (if you have one running) was not touched' (Test-ExistingCopiesUntouched $t)
}
finally {
    Remove-GlanceTest $t
}
Write-TestSummary
exit $script:Failed
