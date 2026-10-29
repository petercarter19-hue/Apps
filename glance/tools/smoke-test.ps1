# End-to-end check of the published app on this PC. Two runs:
#   A. hover:  Glance --preview (card closed). Move the cursor onto the memory tile -> card opens. Move away -> card closes.
#   B. end:    GlanceHog holds 3 GB. Glance --preview --pinned. Two clicks on the hog's End button -> hog is gone.
# Clicks are posted straight to the card window (WM_LBUTTONDOWN/UP), which works even in a remote session where
# synthetic mouse input is not delivered. Screen capture is attempted but the app's own renders (card.png, tiles.png)
# are always saved to shots\. Leaves a normal Glance running at the end.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Glance.exe'),
    [string]$ShotDir = (Join-Path $PSScriptRoot 'shots')
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U {
  public struct POINT { public int X, Y; }
  // Per-Monitor-V2 (-4): must match the card's own awareness, or Windows rescales our posted click
  // coordinates across the DPI boundary and they miss the button on a scaled monitor.
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
}
"@
[void][U]::SetProcessDpiAwarenessContext([IntPtr](-4))
New-Item -ItemType Directory -Force $ShotDir | Out-Null
Get-ChildItem $ShotDir -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
$stateDir = Join-Path $env:LOCALAPPDATA 'Glance'
$dump = Join-Path $stateDir 'preview.txt'
$screenW = [U]::GetSystemMetrics(0); $screenH = [U]::GetSystemMetrics(1)
$results = @()

function ParseRect([string]$s) {
    if ($s -match 'X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)') {
        return New-Object System.Drawing.Rectangle ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3]), ([int]$Matches[4])
    }
    return [System.Drawing.Rectangle]::Empty
}
function ReadDump { if (Test-Path $dump) { return (Get-Content $dump -Raw) } return '' }
function DumpVisible { return ((ReadDump) -match 'visible=True') }
function DumpRect([string]$name) {
    $line = (ReadDump) -split "`r?`n" | Where-Object { $_ -match "(^|\s)$name=\{" } | Select-Object -First 1
    if (-not $line) { return [System.Drawing.Rectangle]::Empty }
    if ($line -match "$name=(\{[^}]*\})") { return ParseRect $Matches[1] }
    return [System.Drawing.Rectangle]::Empty
}
function Check([string]$what, [bool]$ok) { $script:results += "$(if ($ok) {'PASS'} else {'FAIL'})  $what"; Write-Output $script:results[-1] }
function Save([string]$name) {
    foreach ($f in 'card', 'tiles') {
        $src = Join-Path $stateDir "$f.png"
        if (Test-Path $src) { Copy-Item $src (Join-Path $ShotDir "$name-$f.png") -Force }
    }
    try {
        $bmp = New-Object System.Drawing.Bitmap $screenW, $screenH
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size); $g.Dispose()
        $bmp.Save((Join-Path $ShotDir "$name-screen.png")); $bmp.Dispose()
    } catch { Write-Output "  (screen capture unavailable in this session; app renders saved)" }
}
# Takes a SCREEN point and lets the OS convert it to the card's client space, so we never
# have to assume the client origin equals the window's top-left.
function PostClick([IntPtr]$hwnd, [int]$sx, [int]$sy) {
    $p = New-Object U+POINT; $p.X = $sx; $p.Y = $sy; [void][U]::ScreenToClient($hwnd, [ref]$p)
    $lp = [IntPtr](($p.Y -shl 16) -bor ($p.X -band 0xFFFF))
    [void][U]::PostMessage($hwnd, 0x200, [IntPtr]0, $lp); Start-Sleep -Milliseconds 80   # WM_MOUSEMOVE
    [void][U]::PostMessage($hwnd, 0x201, [IntPtr]1, $lp); Start-Sleep -Milliseconds 80   # WM_LBUTTONDOWN
    [void][U]::PostMessage($hwnd, 0x202, [IntPtr]0, $lp)                                 # WM_LBUTTONUP
}
function StopGlance { & $ExePath --quit; Start-Sleep 1; Get-Process Glance -ErrorAction SilentlyContinue | Stop-Process -Force; Remove-Item $dump -ErrorAction SilentlyContinue }

StopGlance

# ---- A. hover ---------------------------------------------------------------------------------
Write-Output '== A. hover'
Start-Process $ExePath -ArgumentList '--preview' | Out-Null
Start-Sleep 4
$ram = DumpRect 'ram'; $cpu = DumpRect 'cpu'
Write-Output "memory tile $ram   cpu tile $cpu"
Check 'Glance is running and reporting state' ((ReadDump).Length -gt 0)
Check 'card starts closed' (-not (DumpVisible))
Check 'both tiles are in the visible tray (not the overflow)' ((-not $ram.IsEmpty) -and ($ram -ne $cpu))
if (-not $ram.IsEmpty) {
    [void][U]::SetCursorPos($ram.X + [int]($ram.Width / 2), $ram.Y + [int]($ram.Height / 2))
    Start-Sleep -Milliseconds 1800
    $opened = DumpVisible
    Check 'hovering the memory tile opens the card' $opened
    if ($opened) { Save 'A1-hover-open' }
    [void][U]::SetCursorPos([int]($screenW / 2), [int]($screenH / 3))
    Start-Sleep -Milliseconds 1800
    Check 'moving away closes the card' (-not (DumpVisible))
}
StopGlance

# ---- B. end an app ----------------------------------------------------------------------------
Write-Output '== B. end'
$hogExe = Join-Path $PSScriptRoot 'Hog\dist\GlanceHog.exe'
if (-not (Test-Path $hogExe)) { throw "publish the hog first (see README, Building)" }
$hog = Start-Process $hogExe -ArgumentList '3' -WindowStyle Hidden -PassThru
Write-Output "hog pid $($hog.Id)"
Start-Sleep 5
Start-Process $ExePath -ArgumentList '--preview', '--pinned' | Out-Null
Start-Sleep 5
$bounds = DumpRect 'bounds'
Check 'pinned card is open' ((DumpVisible) -and (-not $bounds.IsEmpty))
Save 'B1-card-open'
$lines = (ReadDump) -split "`r?`n"
$hogLine = $lines | Where-Object { $_ -match "^\d+\|[^|]*\|[^|]*\|(\d+,)*$($hog.Id)(,\d+)*\|" } | Select-Object -First 1
Check 'memory hog shows up in the top ten' ($null -ne $hogLine)
$hwnd = [U]::FindWindow([IntPtr]::Zero, 'Glance')
Check 'card window found' ($hwnd -ne [IntPtr]::Zero)
if ($hogLine -and $hwnd -ne [IntPtr]::Zero) {
    Write-Output "hog row: $hogLine"
    $btn = ParseRect ($hogLine -replace '.*btn=', '')
    $sx = $btn.X + [int]($btn.Width / 2)
    $sy = $btn.Y + [int]($btn.Height / 2)
    PostClick $hwnd $sx $sy
    Start-Sleep -Milliseconds 1700
    Save 'B2-end-armed'
    $armed = (Get-Content (Join-Path $ShotDir 'B2-end-armed-card.png') -ErrorAction SilentlyContinue) -ne $null
    PostClick $hwnd $sx $sy
    Start-Sleep 2
    $gone = $null -eq (Get-Process -Id $hog.Id -ErrorAction SilentlyContinue)
    Check 'two clicks on End ended the hog' $gone
    Save 'B3-after-end'
}
if (Get-Process -Id $hog.Id -ErrorAction SilentlyContinue) { Stop-Process -Id $hog.Id -Force }
StopGlance

# ---- done: leave a normal instance running -----------------------------------------------------
Start-Process $ExePath | Out-Null
Write-Output ''
Write-Output '==== results ===='
$results | ForEach-Object { Write-Output $_ }
