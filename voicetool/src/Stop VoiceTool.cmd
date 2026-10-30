@echo off
rem Stops VoiceTool, the VoiceTool window, the tidy-up model's server, and the Claude helper VoiceTool keeps running (only if
rem you turned Claude on). Only programs that run from this VoiceTool folder are stopped (a folder whose name merely
rem starts the same is left alone, and so is any other Claude).
set "VT_DIR=%~dp0"
powershell -NoProfile -Command "$d = $env:VT_DIR.TrimEnd('\') + '\'; Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and ($_.Name -in 'pythonw.exe','python.exe','llama-server.exe') -and $_.CommandLine.IndexOf($d, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; Get-CimInstance Win32_Process -Filter \"Name='claude.exe'\" | Where-Object { $_.CommandLine -like '*stream-json*' -and $_.CommandLine -like '*mcp__claude_ai_*' -and $_.CommandLine.IndexOf($d + 'claude_workdir', [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
