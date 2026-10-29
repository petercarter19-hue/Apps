# Hover check that works even while the tiles still sit in the hidden-icons overflow:
# opens the overflow flyout through UI Automation (no synthetic input needed), reads the memory tile's
# on-screen rectangle from Glance's --preview dump, moves the cursor onto it, and checks the card opens.
param([string]$ExePath = (Join-Path $PSScriptRoot '..\dist\Glance.exe'))
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U2 {
  public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, IntPtr title);
}
"@
[void][U2]::SetProcessDPIAware()
$dump = Join-Path $env:LOCALAPPDATA 'Glance\preview.txt'
function ReadDump { if (Test-Path $dump) { return (Get-Content $dump -Raw) } return '' }
function DumpVisible { return ((ReadDump) -match 'visible=True') }
function DumpRect([string]$name) {
    if ((ReadDump) -match "$name=\{X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)\}") {
        return New-Object System.Drawing.Rectangle ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3]), ([int]$Matches[4])
    }
    return [System.Drawing.Rectangle]::Empty
}

# 0. can the cursor be moved at all in this session?
[void][U2]::SetCursorPos(400, 400); Start-Sleep -Milliseconds 100
$p = New-Object U2+POINT; [void][U2]::GetCursorPos([ref]$p)
Write-Output "cursor after SetCursorPos(400,400): $($p.X),$($p.Y)  -> $(if ($p.X -eq 400 -and $p.Y -eq 400) {'cursor control works'} else {'cursor control NOT working'})"

# 1. Glance in preview mode, card closed
& $ExePath --quit; Start-Sleep 1
Get-Process Glance -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $dump -ErrorAction SilentlyContinue
Start-Process $ExePath -ArgumentList '--preview' | Out-Null
Start-Sleep 4
Write-Output "before: ram=$(DumpRect 'ram') visible=$(DumpVisible)"

# 2. open the overflow flyout via UI Automation
$tray = [U2]::FindWindow('Shell_TrayWnd', [IntPtr]::Zero)
$trayEl = [System.Windows.Automation.AutomationElement]::FromHandle($tray)
$cond = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), 'Show Hidden Icons'
$chev = $trayEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($null -eq $chev) {
    $cond2 = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), 'SystemTrayIcon'
    $chev = $trayEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond2)
}
Write-Output "overflow chevron found: $($null -ne $chev)"
if ($chev) {
    try { $chev.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { Write-Output "invoke failed: $($_.Exception.Message)" }
    Start-Sleep -Milliseconds 1500
}
$ram = DumpRect 'ram'; $cpu = DumpRect 'cpu'
Write-Output "after opening overflow: ram=$ram cpu=$cpu"

# 3. hover the memory tile
if (-not $ram.IsEmpty) {
    [void][U2]::SetCursorPos($ram.X + [int]($ram.Width / 2), $ram.Y + [int]($ram.Height / 2))
    Start-Sleep -Milliseconds 2000
    $opened = DumpVisible
    Write-Output "$(if ($opened) {'PASS'} else {'FAIL'})  hovering the memory tile opens the card"
    if ($opened) { Copy-Item (Join-Path $env:LOCALAPPDATA 'Glance\card.png') (Join-Path $PSScriptRoot 'shots\A1-hover-open-card.png') -Force }
    [void][U2]::SetCursorPos(600, 300)
    Start-Sleep -Milliseconds 2000
    Write-Output "$(if (-not (DumpVisible)) {'PASS'} else {'FAIL'})  moving away closes the card"
}

# 4. back to a normal instance
& $ExePath --quit; Start-Sleep 1
Get-Process Glance -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process $ExePath | Out-Null
