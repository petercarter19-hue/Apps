# Proves that tools\safety-lint.ps1 really catches what it says it catches. It writes small made-up scripts into a temporary
# folder (never into tools\), runs the lint on that folder, and checks that every bad one is flagged and a clean one passes.
# Nothing here runs the made-up scripts; the lint only reads them. Exit code = number of failed checks.
# Usage: tools\safety-lint-selftest.ps1
$lint = Join-Path $PSScriptRoot 'safety-lint.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('glance-lint-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$failed = 0
function Expect([string]$what, [bool]$ok) { Write-Output "$(if ($ok) { 'PASS' } else { 'FAIL' })  $what"; if (-not $ok) { $script:failed++ } }
function Lint { $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $lint -Root $root 2>&1 | Out-String; return [pscustomobject]@{ Out = $out; Code = $LASTEXITCODE } }

try {
    New-Item -ItemType Directory -Force (Join-Path $root 'tools'), (Join-Path $root 'src') | Out-Null
    Set-Content (Join-Path $root 'src\a.cs') 'class A { }'
    # A TestLib that checks window ownership before posting (allowed).
    Set-Content (Join-Path $root 'tools\TestLib.ps1') @'
function Send-Good($t, $hwnd) {
    if (-not (Test-WindowOwned $t $hwnd)) { return }
    [X]::PostMessage($hwnd, 1, 0, 0)
}
'@
    Set-Content (Join-Path $root 'tools\good.ps1') ". (Join-Path `$PSScriptRoot 'TestLib.ps1')`nWrite-Output 'fine'"
    $clean = Lint
    Expect 'a clean tree passes' ($clean.Code -eq 0)

    $bad = [ordered]@{
        'cursor-assign.ps1'  = '[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point 1, 1'
        'cursor-assign2.ps1' = '[Windows.Forms.Cursor]::Position=$p'
        'capture.ps1'        = '$g.CopyFromScreen(0,0,0,0,$s)'
        'sendinput.ps1'      = '[W]::SendInput(1,$i,4)'
        'cim-method.ps1'     = 'Invoke-CimMethod -InputObject $p -MethodName Terminate'
        'wmi-terminate.ps1'  = '(Get-WmiObject Win32_Process -Filter "Name=''x.exe''").Terminate()'
        'appdata.ps1'        = '$x = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Glance.lnk"'
        'localappdata.ps1'   = '$x = Join-Path $env:LOCALAPPDATA "Glance"'
        'startmenu.ps1'      = '$x = "C:\Users\me\AppData\Roaming\Microsoft\Windows\Start Menu\Programs"'
        'post-by-hand.ps1'   = '[GlanceTestWin]::PostMessage($anyWindow, 0x201, 0, 0)'
        'findwindow.ps1'     = '$h = [GlanceTestWin]::FindWindow([IntPtr]::Zero, "Glance")'
        'stop-by-name.ps1'   = 'Get-Process Glance | Stop-Process -Force'
        'taskkill.ps1'       = 'taskkill /im Glance.exe /f'
        'start-direct.ps1'   = 'Start-Process $Exe'
        'runkey.ps1'         = 'Set-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name Glance -Value x'
    }
    foreach ($name in $bad.Keys) { Set-Content (Join-Path $root "tools\$name") $bad[$name] }
    # TestLib posting without checking ownership is flagged too.
    Add-Content (Join-Path $root 'tools\TestLib.ps1') "`nfunction Send-Bad(`$t, `$hwnd) {`n    [X]::PostMessage(`$hwnd, 1, 0, 0)`n}`nfunction Sneaky { [X]::PostMessage(1, 1, 0, 0) }`n"
    $r = Lint
    Expect 'a tree with bad scripts fails' ($r.Code -gt 0)
    foreach ($name in $bad.Keys) { Expect "flags $name" ($r.Out -match [regex]::Escape($name)) }
    Expect 'flags a Send-* helper that skips the ownership check' ($r.Out -match 'Send-Bad posts a window message without checking Test-WindowOwned')
    Expect 'flags a non-Send helper that posts messages' ($r.Out -match 'Sneaky posts a window message')
    Expect 'does not flag the good helper' ($r.Out -notmatch 'Send-Good')
    Expect 'does not flag good.ps1' ($r.Out -notmatch 'good\.ps1')

    # A disposable-VM script without the Windows Sandbox guard is flagged; with it, it is allowed to do desktop things.
    $vm = Join-Path $root 'tools\disposable-vm-only'
    New-Item -ItemType Directory -Force $vm | Out-Null
    Set-Content (Join-Path $vm 'unguarded.ps1') '[W]::SetCursorPos(1,1)'
    Set-Content (Join-Path $vm 'guarded.ps1') "if (`$who -notmatch 'WDAGUtilityAccount') { exit 3 }`n[W]::SetCursorPos(1,1)"
    $r2 = Lint
    Expect 'flags a disposable-VM script with no Sandbox guard' ($r2.Out -match 'unguarded\.ps1.*no Windows Sandbox guard')
    Expect 'allows desktop calls inside a guarded disposable-VM script' ($r2.Out -notmatch 'guarded\.ps1:[0-9]+')
}
finally {
    $tmp = [IO.Path]::GetTempPath()
    if ($root.StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $root -Leaf) -like 'glance-lint-selftest-*' -and (Test-Path $root)) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Output "$failed failed."
exit $failed
