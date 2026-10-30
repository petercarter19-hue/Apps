"""VoiceTool's original small settings window (tkinter): every feature, its key, what it does, and an on/off switch.
Changes apply at once. Since the app window (ui/index.html, shown by app_window_win.py) took over, this is only the
fallback: VoiceTool opens it if the app window can't start. The tray icon, the Start-menu entry and "Settings" all open
the app window first. This file also keeps the helpers they share: icon_image(), save(), startup_enabled(), set_startup()."""
import json
import os
import subprocess
import tkinter as tk
from pathlib import Path

HERE = Path(__file__).resolve().parent
BG, CARD, FG, MUTED, ACCENT, OFF = "#15191b", "#1b1f22", "#f2f2f2", "#9a9aa4", "#2bbfae", "#454c50"

# the same list of features and switches as the app window's Settings page
from ui_api import FEATURES, OFF_BY_DEFAULT  # noqa: E402


class Toggle(tk.Canvas):
    def __init__(self, parent, value, on_change):
        super().__init__(parent, width=40, height=22, bg=CARD, highlightthickness=0, cursor="hand2")
        self.value, self.on_change = value, on_change
        self.bind("<Button-1>", lambda e: self.set(not self.value, notify=True))
        self.draw()

    def set(self, value, notify=False):
        self.value = value
        self.draw()
        if notify:
            self.on_change(value)

    def draw(self):
        self.delete("all")
        colour = ACCENT if self.value else OFF
        self.create_oval(1, 1, 21, 21, fill=colour, outline=colour)
        self.create_oval(19, 1, 39, 21, fill=colour, outline=colour)
        self.create_rectangle(11, 1, 29, 21, fill=colour, outline=colour)
        x = 29 if self.value else 11
        self.create_oval(x - 8, 3, x + 8, 19, fill="#ffffff", outline="")


class SettingsWindow:
    def __init__(self, root, config, actions):
        """config: the live settings dict. actions: callbacks (spending, quit, restart) from the running tool."""
        self.config, self.actions = config, actions
        self.toggles = {}
        clear_first_run(config)
        win = self.win = tk.Toplevel(root)
        win.title("VoiceTool")
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
        body = tk.Frame(win, bg=BG, padx=18, pady=14)
        body.pack(fill="both", expand=True)

        top = tk.Frame(body, bg=BG)
        top.pack(fill="x", pady=(0, 6))
        tk.Label(top, text="VoiceTool", fg=FG, bg=BG, font=("Segoe UI Semibold", 15)).pack(side="left")
        self.pause = Toggle(top, not config.get("enabled", True), self._set_paused)
        self.pause.configure(bg=BG)
        self.pause.pack(side="right")
        tk.Label(top, text="Pause everything", fg=MUTED, bg=BG, font=("Segoe UI", 9)).pack(side="right", padx=(0, 8))

        section = None
        for name, key, title, badge, text in FEATURES:
            if name != section:
                section = name
                tk.Label(body, text=name.upper(), fg=ACCENT, bg=BG, font=("Segoe UI Semibold", 9)).pack(anchor="w", pady=(10, 3))
            card = tk.Frame(body, bg=CARD, padx=12, pady=6)
            card.pack(fill="x", pady=2)
            toggle = self.toggles[key] = Toggle(card, bool(config.get(key, key not in OFF_BY_DEFAULT)),
                                                lambda v, k=key: self._set(k, v))
            toggle.grid(row=0, column=0, rowspan=2, sticky="n", padx=(0, 12), pady=2)
            tk.Label(card, text=title, fg=FG, bg=CARD, font=("Segoe UI Semibold", 10)).grid(row=0, column=1, sticky="w")
            if badge:
                tk.Label(card, text=badge, fg="#6fe6d6", bg="#1c3532", font=("Segoe UI", 8), padx=6, pady=1).grid(row=0, column=2, sticky="e")
            tk.Label(card, text=text, fg=MUTED, bg=CARD, font=("Segoe UI", 9), justify="left", wraplength=480,
                     anchor="w").grid(row=1, column=1, columnspan=2, sticky="w")
            card.grid_columnconfigure(1, weight=1)

        tk.Label(body, text="GENERAL", fg=ACCENT, bg=BG, font=("Segoe UI Semibold", 9)).pack(anchor="w", pady=(10, 3))
        card = tk.Frame(body, bg=CARD, padx=12, pady=8)
        card.pack(fill="x", pady=2)
        self.startup = Toggle(card, startup_enabled(), self._set_startup)
        self.startup.grid(row=0, column=0, rowspan=2, sticky="n", padx=(0, 12), pady=2)
        tk.Label(card, text="Start when I sign in", fg=FG, bg=CARD, font=("Segoe UI Semibold", 10)).grid(row=0, column=1, sticky="w")
        tk.Label(card, text="Opens VoiceTool automatically each time you sign in to Windows.", fg=MUTED, bg=CARD,
                 font=("Segoe UI", 9)).grid(row=1, column=1, sticky="w")

        style_row = tk.Frame(body, bg=BG)
        style_row.pack(fill="x", pady=(12, 0))
        self._button(style_row, "My style", lambda: os.startfile(HERE / "style.md") if (HERE / "style.md").exists()
                     else None).pack(side="left", padx=(0, 6))
        if "refresh_style" in actions and config.get("claude_actions", False):  # (needs Claude)
            self._button(style_row, "Re-learn my style from sent emails", actions["refresh_style"]).pack(side="left")
        buttons = tk.Frame(body, bg=BG)
        buttons.pack(fill="x", pady=(6, 0))
        for label, action in (("Word list", lambda: os.startfile(HERE / "words.txt")),
                              ("Learned fixes", open_learned_fixes),
                              ("Log", lambda: os.startfile(HERE / "voicetool.log")),
                              ("Folder", lambda: os.startfile(HERE))):
            self._button(buttons, label, action).pack(side="left", padx=(0, 6))
        self._button(buttons, "Quit VoiceTool", actions["quit"], danger=True).pack(side="right")
        if finish_setup_wanted(actions):
            setup = tk.Frame(body, bg=BG)
            setup.pack(fill="x", pady=(10, 0))
            self._button(setup, "Finish setup: download what's missing", self._finish_setup).pack(side="left")

        win.update_idletasks()
        w, h = win.winfo_reqwidth(), win.winfo_reqheight()
        x, y = win.winfo_screenwidth() - w - 40, max(20, win.winfo_screenheight() - h - 90)
        win.geometry(f"+{x}+{y}")
        win.focus_force()

    def _finish_setup(self):
        from tkinter import messagebox
        reply = self.actions["finish_setup"]() or {}
        messagebox.showinfo("Finish setup", reply.get("message") or "The download opened in its own window. "
                            "It says when it's done.", parent=self.win)

    def _button(self, parent, label, action, danger=False):
        return tk.Button(parent, text=label, command=action, relief="flat", cursor="hand2", padx=10, pady=3,
                         bg="#3a2230" if danger else "#2c2c33", fg="#ffb3c7" if danger else FG,
                         activebackground="#44444c", activeforeground="#ffffff", font=("Segoe UI", 9))

    def _set(self, key, value):
        if value and key == "meeting_notes":  # turning meeting notes on asks about consent first
            import meetings
            from tkinter import messagebox
            if not messagebox.askokcancel("Meeting notes", meetings.CONSENT_REMINDER, parent=self.win):
                self.toggles[key].set(False)
                return
        if key == "offline_only" and not value and not str(self.config.get("azure_openai_base_url") or "").strip():
            from tkinter import messagebox
            messagebox.showinfo("Keep everything on this computer", "Azure isn't set up (there is no Azure endpoint in "
                                "config.json), so this has to stay on.", parent=self.win)
            self.toggles[key].set(True)
            return
        self.config[key] = value
        save(self.config)
        if key == "claude_keep_running" and not value and "stop_claude" in self.actions:
            self.actions["stop_claude"]()
        if key in ("offline_tidyup", "offline_only") and "offline" in self.actions:
            self.actions["offline"]()

    def _set_paused(self, paused):
        self._set("enabled", not paused)
        if "paused" in self.actions:
            self.actions["paused"](paused)

    def _set_startup(self, on):
        set_startup(on)
        self.startup.set(startup_enabled())


def icon_image(paused=False, recording=False):
    """The VoiceTool icon: a white microphone on a blue-green circle (grey when paused, red while taking meeting notes)."""
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    colour = (230, 64, 70, 255) if recording else (110, 110, 120, 255) if paused else (43, 191, 174, 255)
    d.ellipse((2, 2, 62, 62), fill=colour)
    d.rounded_rectangle((24, 12, 40, 38), radius=8, fill="white")
    d.arc((17, 22, 47, 46), start=0, end=180, fill="white", width=4)
    d.line((32, 46, 32, 53), fill="white", width=4)
    d.line((24, 53, 40, 53), fill="white", width=4)
    return img


def clear_first_run(config):
    """The Welcome card only exists in the app window, so opening this fallback window counts as having seen it."""
    if config.get("first_run"):
        config["first_run"] = False
        save(config)


def finish_setup_wanted(actions):
    """True when this window should offer "Finish setup" (the models are missing and the tool can start the download)."""
    return "finish_setup" in actions and bool(actions.get("setup_missing", lambda: False)())


def open_learned_fixes():
    """Open the learned fixes file; it doesn't exist until the first fix is learned, so an empty one is made first."""
    path = HERE / "corrections.json"
    if not path.exists():
        path.write_text("[]\n", encoding="utf-8")
    os.startfile(path)


def save(config):
    (HERE / "config.json").write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")


# The Task Scheduler task that starts VoiceTool at sign-in. (Tests point this at a task that doesn't exist.)
TASK = os.environ.get("VOICETOOL_TASK", "VoiceTool")


def startup_enabled():
    try:
        out = subprocess.run(["schtasks", "/Query", "/TN", TASK, "/FO", "CSV", "/NH"], capture_output=True,
                             text=True, creationflags=subprocess.CREATE_NO_WINDOW).stdout
        return bool(out.strip()) and "Disabled" not in out
    except Exception:
        return False


def set_startup(on):
    """Turn "start when I sign in" on or off by enabling or disabling the Task Scheduler task."""
    subprocess.run(["schtasks", "/Change", "/TN", TASK, "/ENABLE" if on else "/DISABLE"], capture_output=True,
                   creationflags=subprocess.CREATE_NO_WINDOW)
