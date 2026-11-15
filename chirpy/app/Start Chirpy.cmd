@echo off
rem Starts Chirpy in the background (no window). It beeps twice when it's ready (about 10 seconds).
start "" "%~dp0python\pythonw.exe" -s -E "%~dp0voicetool.py" %*
