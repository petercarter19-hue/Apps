# Release checks for the new Glance build, run from a test folder. No real mouse moves, no screen pictures,
# and End only ever touches the test hogs. Restores the user's own settings at the end.
# Usage: tools\release-test.ps1 [-Exe <Glance.exe>] [-HogDir <folder with GlanceHog.exe>]. Publish tools\Hog into tools\Hog\dist first.
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\Glance.exe'),
    [string]$HogDir = (Join-Path $PSScriptRoot 'Hog\dist')
)
$Exe = (Resolve-Path $Exe).Path
if (-not (Test-Path (Join-Path $HogDir 'GlanceHogKid.exe'))) { Copy-Item (Join-Path $HogDir 'GlanceHog.exe') (Join-Path $HogDir 'GlanceHogKid.exe') }
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class T {
  public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr h, uint f);
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(int a, bool i, int pid);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
  public static long Created(int pid) { IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return 0;
    long c, e, k, u; GetProcessTimes(h, out c, out e, out k, out u); CloseHandle(h); return c; }
}
"@
[void][T]::SetProcessDpiAwarenessContext([IntPtr](-4))
$state = Join-Path $env:LOCALAPPDATA 'Glance'
$settings = Join-Path $state 'settings.json'
$dump = Join-Path $state 'preview.txt'
$log = Join-Path $state 'log.txt'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glance.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$results = @()
function Check([string]$what, [bool]$ok) { $script:results += "$(if ($ok) {'PASS'} else {'FAIL'})  $what"; Write-Output $script:results[-1] }
function Glances { @(Get-Process Glance -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId }) }
function StopAll { & $Exe --quit; Start-Sleep 2; Glances | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep 1 }
function ReadDump { if (Test-Path $dump) { return (Get-Content $dump -Raw) } return '' }
function DumpRect([string]$name) {
    if ((ReadDump) -match "$name=\{X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)\}") {
        return New-Object System.Drawing.Rectangle ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3]), ([int]$Matches[4]) }
    return [System.Drawing.Rectangle]::Empty
}
function PostClick([IntPtr]$hwnd, [int]$sx, [int]$sy) {
    $p = New-Object T+POINT; $p.X = $sx; $p.Y = $sy; [void][T]::ScreenToClient($hwnd, [ref]$p)
    $lp = [IntPtr](($p.Y -shl 16) -bor ($p.X -band 0xFFFF))
    [void][T]::PostMessage($hwnd, 0x200, [IntPtr]0, $lp); Start-Sleep -Milliseconds 80
    [void][T]::PostMessage($hwnd, 0x201, [IntPtr]1, $lp); Start-Sleep -Milliseconds 80
    [void][T]::PostMessage($hwnd, 0x202, [IntPtr]0, $lp)
}

# Keep the user's own settings to put back at the end.
$savedSettings = if (Test-Path $settings) { Get-Content $settings -Raw } else { $null }
$logBefore = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
StopAll

# ---- 1. first run on a PC: settings created, Start with Windows on, Start menu entry made --------------
Write-Output '== 1. first run'
Remove-Item $settings -ErrorAction SilentlyContinue
Remove-Item $lnk -ErrorAction SilentlyContinue
Remove-ItemProperty $runKey -Name Glance -ErrorAction SilentlyContinue
Start-Process $Exe | Out-Null
Start-Sleep 4
Check 'first run: Glance is running' ((Glances).Count -eq 1)
Check 'first run: settings file created' (Test-Path $settings)
$run = (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).Glance
Check "first run: Start with Windows turned on ($run)" ($run -eq "`"$Exe`"")
$sh = New-Object -ComObject WScript.Shell
$target = if (Test-Path $lnk) { $sh.CreateShortcut($lnk).TargetPath } else { '' }
Check "first run: Start menu entry points at this copy ($target)" ($target -eq $Exe)

# ---- 2. opening it again restarts it, and only one copy is left ----------------------------------------
Write-Output '== 2. open again = restart'
$a = (Glances)[0].Id
Start-Process $Exe | Out-Null
Start-Sleep 5
$now = Glances
Check "restart: the old copy ($a) quit" ($null -eq (Get-Process -Id $a -ErrorAction SilentlyContinue))
Check "restart: exactly one copy running ($($now.Id -join ','))" ($now.Count -eq 1)
Start-Sleep 5
Check 'restart: the new copy is still running 10 s later' ((Glances).Count -eq 1)
Start-Process $Exe | Out-Null; Start-Sleep 5
Check 'restart twice in a row: still exactly one copy' ((Glances).Count -eq 1)

# ---- 3. --quit -------------------------------------------------------------------------------------------
Write-Output '== 3. quit'
& $Exe --quit; Start-Sleep 3
Check '--quit closes it' ((Glances).Count -eq 0)

# ---- 4. a moved copy fixes its startup entry ---------------------------------------------------------
Write-Output '== 4. moved file'
Set-ItemProperty $runKey -Name Glance -Value '"C:\Old\Place\Glance.exe"'
Start-Process $Exe | Out-Null; Start-Sleep 4
$run = (Get-ItemProperty $runKey).Glance
Check "startup entry repointed to this copy ($run)" ($run -eq "`"$Exe`"")
StopAll

# ---- 5. "As admin" helper ends only the exact process it was given ---------------------------------
Write-Output '== 5. --end checks the start time'
$hog = Start-Process (Join-Path $HogDir 'GlanceHog.exe') -ArgumentList '1' -WindowStyle Hidden -PassThru
Start-Sleep 3
$created = [T]::Created($hog.Id)
$p = Start-Process $Exe -ArgumentList '--end', "$($hog.Id):$($created + 1)" -PassThru -Wait
Check "wrong start time: hog left alone (exit $($p.ExitCode))" ($null -ne (Get-Process -Id $hog.Id -ErrorAction SilentlyContinue))
$p = Start-Process $Exe -ArgumentList '--end', "$($hog.Id):$created" -PassThru -Wait
Start-Sleep 1
Check "right start time: hog ended (exit $($p.ExitCode))" ($null -eq (Get-Process -Id $hog.Id -ErrorAction SilentlyContinue))
Check '--end did not leave a Glance running' ((Glances).Count -eq 0)

# ---- 6. two clicks on End end the app and the child it started --------------------------------------
Write-Output '== 6. End takes the children'
$hog = Start-Process (Join-Path $HogDir 'GlanceHog.exe') -ArgumentList '3', '--child', (Join-Path $HogDir 'GlanceHogKid.exe') -WindowStyle Hidden -PassThru
Start-Sleep 6
$kid = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($hog.Id)" | Where-Object Name -eq 'GlanceHogKid.exe' | Select-Object -First 1
Check "test hog $($hog.Id) started its child $($kid.ProcessId)" ($null -ne $kid)
Start-Process $Exe -ArgumentList '--preview', '--pinned' | Out-Null
Start-Sleep 5
$lines = (ReadDump) -split "`r?`n"
$hogLine = $lines | Where-Object { $_ -match "^\d+\|[^|]*\|[^|]*\|(\d+,)*$($hog.Id)(,\d+)*\|" } | Select-Object -First 1
Check 'hog row is in the top ten' ($null -ne $hogLine)
$hwnd = [T]::FindWindow([IntPtr]::Zero, 'Glance')
if ($hogLine -and $hwnd -ne [IntPtr]::Zero) {
    if (($hogLine -replace '.*btn=', '') -match 'X=(-?\d+),Y=(-?\d+),Width=(\d+),Height=(\d+)') {
        $sx = [int]$Matches[1] + [int]([int]$Matches[3] / 2); $sy = [int]$Matches[2] + [int]([int]$Matches[4] / 2)
        PostClick $hwnd $sx $sy; Start-Sleep -Milliseconds 800
        Check 'one click does not end it' ($null -ne (Get-Process -Id $hog.Id -ErrorAction SilentlyContinue))
        PostClick $hwnd $sx $sy; Start-Sleep 2
        Check 'two clicks ended the hog' ($null -eq (Get-Process -Id $hog.Id -ErrorAction SilentlyContinue))
        Check 'and its child' ($null -eq (Get-Process -Id $kid.ProcessId -ErrorAction SilentlyContinue))
        Check 'Glance is still running after End' ((Glances).Count -eq 1)
    }
}
Get-Process GlanceHog, GlanceHogKid -ErrorAction SilentlyContinue | Stop-Process -Force

# ---- 7. no handle leak over a minute with the card open --------------------------------------------
Write-Output '== 7. leak check (60 s, card open)'
$g = (Glances)[0]
$gdi0 = [T]::GetGuiResources($g.Handle, 0); $h0 = $g.HandleCount
Start-Sleep 60
$g.Refresh()
$gdi1 = [T]::GetGuiResources($g.Handle, 0); $h1 = $g.HandleCount
Check "graphics handles steady over 60 s ($gdi0 -> $gdi1)" (($gdi1 - $gdi0) -lt 20)
Check "process handles steady over 60 s ($h0 -> $h1)" (($h1 - $h0) -lt 50)
StopAll

# ---- 8. no errors logged ---------------------------------------------------------------------------
$logAfter = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
Check 'no new errors in the log' ($logAfter -eq $logBefore)
if ($logAfter -gt $logBefore) { Get-Content $log -Tail 20 }

# Put the user's settings back.
if ($savedSettings) { Set-Content $settings $savedSettings -NoNewline -Encoding UTF8 }
Remove-Item $dump, (Join-Path $state 'card.png'), (Join-Path $state 'tiles.png') -ErrorAction SilentlyContinue
Write-Output ''
Write-Output '==== results ===='
$results | ForEach-Object { Write-Output $_ }
