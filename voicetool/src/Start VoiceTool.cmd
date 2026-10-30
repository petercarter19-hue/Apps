@echo off
rem Starts VoiceTool in the background (no window). It beeps twice when ready (about 10 seconds).
start "" "%~dp0.venv\Scripts\pythonw.exe" "%~dp0voicetool.py"
