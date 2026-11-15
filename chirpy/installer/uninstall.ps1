# Removes Chirpy from the folder this file is in: stops it, removes its Start menu entry and its
# start-at-sign-in entry, then deletes the folder (your words, fixes and history in it too) after you say yes.
param([switch]$Yes)
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $dir '.voicetool-light-from-daily'))) { Write-Host 'This is not a Chirpy folder. Nothing was removed.'; exit 1 }
$answer = if ($Yes) { 'Y' } else { Read-Host "Remove Chirpy from $dir, with its words, fixes and history? Type Y to remove" }
if ($answer -ne 'Y' -and $answer -ne 'y') { Write-Host 'Nothing was removed.'; exit 0 }
Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in 'python.exe', 'pythonw.exe' -and $_.ExecutablePath -and
    $_.ExecutablePath.StartsWith($dir + '\python\', [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 1
$link = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Chirpy.lnk'
if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$value = (Get-ItemProperty -Path $run -Name 'Chirpy' -ErrorAction SilentlyContinue).'Chirpy'
if ($value -and $value -like "*$dir\*") { Remove-ItemProperty -Path $run -Name 'Chirpy' }
Set-Location $env:TEMP
$sounds = Join-Path $env:TEMP 'voicetool-light-sounds'
if (Test-Path -LiteralPath $sounds) { Remove-Item -LiteralPath $sounds -Recurse -Force }
Remove-Item -LiteralPath $dir -Recurse -Force
Write-Host 'Chirpy was removed.'
