"""Adds 'VoiceTool' to the Start menu, with the blue-green microphone icon. Clicking it starts VoiceTool if it isn't
running, then opens the VoiceTool window (Home). Run once: .venv\\Scripts\\python.exe make_start_menu_app.py
(The shortcut is unchanged: it still runs voicetool.py --settings, which now means "open the VoiceTool window". Run this
again only to refresh the shortcut's tooltip.)"""
import os
from pathlib import Path

import win32com.client

from settings_window import icon_image

HERE = Path(__file__).resolve().parent


def main():
    ico = HERE / "voicetool.ico"
    icon_image().save(ico, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64)])
    programs = Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
    link = programs / "VoiceTool.lnk"
    shell = win32com.client.Dispatch("WScript.Shell")
    shortcut = shell.CreateShortCut(str(link))
    shortcut.TargetPath = str(HERE / ".venv" / "Scripts" / "pythonw.exe")
    shortcut.Arguments = f'"{HERE / "voicetool.py"}" --settings'
    shortcut.WorkingDirectory = str(HERE)
    shortcut.IconLocation = str(ico)
    shortcut.Description = "VoiceTool: hold Left Alt to talk. Opens the VoiceTool window."
    shortcut.save()
    print("Start menu app:", link)


if __name__ == "__main__":
    main()
