# Notes for the AI building this app

This folder is a finished, working Windows app: a voice app that types what the person says wherever their cursor is (Python 3.13, Windows 10 or 11, 64-bit). The app's code is in `src\`, its tests in `tools\`. The person you're helping may never have coded, so use plain words, short answers and sensible defaults, and tell them what you picked.

## Install it (the normal way)

1. From `src\`, run `Setup.cmd` (it runs `setup.ps1`). It needs `src\wheels\` with the one package PyPI has no wheel for; the folder must be complete, so don't run it from inside a zip.
2. Setup copies the app to `%LOCALAPPDATA%\VoiceTool`, installs Microsoft's Visual C++ Runtime if missing (Windows asks once), finds or installs Python 3.13 just for VoiceTool, makes a private `.venv` with the hash-pinned packages in `requirements-friend.txt`, downloads the models with `fetch_models.py` (about 3.2 GB, every file checked by SHA-256), writes this computer's settings into `config.json`, adds the Start menu entries and a sign-in task, and starts it.
3. Tell the person: hold Left Alt to talk and Left Ctrl for commands; the window opens from the Start menu. Running Setup again repairs an install without touching their words or settings. Uninstall is in the Start menu (`uninstall.ps1`).

`Start VoiceTool.cmd` and `Stop VoiceTool.cmd` in the install folder start and stop it. The log is `voicetool.log` in the install folder.

## Work on the code

1. Make a development environment in `src\` with Python 3.13:

   ```
   py -3.13 -m venv src\.venv
   src\.venv\Scripts\python.exe -m pip install --require-hashes --only-binary=:all: --no-cache-dir --find-links src\wheels -r src\requirements-friend.txt
   src\.venv\Scripts\python.exe -m pip install pytest
   ```

2. To run it from `src\` instead of the install, first fetch the models into `src\` with `src\.venv\Scripts\python.exe src\fetch_models.py`, then use `src\Start VoiceTool.cmd`. Quit the installed copy first (`Stop VoiceTool.cmd` in the install folder), so only one copy listens to the keys.

## Test it

From this folder:

- `src\.venv\Scripts\python.exe -m pytest tools` runs the Python tests (dictation, commands, learning, the window, meetings, the downloader). They work in throwaway folders. One check expects the temp folder on drive C:.
- `powershell -NoProfile -ExecutionPolicy Bypass -File tools\test_setup_functions.ps1` and `tools\test_uninstall.ps1` check Setup and Uninstall with throwaway folders, task names and shortcuts (`VoiceTool-unit*`). They need Python 3.13 in `%LOCALAPPDATA%\Programs\Python\Python313`.
- `<install>\.venv\Scripts\python.exe -I tools\dryrun_check.py <install folder>` checks an installed copy end to end: speech to text, tidy-up and a few commands, with nothing sent off the computer.

## Rules

- **Hands off the person's screen.** Tests never press real keys, move the mouse, take pictures of the real screen, change the volume or play sounds, and never send anything.
- **Their saved words are theirs.** `words.txt`, `corrections.json`, `style.md`, `styles.json`, `config.json` and `history.jsonl` in the install folder are the person's own. Never overwrite or empty them; back them up before a big change. Setup and Uninstall already keep them.
- **Never fail silently.** Problems show on the light ("Didn't catch that") or in the window, and go into `voicetool.log`.
- **Nothing leaves the computer** unless the person turns on an online option. Speech to text and the tidy-up model run locally; the model server listens on 127.0.0.1 only.
- **Spoken words are never run as commands.** Computer controls come from the fixed list in `system_actions.py`; opening things never runs programs or scripts.
- **Email and calendar are drafts only:** never send, never delete. They're off by default (`claude_actions` in `config.json`). When on, the finished code hands those jobs to the Claude Code command line (`claude -p`) signed in with the person's own plan and their Gmail or Outlook connection; if they use a different AI app, adapt `commands.py` to it.
- **Meeting notes need everyone's OK.** They're off by default and ask every time; the other people's audio is deleted once the notes are written.
- **Build new versions in a separate copy.** Stop the running copy first, and swap the new one in only when the tests pass.
- **To change something,** follow `KIT.md`, section 7 ("Make it your own"). Make one change at a time, and test it before the next.
