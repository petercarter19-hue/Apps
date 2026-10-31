# Builds PhotoTwins as ONE file, tests it, and swaps it in. Safe to run again for every new version.
#
#   1. builds the new version in its own folder (dist-next) and never touches the installed copy yet
#   2. checks the new file: it starts, reads a picture with its built-in reader, and draws its own screen
#      (this also reads the Windows folder list, but never opens a photo and never touches your settings)
#   3. asks a running copy to close (it stops and saves first); waits; never kills anything
#   4. swaps the new file into app\PhotoTwins.exe in this folder (the old one is kept once as PhotoTwins.previous.exe)
#   5. makes sure the Start menu entry "PhotoTwins" points at that one file
#   6. checks the installed file the same way
#
# Run the unit tests first:  dotnet test tests\PhotoTwins.Tests
#
#   -DontClose  never asks a running copy to close: if PhotoTwins is open, the new file is built and checked in dist-next
#               and the swap is left for later (run again without it once the window is closed)
param([switch]$DontClose)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$next = Join-Path $root 'dist-next'
$appDir = Join-Path $root 'app'
$exe = Join-Path $appDir 'PhotoTwins.exe'
$check = Join-Path $root 'test-work\install-check'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\PhotoTwins.lnk'

# scratch and temp files stay next to the app (on the same drive), in the git-ignored test-work folder
$scratch = Join-Path $root 'test-work\tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TMP = $scratch; $env:TEMP = $scratch

function Test-Build([string]$file) {
    New-Item -ItemType Directory -Force $check | Out-Null
    $p = Start-Process -FilePath $file -ArgumentList @('--selfcheck', (Join-Path $check 'data'), (Join-Path $check 'screen.png')) -PassThru -Wait -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { throw "The self-check of $file failed (exit code $($p.ExitCode)). Nothing was changed." }
    Write-Host "  self-check passed: $file"
}

Write-Host '1. Building the new version...'
if (Test-Path $next) { Get-ChildItem $next -Recurse -File | Remove-Item -Force }
& dotnet publish (Join-Path $root 'src\PhotoTwins\PhotoTwins.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $next | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The build failed. Nothing was changed.' }
$newExe = Join-Path $next 'PhotoTwins.exe'
Write-Host ('  built: {0} ({1:N1} MB)' -f $newExe, ((Get-Item $newExe).Length / 1MB))

Write-Host '2. Checking the new file...'
Test-Build $newExe

if ($DontClose -and (Get-Process -Name PhotoTwins -ErrorAction SilentlyContinue)) {
    Write-Host ''
    Write-Host 'PhotoTwins is open right now, so it was NOT closed and nothing was swapped.'
    Write-Host "The new version is built and checked: $newExe"
    Write-Host 'Close PhotoTwins, then run this again to put it in place.'
    exit 0
}

if (-not $DontClose) {
    Write-Host '3. Closing a running copy (if any)...'
    if (Test-Path $exe) { Start-Process -FilePath $exe -ArgumentList '--quit' -Wait -WindowStyle Hidden }
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Process -Name PhotoTwins -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}
if (Get-Process -Name PhotoTwins -ErrorAction SilentlyContinue) { throw 'PhotoTwins is still running (maybe it is busy saving). Close it and run this again. Nothing was changed.' }

Write-Host '4. Swapping the new file in...'
New-Item -ItemType Directory -Force $appDir | Out-Null
if (Test-Path $exe) { Move-Item -LiteralPath $exe -Destination (Join-Path $appDir 'PhotoTwins.previous.exe') -Force }
Copy-Item -LiteralPath $newExe -Destination $exe -Force

Write-Host '5. Start menu entry...'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($startMenu)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $appDir
$lnk.Description = 'PhotoTwins: find duplicate photos and clear out the extra copies safely'
$lnk.IconLocation = "$exe,0"
$lnk.Save()
Write-Host "  $startMenu -> $exe"

Write-Host '6. Checking the installed file...'
Test-Build $exe
Write-Host ''
Write-Host "Done. PhotoTwins is installed: $exe"
Write-Host 'Open it from the Start menu (type PhotoTwins).'
