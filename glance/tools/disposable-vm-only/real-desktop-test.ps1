# DISPOSABLE WINDOWS SANDBOX ONLY. This moves the mouse and captures the screen. Never run it on your own PC.
#
# It checks the one thing the safe tests can't: that REAL pointer hover opens the card and moving away closes it, and it saves
# a picture of the card region of the real screen. Both need a real pointer and a real screen, which is exactly why they are
# kept out of tools\ and refuse to run anywhere but Windows Sandbox (a throwaway Windows that is deleted when you close it,
# whose only account is WDAGUtilityAccount).
#
# To use it: turn on Windows Sandbox (Windows Features), start it, copy the glance folder and a built Glance.exe in, and run
#   powershell -ExecutionPolicy Bypass -File tools\disposable-vm-only\real-desktop-test.ps1 -ThisMovesTheMouseAndCapturesTheScreen
# Nothing in tools\ runs this for you.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\..\dist\Glance.exe'),
    [switch]$ThisMovesTheMouseAndCapturesTheScreen
)
# Guard first, before anything else is loaded or run. It asks Windows who is signed in (the account name itself), not an
# environment variable, which anything could set.
$whoAmI = ''
try { $whoAmI = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name } catch { }
if ($whoAmI -notmatch '(^|\\)WDAGUtilityAccount$') {
    Write-Output 'Refusing to run: this test moves the mouse and captures the screen, and it only runs inside Windows Sandbox.'
    Write-Output 'On your own PC use tools\release-test.ps1, message-test.ps1, hover-test.ps1 and smoke-test.ps1 instead: they never touch your pointer or screen.'
    exit 3
}
if (-not $ThisMovesTheMouseAndCapturesTheScreen) {
    Write-Output 'Refusing to run without -ThisMovesTheMouseAndCapturesTheScreen.'
    exit 3
}

Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot '..\TestLib.ps1')
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class SandboxDesktop {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
}
"@
$screenW = [SandboxDesktop]::GetSystemMetrics(0); $screenH = [SandboxDesktop]::GetSystemMetrics(1)
$t = New-GlanceTest -Exe $ExePath
try {
    $app = Start-GlanceTestApp $t @('--preview')
    Start-Sleep 4
    $ram = Get-PreviewRect $t 'ram'
    Check 'both tiles are in the visible tray' (-not $ram.IsEmpty)
    if (-not $ram.IsEmpty) {
        [void][SandboxDesktop]::SetCursorPos($ram.X + [int]($ram.Width / 2), $ram.Y + [int]($ram.Height / 2))
        Start-Sleep -Milliseconds 1800
        $opened = Test-PreviewVisible $t
        Check 'hovering the memory tile with the real pointer opens the card' $opened
        if ($opened) {
            $b = Get-PreviewRect $t 'bounds'
            $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size); $g.Dispose()
            $out = Join-Path $t.StateDir 'real-screen-card.png'
            $bmp.Save($out); $bmp.Dispose()
            Write-Output "saved a capture of the real screen at $out"
        }
        [void][SandboxDesktop]::SetCursorPos([int]($screenW / 2), [int]($screenH / 3))
        Start-Sleep -Milliseconds 1800
        Check 'moving the real pointer away closes the card' (-not (Test-PreviewVisible $t))
    }
    Stop-GlanceTestApp $t $app
}
finally { Remove-GlanceTest $t }
Write-TestSummary
exit $script:Failed
