

import ctypes
import ctypes.wintypes as wintypes
import hashlib
import json
import queue
import re
import subprocess
import threading
import time
from pathlib import Path

import win32api
import win32clipboard
import win32con
import win32gui
import win32process
from pynput import keyboard

from spelling import americanize

HERE = Path(__file__).resolve().parent

NO_COPY_APPS = {"windowsterminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "wezterm-gui.exe",
                "alacritty.exe", "mintty.exe", "code.exe", "cursor.exe", "putty.exe", "openconsole.exe"}

class _GuiThreadInfo(ctypes.Structure):

    _fields_ = [("cbSize", wintypes.DWORD), ("flags", wintypes.DWORD), ("hwndActive", wintypes.HWND),
                ("hwndFocus", wintypes.HWND), ("hwndCapture", wintypes.HWND), ("hwndMenuOwner", wintypes.HWND),
                ("hwndMoveSize", wintypes.HWND), ("hwndCaret", wintypes.HWND), ("rcCaret", wintypes.RECT)]

def _thread_focus(thread_id):

    info = _GuiThreadInfo()
    info.cbSize = ctypes.sizeof(info)
    if ctypes.windll.user32.GetGUIThreadInfo(thread_id, ctypes.byref(info)):
        return int(info.hwndFocus or 0)
    return 0

def focus_control(hwnd):

    if not hwnd:
        return 0
    try:
        thread_id, _ = win32process.GetWindowThreadProcessId(hwnd)
        focus = _thread_focus(thread_id)
        if not focus and hwnd == win32gui.GetForegroundWindow():
            focus = _thread_focus(0)
        return focus
    except Exception:
        return 0

def window_info():

    hwnd = win32gui.GetForegroundWindow()
    title = win32gui.GetWindowText(hwnd)
    app, pid = "", 0
    try:
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        handle = win32api.OpenProcess(win32con.PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        app = Path(win32process.GetModuleFileNameEx(handle, 0)).name
    except Exception:
        pass
    return {"app": app, "bundle": "", "title": title, "pid": pid, "hwnd": hwnd, "focus": focus_control(hwnd)}

def selected_text(open_clipboard, save_clipboard, restore_clipboard):

    saved = save_clipboard()
    before = win32clipboard.GetClipboardSequenceNumber()
    kb = keyboard.Controller()
    with kb.pressed(keyboard.Key.ctrl):
        kb.tap("c")
    text = ""
    for _ in range(15):
        time.sleep(0.02)
        if win32clipboard.GetClipboardSequenceNumber() != before:
            time.sleep(0.03)
            open_clipboard()
            try:
                if win32clipboard.IsClipboardFormatAvailable(win32con.CF_UNICODETEXT):
                    text = win32clipboard.GetClipboardData(win32con.CF_UNICODETEXT)
            finally:
                win32clipboard.CloseClipboard()
            break
    if win32clipboard.GetClipboardSequenceNumber() != before:
        restore_clipboard(saved)

    return text if text.strip() else ""

SELECTION_LIMIT = 12000

def selection_identity(text):

    whole = (text or "").strip()
    return f"{len(whole)}:{hashlib.sha256(whole.encode('utf-8', 'surrogatepass')).hexdigest()}"

def has_selection(context):

    return bool((context or {}).get("selection") or (context or {}).get("selection_id"))

def capture_context(open_clipboard, save_clipboard, restore_clipboard):
    info = window_info()
    info["selection"] = ""
    if info["app"].lower() not in NO_COPY_APPS:
        try:
            copied = selected_text(open_clipboard, save_clipboard, restore_clipboard)
            chosen = copied[:SELECTION_LIMIT]
            info["selection"] = chosen if chosen.strip() else ""
            if copied.strip():
                info["selection_id"] = selection_identity(copied)
                info["selection_cut"] = bool(copied[SELECTION_LIMIT:].strip())
        except Exception:
            pass
    return info

