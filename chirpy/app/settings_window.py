

import json
import os
import subprocess
import tkinter as tk
from pathlib import Path

import light_keys
import screen_layout

HERE = Path(__file__).resolve().parent
BG, CARD, FG, MUTED, ACCENT, OFF = "#15191b", "#1b1f22", "#f2f2f2", "#9a9aa4", "#2bbfae", "#454c50"

FEATURES = [
    ("Talking", "dictate_enabled", "Dictation", "Hold or double-tap Left Alt",
     "Hold, talk, let go: your words appear wherever the cursor is. Double-tap for hands-free; tap again to stop."),
    ("Talking", "show_light", "Listening light", None,
     "A small glass microphone at the bottom of the screen that glows green while you talk."),
    ("Talking", "beeps", "Beeps", None,
     "Short sounds when it starts listening, locks hands-free, and finishes."),
    ("Learning", "typed_fix", "Teach a word", "Shift + tap Left Alt",
     "Select a misheard word, then type what it should be. Fixed now and learned for next time."),
]

class Toggle(tk.Canvas):
    def __init__(self, parent, value, on_change, dpi=96):
        self.dpi = dpi
        super().__init__(parent, width=self.s(40), height=self.s(22), bg=CARD, highlightthickness=0, cursor="hand2")
        self.value, self.on_change = value, on_change
        self.bind("<Button-1>", lambda e: self.set(not self.value, notify=True))
        self.draw()

    def s(self, n):
        return screen_layout.scaled(n, self.dpi)

    def set(self, value, notify=False):
        self.value = value
        self.draw()
        if notify:
            self.on_change(value)

    def draw(self):
        s = self.s
        self.delete("all")
        colour = ACCENT if self.value else OFF
        self.create_oval(s(1), s(1), s(21), s(21), fill=colour, outline=colour)
        self.create_oval(s(19), s(1), s(39), s(21), fill=colour, outline=colour)
        self.create_rectangle(s(11), s(1), s(29), s(21), fill=colour, outline=colour)
        x = 29 if self.value else 11
        self.create_oval(s(x - 8), s(3), s(x + 8), s(19), fill="#ffffff", outline="")

class SettingsWindow:
    def __init__(self, root, config, actions):

        self.config, self.actions = config, actions
        win = self.win = tk.Toplevel(root)
        win.title("Chirpy")
        try:
            from PIL import ImageTk
            self._icon = ImageTk.PhotoImage(icon_image(), master=win)
            win.iconphoto(False, self._icon)
        except Exception:
            pass
        win.configure(bg=BG)
        win.resizable(False, False)
        win.attributes("-topmost", True)
        win.after(400, lambda: win.attributes("-topmost", False))

        work, self.dpi = screen_layout.work_area_and_dpi()
        s = self.s

        body = tk.Frame(win, bg=BG, padx=s(18), pady=s(14))
        body.pack(fill="both", expand=True)

        top = tk.Frame(body, bg=BG)
        top.pack(fill="x", pady=(0, s(6)))
        tk.Label(top, text="Chirpy", fg=FG, bg=BG, font=("Segoe UI Semibold", 15)).pack(side="left")
        self.pause = Toggle(top, not config.get("enabled", True), self._set_paused, self.dpi)
        self.pause.configure(bg=BG)
        self.pause.pack(side="right")
        tk.Label(top, text="Pause everything", fg=MUTED, bg=BG, font=("Segoe UI", 9)).pack(side="right", padx=(0, s(8)))

        section = None
        for name, key, title, badge, text in light_keys.features(FEATURES, config):
            if name != section:
                section = name
                tk.Label(body, text=name.upper(), fg=ACCENT, bg=BG, font=("Segoe UI Semibold", 9)).pack(anchor="w", pady=(s(10), s(3)))
            card = tk.Frame(body, bg=CARD, padx=s(12), pady=s(6))
            card.pack(fill="x", pady=s(2))
            Toggle(card, bool(config.get(key, key != "offline_only")), lambda v, k=key: self._set(k, v), self.dpi).grid(
                row=0, column=0, rowspan=2, sticky="n", padx=(0, s(12)), pady=s(2))
            tk.Label(card, text=title, fg=FG, bg=CARD, font=("Segoe UI Semibold", 10)).grid(row=0, column=1, sticky="w")
            if badge:
                tk.Label(card, text=badge, fg="#6fe6d6", bg="#1c3532", font=("Segoe UI", 8), padx=s(6), pady=s(1)).grid(row=0, column=2, sticky="e")
            tk.Label(card, text=text, fg=MUTED, bg=CARD, font=("Segoe UI", 9), justify="left", wraplength=s(480),
                     anchor="w").grid(row=1, column=1, columnspan=2, sticky="w")
            card.grid_columnconfigure(1, weight=1)

        tk.Label(body, text="GENERAL", fg=ACCENT, bg=BG, font=("Segoe UI Semibold", 9)).pack(anchor="w", pady=(s(10), s(3)))
        card = tk.Frame(body, bg=CARD, padx=s(12), pady=s(8))
        card.pack(fill="x", pady=s(2))
        self.startup = Toggle(card, startup_enabled(), self._set_startup, self.dpi)
        self.startup.grid(row=0, column=0, rowspan=2, sticky="n", padx=(0, s(12)), pady=s(2))
        tk.Label(card, text="Start when I sign in", fg=FG, bg=CARD, font=("Segoe UI Semibold", 10)).grid(row=0, column=1, sticky="w")
        tk.Label(card, text="Opens Chirpy automatically each time you log in to Windows.", fg=MUTED, bg=CARD,
                 font=("Segoe UI", 9)).grid(row=1, column=1, sticky="w")

        buttons = tk.Frame(body, bg=BG)
        buttons.pack(fill="x", pady=(s(12), 0))
        for label, action in (("Log", lambda: os.startfile(HERE / "voicetool.log")), ("Folder", lambda: os.startfile(HERE))):
            self._button(buttons, label, action).pack(side="left", padx=(0, s(6)))
        self._button(buttons, "Quit Chirpy", actions["quit"], danger=True).pack(side="right")

        win.update_idletasks()
        x, y = screen_layout.settings_corner(work, self.dpi, win.winfo_reqwidth(), win.winfo_reqheight())
        win.geometry(screen_layout.tk_position(x, y))
        win.focus_force()

    def s(self, n):

        return screen_layout.scaled(n, self.dpi)

    def _button(self, parent, label, action, danger=False):
        return tk.Button(parent, text=label, command=action, relief="flat", cursor="hand2", padx=self.s(10), pady=self.s(3),
                         bg="#3a2230" if danger else "#2c2c33", fg="#ffb3c7" if danger else FG,
                         activebackground="#44444c", activeforeground="#ffffff", font=("Segoe UI", 9))

    def _set(self, key, value):
        save(self.config, {key: bool(value)})

    def _set_paused(self, paused):
        self._set("enabled", not paused)
        if "paused" in self.actions:
            self.actions["paused"](paused)

    def _set_startup(self, on):
        set_startup(on)
        self.startup.set(startup_enabled())

def icon_image(paused=False, recording=False):

    return chirpy_icon_image(paused)

def chirpy_icon_image(paused=False):

    from PIL import Image, ImageDraw, ImageEnhance
    try:
        want = 16
        try:
            import win32api
            want = win32api.GetSystemMetrics(49)
        except Exception:
            pass
        ico = Image.open(HERE / "voicetool.ico")
        sizes = sorted(ico.info.get("sizes") or {ico.size})
        ico.size = next((s for s in sizes if s[0] >= want), sizes[-1])
        ico.load()
        img = ico.convert("RGBA")
    except Exception:
        img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
        ImageDraw.Draw(img).ellipse((6, 6, 58, 58), fill=(18, 173, 156, 255))
    if paused:
        r, g, b, a = img.split()
        grey = ImageEnhance.Brightness(Image.merge("RGB", (r, g, b)).convert("L")).enhance(0.85)
        img = Image.merge("RGBA", (grey, grey, grey, a))
    return img
def save(config, changes):

    return light_keys.change(HERE / "config.json", config, changes)

RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "Chirpy"
_test_startup = {"on": False}

def startup_command():
    import sys
    pythonw = Path(sys.executable).with_name("pythonw.exe")
    return f'"{pythonw}" -s -E "{HERE / "voicetool.py"}"'

def startup_enabled():
    if os.environ.get("VOICETOOL_TEST") == "1":
        return _test_startup["on"]
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value, _ = winreg.QueryValueEx(key, RUN_VALUE)
        return bool(str(value).strip())
    except OSError:
        return False

def set_startup(on):
    if os.environ.get("VOICETOOL_TEST") == "1":
        _test_startup["on"] = bool(on)
        return
    import winreg
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
        if on:
            winreg.SetValueEx(key, RUN_VALUE, 0, winreg.REG_SZ, startup_command())
        else:
            try:
                winreg.DeleteValue(key, RUN_VALUE)
            except FileNotFoundError:
                pass
