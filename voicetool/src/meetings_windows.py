"""The Windows half of meetings.py (moved here unchanged from the PC's meetings.py, 2026-10-22):
the other side of the call (WASAPI loopback of every output), which app is using the mic, window titles,
and where the meeting notes go (Documents, never OneDrive). The Mac has meetings_mac.py with the same three functions."""
import os
import re
import time
import winreg
from pathlib import Path

import numpy as np

from meetings import CALL_WINDOWS, RATE


def start_system_audio(on_audio, log=None, is_running=lambda: True):
    """Mix what every output device is playing (the other people on the call) and hand it to on_audio in small
    blocks, until is_running() turns False. Runs on its own thread (started by the caller)."""
    import warnings
    import soundcard as sc
    warnings.filterwarnings("ignore", category=sc.SoundcardRuntimeWarning)
    recorders = []
    for speaker in sc.all_speakers():
        try:
            rec = sc.get_microphone(id=str(speaker.name), include_loopback=True).recorder(samplerate=RATE, channels=1)
            rec.__enter__()
            recorders.append(rec)
        except Exception as e:
            if log:
                log.info("loopback skipped for %s: %s", speaker.name, e)
    try:
        while is_running() and recorders:
            parts = []
            for rec in recorders:
                try:
                    parts.append(rec.record(numframes=RATE // 5)[:, 0])
                except Exception:
                    pass
            if parts:
                n = min(len(p) for p in parts)
                on_audio(np.clip(sum(p[:n] for p in parts), -1, 1))
    finally:
        for rec in recorders:
            try:
                rec.__exit__(None, None, None)
            except Exception:
                pass


CALL_APPS = {"msteams": "Teams", "microsoftteams": "Teams", "zoom": "Zoom", "webex": "Webex", "ciscocollab": "Webex",
             "slack": "Slack", "discord": "Discord", "skype": "Skype"}
MIC_STORE = r"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"


def _apps_using_mic():
    """Names of apps Windows says are using the microphone right now."""
    found = []

    def scan(path):
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, path)
        except OSError:
            return
        i = 0
        while True:
            try:
                name = winreg.EnumKey(key, i)
            except OSError:
                break
            i += 1
            if name == "NonPackaged":
                continue
            try:
                sub = winreg.OpenKey(key, name)
                start = winreg.QueryValueEx(sub, "LastUsedTimeStart")[0]
                stop = winreg.QueryValueEx(sub, "LastUsedTimeStop")[0]
                if start and not stop:
                    found.append(name.lower())
            except OSError:
                pass

    scan(MIC_STORE)
    scan(MIC_STORE + r"\NonPackaged")
    return found


# This PC's Windows only records Store apps (like new Teams) in the microphone list, not desktop apps like Zoom or
# browsers, so those are recognized by their meeting window instead.
_UNUSED_CALL_WINDOWS = [(re.compile(r"^Zoom Meeting|^Zoom Webinar|Zoom Workplace.*Meeting", re.I), "Zoom"),
                (re.compile(r"^Meet [-–] |Google Meet", re.I), "Google Meet"),
                (re.compile(r"Webex Meeting|Cisco Webex Meetings", re.I), "Webex"),
                (re.compile(r"\bHuddle\b", re.I), "Slack")]


def _window_titles():
    import win32gui
    titles = []
    win32gui.EnumWindows(lambda h, _: titles.append(win32gui.GetWindowText(h)) if win32gui.IsWindowVisible(h) else None, None)
    return [t for t in titles if t]


def call_in_progress():
    """'Teams', 'Zoom', ... if a call is going on now, else None."""
    for app in _apps_using_mic():
        for key, label in CALL_APPS.items():
            if key in app:
                return label
    for title in _window_titles():
        for pattern, label in CALL_WINDOWS:
            if pattern.search(title):
                return label
    return None




def notes_base():
    """Meeting notes always go under Documents in your user folder, never to OneDrive (which can sync them to the
    cloud). This is deliberately not Windows' "Documents" known folder, because OneDrive can redirect that one."""
    return Path.home() / "Documents"
