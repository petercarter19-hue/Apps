

import os
import subprocess
import sys
from pathlib import Path

TITLE = "Chirpy"
STARTED = {"yes": False}

COULDNT_START = (
    "Chirpy couldn't start.\n\n"
    "A file Chirpy needs may be missing or damaged. Antivirus software sometimes removes files like these.\n\n"
    "To fix it, run Chirpy-Setup again (or Setup.cmd, if that is how you installed Chirpy). It puts the files back and "
    "keeps your words, fixes, history and settings."
)
STOPPED = (
    "Chirpy stopped.\n\n"
    "Start it again from the Start menu. If it keeps stopping, run Chirpy-Setup again (or Setup.cmd, if that is how you "
    "installed Chirpy). It puts the files back and keeps your words, fixes, history and settings."
)

def explain(error):

    detail = " ".join(str(error).split())
    if "Original error was:" in detail:
        detail = detail.split("Original error was:")[-1].strip()
    said = f"{type(error).__name__}: {detail}"
    if len(said) > 300:
        said = said[:297] + "..."
    return (STOPPED if STARTED["yes"] else COULDNT_START) + "\n\nWhat was reported: " + said

def say(text):

    target = os.environ.get("CHIRPY_MESSAGES_FILE")
    if target:
        with open(target, "a", encoding="utf-8") as f:
            f.write(text + "\n---\n")
        return
    code = "import ctypes, sys; ctypes.windll.user32.MessageBoxW(None, sys.argv[1], sys.argv[2], 0x10 | 0x10000)"
    try:
        pythonw = Path(sys.executable).with_name("pythonw.exe")
        subprocess.Popen([str(pythonw), "-s", "-E", "-c", code, text, TITLE], creationflags=0x08000000)
    except Exception:
        try:
            import ctypes
            ctypes.windll.user32.MessageBoxW(None, text, TITLE, 0x10 | 0x10000)
        except Exception:
            pass

def started():

    STARTED["yes"] = True

def _hook(kind, value, trace):
    try:
        sys.__excepthook__(kind, value, trace)
    except Exception:
        pass
    if issubclass(kind, (KeyboardInterrupt, SystemExit)):
        return
    try:
        say(explain(value))
    except Exception:
        pass

def install():
    sys.excepthook = _hook
