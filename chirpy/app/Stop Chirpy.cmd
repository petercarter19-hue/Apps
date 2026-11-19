@echo off
rem Stops Chirpy and its window (only the copy in this folder).
set "CHIRPY_FOLDER=%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$here = $env:CHIRPY_FOLDER.TrimEnd('\'); Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'python.exe','pythonw.exe' -and $_.ExecutablePath -and $_.ExecutablePath.StartsWith($here + '\python\', [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
