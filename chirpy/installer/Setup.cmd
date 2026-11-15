@echo off
setlocal
title Chirpy setup
if not exist "%~dp0setup.ps1" (
    echo Please extract the whole package first, then run Setup in the extracted folder.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set "VT_EXIT=%errorlevel%"
if not "%VT_EXIT%"=="0" (
    echo.
    echo Setup did not finish. Read the message above.
    pause
)
exit /b %VT_EXIT%
