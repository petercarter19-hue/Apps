@echo off
rem VoiceTool setup. Double-click this file. It opens a window that shows progress; leave it open until it says VoiceTool is ready.
title VoiceTool setup
rem Started from inside the zip, Windows unpacks just this one file, so setup.ps1 isn't beside it. Say what to do instead.
if not exist "%~dp0setup.ps1" (
    echo.
    echo Please right-click the zip, choose Extract All, then double-click Setup in the extracted folder.
    echo.
    pause
    exit /b 1
)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set "VT_EXIT=%errorlevel%"
echo.
if not "%VT_EXIT%"=="0" echo Setup did not finish. Read the message above, then double-click Setup again; it picks up where it left off.
echo.
pause
exit /b %VT_EXIT%
