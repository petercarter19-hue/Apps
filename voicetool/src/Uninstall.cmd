@echo off
rem Removes VoiceTool from this computer. It asks before it deletes anything, and asks whether to keep your words and fixes.
title VoiceTool uninstall
rem Run a copy of the script from the Temp folder, because this folder is about to be deleted.
set "VT_HERE=%~dp0"
copy /y "%VT_HERE%uninstall.ps1" "%TEMP%\voicetool-uninstall.ps1" >nul
cd /d "%TEMP%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\voicetool-uninstall.ps1" -InstallDir "%VT_HERE:~0,-1%" %* & exit
