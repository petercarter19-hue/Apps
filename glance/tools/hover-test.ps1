# Hover and tray-icon checks that never touch your pointer, your taskbar or your screen.
#
# This used to move the real mouse onto the memory tile and open Windows' hidden-icons flyout. That can't be done safely on a
# real PC (it moves your cursor and can capture whatever is on screen), so those checks now live only in
# tools\disposable-vm-only\ (Windows Sandbox only, never run by default).
#
# What this does instead: it posts the same events Windows' shell sends the tray window to a TEST copy of Glance's own window,
# and reads the answer from the app's own --preview file:
#   hover event on the memory tile            -> the card opens, not pinned, beside the tile
#   click event on the tile twice             -> pinned, then closed
# Usage: tools\hover-test.ps1 [-ExePath <Glance.exe>] [-ScratchRoot <folder>]
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
    $ram = Get-PreviewRect $t 'ram'; $cpu = Get-PreviewRect $t 'cpu'
    Write-Output "memory tile $ram   cpu tile $cpu"
    Check 'the app reports where its tiles are' (-not $ram.IsEmpty)

    Send-TrayEvent $tray 1 0x406 $ram                       # NIN_POPUPOPEN on the memory tile
    $seen = $false
    for ($i = 0; $i -lt 30 -and -not $seen; $i++) { Start-Sleep -Milliseconds 50; $seen = Test-PreviewVisible $t }
    Check 'hover event on the memory tile opens the card' $seen
    Check 'a hover-opened card is not pinned' (-not (Test-PreviewPinned $t))

    Start-Sleep 2
    Check 'with no pointer over it, the hover card closes' (-not (Test-PreviewVisible $t))

    Send-TrayEvent $tray 1 0x400 $ram                       # NIN_SELECT (click)
    Start-Sleep 1
    Check 'a click opens the card pinned' ((Test-PreviewVisible $t) -and (Test-PreviewPinned $t))
    Send-TrayEvent $tray 1 0x400 $ram
    Start-Sleep 1
    Check 'a second click closes it' (-not (Test-PreviewVisible $t))

    Stop-GlanceTestApp $t $app
    Check 'your own Glance (if you have one running) was not touched' (Test-ExistingCopiesUntouched $t)
}
finally {
    Remove-GlanceTest $t
}
Write-TestSummary
exit $script:Failed
