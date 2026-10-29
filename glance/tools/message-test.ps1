# Verifies the tray-event wiring by posting the same messages the shell sends to Glance's tray window:
#   NIN_POPUPOPEN (hover)  -> card opens next to the memory tile (and, with no cursor in this session, auto-closes after ~0.5 s)
#   NIN_SELECT (click)     -> card opens pinned and stays; a second NIN_SELECT closes it
# Needs Glance running with --preview (this script starts it).
param([string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Glance.exe'))
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U3 {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
"@
$dump = Join-Path $env:LOCALAPPDATA 'Glance\preview.txt'
function ReadDump { if (Test-Path $dump) { return (Get-Content $dump -Raw) } return '' }
function DumpVisible { return ((ReadDump) -match 'visible=True') }
function DumpPinned { return ((ReadDump) -match 'pinned=True') }
function DumpRect([string]$name) {
    if ((ReadDump) -match "$name=\{X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)\}") {
        return New-Object System.Drawing.Rectangle ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3]), ([int]$Matches[4])
    }
    return [System.Drawing.Rectangle]::Empty
}
function Check([string]$what, [bool]$ok) { Write-Output "$(if ($ok) {'PASS'} else {'FAIL'})  $what" }
function TrayMsg([IntPtr]$hwnd, [int]$iconId, [int]$evt, [System.Drawing.Rectangle]$at) {
    $x = $at.X + [int]($at.Width / 2); $y = $at.Y + [int]($at.Height / 2)
    $wp = [IntPtr](($y -shl 16) -bor ($x -band 0xFFFF))
    $lp = [IntPtr](($iconId -shl 16) -bor $evt)
    [void][U3]::PostMessage($hwnd, 0x8001, $wp, $lp)   # WM_APP + 1, Glance's tray callback
}

& $ExePath --quit; Start-Sleep 1
Get-Process Glance -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $dump -ErrorAction SilentlyContinue
Start-Process $ExePath -ArgumentList '--preview' | Out-Null
Start-Sleep 4

$tray = [U3]::FindWindow([IntPtr]::Zero, 'Glance tray')
Check 'tray window found' ($tray -ne [IntPtr]::Zero)
$ram = DumpRect 'ram'
Write-Output "memory tile rect: $ram"

# hover: NIN_POPUPOPEN = 0x406, icon id 1 = memory tile
TrayMsg $tray 1 0x406 $ram
$seen = $false; $bounds = [System.Drawing.Rectangle]::Empty
for ($i = 0; $i -lt 30 -and -not $seen; $i++) { Start-Sleep -Milliseconds 50; if (DumpVisible) { $seen = $true; $bounds = DumpRect 'bounds' } }
Check 'shell hover message opens the card' $seen
if ($seen) {
    Write-Output "card bounds: $bounds (tile at $ram)"
    $onScreen = $bounds.Bottom -le $ram.Top -or $bounds.Right -le $ram.Left -or $bounds.X -ge $ram.Right
    Check 'card sits beside the tile, not on top of it' $onScreen
    Check 'card is not pinned after a hover' (-not (DumpPinned))
}
Start-Sleep 2
Check 'hover card closed again (no cursor over it in this session)' (-not (DumpVisible))

# click: NIN_SELECT = 0x400 -> pinned
TrayMsg $tray 1 0x400 $ram
Start-Sleep 1
Check 'clicking the tile opens the card pinned' ((DumpVisible) -and (DumpPinned))
Start-Sleep 2
Check 'pinned card stays open' (DumpVisible)
TrayMsg $tray 1 0x400 $ram
Start-Sleep 1
Check 'clicking the tile again closes it' (-not (DumpVisible))

# CPU tile click sorts by CPU
TrayMsg $tray 2 0x400 (DumpRect 'cpu')
Start-Sleep 2
Copy-Item (Join-Path $env:LOCALAPPDATA 'Glance\card.png') (Join-Path $PSScriptRoot 'shots\C1-by-cpu-card.png') -Force -ErrorAction SilentlyContinue
Check 'CPU tile opens the card' (DumpVisible)
TrayMsg $tray 2 0x400 (DumpRect 'cpu')
Start-Sleep 1

& $ExePath --quit; Start-Sleep 1
Get-Process Glance -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process $ExePath | Out-Null
