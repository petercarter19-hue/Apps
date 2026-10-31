# Checks that the shipped test scripts keep Glance's promise: they never capture your screen, never move your mouse or press
# keys, never stop a program by name, and never touch your real Glance settings, startup entry or Start menu shortcut.
# Run it any time you add or change a script in tools\. It only reads files; it starts nothing. Exit code = number of problems.
#
# The one place those things are allowed is tools\disposable-vm-only\ (it refuses to run anywhere but Windows Sandbox, and
# nothing in tools\ runs it for you). TestLib.ps1 is allowed to stop processes and to read your real state, because that is
# where the "only exact processes the tests started" and "put your real state back" logic lives.
#
# Usage: tools\safety-lint.ps1 [-Root <the glance folder>]
param([string]$Root = (Join-Path $PSScriptRoot '..'))
$Root = (Resolve-Path $Root).Path
$problems = New-Object System.Collections.ArrayList

# Never allowed outside disposable-vm-only: reaching into the real desktop.
$desktop = @(
    'CopyFromScreen', 'SetCursorPos', 'mouse_event', 'SendInput', 'keybd_event', 'SendKeys', 'SetCursorPosition',
    'UIAutomation', 'InvokePattern', 'GetDesktopWindow', 'GetWindowDC', 'BitBlt', 'PrintWindow', 'SetForegroundWindow', 'GetCursorPos'
)
# Never allowed outside TestLib.ps1: stopping things, or touching real per-user state.
$process = @(
    @{ Name = 'Stop-Process'; Pattern = 'Stop-Process' },
    @{ Name = '.Kill()'; Pattern = '\.Kill\(' },
    @{ Name = 'taskkill'; Pattern = 'taskkill' },
    @{ Name = 'Get-Process by name'; Pattern = 'Get-Process\s+(-Name\s+)?[A-Za-z''"]' },
    @{ Name = 'starting a program outside TestLib.ps1 (go through Start-GlanceTestApp or Invoke-GlanceTestApp)'; Pattern = 'Start-Process|&\s*\$|\[(System\.)?Diagnostics\.Process\]::Start' },
    @{ Name = 'real LocalAppData'; Pattern = 'LOCALAPPDATA' },
    @{ Name = 'real startup (Run) key'; Pattern = 'CurrentVersion\\Run' },
    @{ Name = 'real Start menu folder'; Pattern = "GetFolderPath\('Programs'\)" }
)

function Code-Lines([string]$path) {
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        $n++
        $trim = $line.TrimStart()
        if ($trim.StartsWith('#') -or $trim.StartsWith('//')) { continue }
        [pscustomobject]@{ N = $n; Text = $line }
    }
}

$self = (Resolve-Path (Join-Path $Root 'tools\safety-lint.ps1') -ErrorAction SilentlyContinue)
$files = @()
$files += Get-ChildItem (Join-Path $Root 'tools') -Recurse -Include *.ps1, *.cs -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
$files += Get-ChildItem (Join-Path $Root 'src') -Filter *.cs -File
foreach ($f in $files) {
    if ($self -and $f.FullName -eq $self.Path) { continue }
    $rel = $f.FullName.Substring($Root.Length).TrimStart('\')
    $inVm = $rel -like 'tools\disposable-vm-only\*'
    $isLib = $rel -eq 'tools\TestLib.ps1'
    $isTestCs = $rel -like 'tools\Tests\*'
    $lines = @(Code-Lines $f.FullName)

    if ($inVm) {
        # Anything here must refuse to run outside Windows Sandbox before it does anything else.
        $text = ($lines | ForEach-Object { $_.Text }) -join "`n"
        if ($f.Extension -eq '.ps1' -and $text -notmatch 'WDAGUtilityAccount') {
            [void]$problems.Add("${rel}: has no Windows Sandbox guard (WDAGUtilityAccount)")
        }
        continue
    }
    foreach ($l in $lines) {
        foreach ($w in $desktop) {
            if ($w -eq 'SetForegroundWindow' -and $rel -like 'src\*') { continue }   # the app raising its own tray menu is fine; a test raising windows is not
            if ($l.Text -match [regex]::Escape($w)) { [void]$problems.Add("${rel}:$($l.N): reaches the real desktop ($w)") }
        }
        if ($isLib -or $isTestCs -or $rel -like 'src\*') { continue }   # the rules below are for the test scripts themselves
        foreach ($p in $process) {
            if ($l.Text -match $p.Pattern) { [void]$problems.Add("${rel}:$($l.N): $($p.Name)") }
        }
    }
}

# The disposable-VM scripts must not be called from anywhere else.
foreach ($f in ($files | Where-Object { $_.Extension -eq '.ps1' -and $_.FullName -notmatch '\\disposable-vm-only\\' })) {
    if ($self -and $f.FullName -eq $self.Path) { continue }
    foreach ($l in (Code-Lines $f.FullName)) {
        if ($l.Text -match 'disposable-vm-only') { [void]$problems.Add("$($f.Name):$($l.N): runs something from disposable-vm-only")}
    }
}

if ($problems.Count -eq 0) { Write-Output "PASS  no screen capture, cursor or key input, name-based stops, or real-state access outside the allowed places ($($files.Count) files checked)" }
else { foreach ($p in $problems) { Write-Output "FAIL  $p" }; Write-Output "$($problems.Count) problem(s)." }
exit $problems.Count
