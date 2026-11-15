

try:
    import light_start
    light_start.install()
except Exception:
    light_start = None
import ctypes
import json
import os
import logging
import logging.handlers
import queue
import re
import subprocess
import sys
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
import wave
import winsound
from ctypes import wintypes
from pathlib import Path

import numpy as np
import onnx_asr
import sounddevice as sd
import win32api
import win32clipboard
import win32con
import win32event
import winerror
from pynput import keyboard

import commands
import corrections
import light_files
import light_keys
import mic_light
import screen_layout
import settings_window

try:
    from pynput._util.win32 import SystemHook
    _SUPPRESS = SystemHook.SuppressException
except Exception:
    class _SUPPRESS(Exception):
        pass

HERE = Path(__file__).resolve().parent
CONFIG = light_keys.read_settings(HERE / "config.json")
_NOTICE = {"text": None}
light_keys.on_problem = lambda text, detail: (log.warning(detail), _NOTICE.update(text=text))
MIC_TEXT = ("Chirpy couldn't open a microphone, so nothing was recorded. Plug in a microphone and try again. If one is "
            "plugged in, open Windows Settings, then Privacy & security, then Microphone, and turn on microphone access "
            "and \"Let desktop apps access your microphone\".")
RATE = 16000
MAX_CHUNK_SECONDS = 25
LLKHF_INJECTED = 0x10
MASK_VK = 0xE8
VK_LCONTROL = 0xA2
ALTGR_CTRL_SCAN = 0x21D
LLMHF_INJECTED = 0x01
MOUSE_USED = (0x201, 0x204, 0x207, 0x20B, 0x20A, 0x20E)
CTRL_KEYS = ("ctrl", "ctrl_l", "ctrl_r")
CTRL_MIC_DELAY_SECONDS = 0.15

log = logging.getLogger("voicetool")
log.setLevel(logging.INFO)
_handler = logging.handlers.RotatingFileHandler(HERE / "voicetool.log", maxBytes=1_000_000, backupCount=2, encoding="utf-8",
                                               delay=True)
_handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
_early = logging.handlers.MemoryHandler(10_000, flushLevel=logging.CRITICAL + 1)
log.addHandler(_early)
if sys.stdout:
    log.addHandler(logging.StreamHandler(sys.stdout))

logging.getLogger("pynput").setLevel(logging.WARNING)
logging.getLogger("pynput").addHandler(_handler)

HOOK_MISSED_PRESS_SECONDS = 0.4
HOOK_MISSED_RELEASE_SECONDS = 0.3
WATCH_EVERY_SECONDS = 0.05
LOOP_STUCK_SECONDS = 3.0

_BEEP_DIR = Path(os.environ.get("TEMP", HERE)) / "voicetool-sounds"
_BEEP_DIR = Path(os.environ.get("TEMP", HERE)) / "voicetool-light-sounds"

def _soft_tone_file(tones, volume):

    name = "_".join(f"{f}-{d}" for f, d in tones) + f"_v{int(volume * 100)}.wav"
    path = _BEEP_DIR / name
    if not path.exists():
        _BEEP_DIR.mkdir(parents=True, exist_ok=True)
        rate, parts = 22050, []
        for f, d in tones:
            t = np.arange(int(rate * d / 1000)) / rate
            tone = np.sin(2 * np.pi * f * t)
            fade = np.minimum(1, np.minimum(t, t[-1] - t) / 0.012) if len(t) else t
            parts += [tone * fade * volume, np.zeros(int(rate * 0.025))]
        audio = (np.concatenate(parts) * 32767).astype(np.int16)
        with wave.open(str(path), "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(rate)
            w.writeframes(audio.tobytes())
    return str(path)

def beep(*tones):
    if not CONFIG.get("beeps", True):
        return
    try:
        winsound.PlaySound(_soft_tone_file(tones, float(CONFIG.get("beep_volume", 0.12))),
                           winsound.SND_FILENAME | winsound.SND_ASYNC | winsound.SND_NODEFAULT)
    except Exception:
        pass

def known_words():
    path = HERE / "words.txt"
    if not path.exists():
        return []
    return [w.strip() for w in path.read_text(encoding="utf-8").splitlines() if w.strip() and not w.startswith("#")]

def spell_words(text, words):

    for word in sorted(words, key=len, reverse=True):
        pattern = r"(?<!\w)" + r"\s+".join(map(re.escape, word.split())) + r"(?!\w)"
        text = re.sub(pattern, lambda m, word=word: word, text, flags=re.IGNORECASE)
    return text

def find_mic():
    want = CONFIG.get("mic_name_contains", "").lower()
    for i, d in enumerate(sd.query_devices()):
        if d["max_input_channels"] > 0 and d["hostapi"] == 0 and want and want in d["name"].lower():
            return i
    return None

class Recorder:
    def __init__(self):
        self.device = find_mic()
        self.frames = []
        self.stream = None
        self.level = 0.0

    def _audio(self, data, frames, when, status):
        self.frames.append(data.copy())
        self.level = float(np.sqrt((data ** 2).mean()))

    def start(self):
        self.frames = []
        self.stream = sd.InputStream(samplerate=RATE, channels=1, dtype="float32", device=self.device, callback=self._audio)
        self.stream.start()

    def stop(self):
        if self.stream is None:
            return np.zeros(0, np.float32)
        self.stream.stop()
        self.stream.close()
        self.stream = None
        self.level = 0.0
        return np.concatenate(self.frames)[:, 0] if self.frames else np.zeros(0, np.float32)

class Transcriber:
    def __init__(self):
        t = time.perf_counter()
        self.model = onnx_asr.load_model(CONFIG["speech_model"], HERE / CONFIG["speech_model_path"], quantization="int8")
        self.model.recognize(np.zeros(RATE, np.float32), sample_rate=RATE)
        log.info("speech model ready in %.1fs", time.perf_counter() - t)

    def __call__(self, audio):
        text = " ".join(t for t in (self.model.recognize(c, sample_rate=RATE).strip() for c in split_at_pauses(audio)) if t)
        return " ".join(text.replace("<unk>", " ").split())

def split_at_pauses(audio):
    chunks, step = [], RATE // 10
    while len(audio) > MAX_CHUNK_SECONDS * RATE:
        lo, hi = 15 * RATE, MAX_CHUNK_SECONDS * RATE
        window = audio[lo:hi][: (hi - lo) // step * step].reshape(-1, step)
        cut = lo + int(np.argmin((window ** 2).mean(axis=1))) * step + step // 2
        chunks.append(audio[:cut])
        audio = audio[cut:]
    chunks.append(audio)
    return chunks

_cf_exclude = win32clipboard.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing")
_cf_no_history = win32clipboard.RegisterClipboardFormat("CanIncludeInClipboardHistory")

def _open_clipboard():
    for _ in range(20):
        try:
            win32clipboard.OpenClipboard()
            return
        except Exception:
            time.sleep(0.02)
    win32clipboard.OpenClipboard()

def save_clipboard():
    saved = []
    _open_clipboard()
    try:
        fmt = win32clipboard.EnumClipboardFormats(0)
        while fmt:
            try:
                data = win32clipboard.GetClipboardData(fmt)
                if isinstance(data, (bytes, str)):
                    saved.append((fmt, data))
            except Exception:
                pass
            fmt = win32clipboard.EnumClipboardFormats(fmt)
    finally:
        win32clipboard.CloseClipboard()
    return saved

def restore_clipboard(saved):
    _open_clipboard()
    try:
        win32clipboard.EmptyClipboard()
        for fmt, data in saved:
            try:
                win32clipboard.SetClipboardData(fmt, data)
            except Exception:
                pass
    finally:
        win32clipboard.CloseClipboard()

def set_clipboard_text(text):
    _open_clipboard()
    try:
        win32clipboard.EmptyClipboard()
        win32clipboard.SetClipboardText(text, win32con.CF_UNICODETEXT)

        for fmt in (_cf_exclude, _cf_no_history):
            try:
                win32clipboard.SetClipboardData(fmt, b"\x00\x00\x00\x00")
            except Exception as e:
                log.warning("clipboard history opt-out skipped: %s", e)
    finally:
        win32clipboard.CloseClipboard()

def paste(text):
    saved = save_clipboard()
    set_clipboard_text(text)
    ours = win32clipboard.GetClipboardSequenceNumber()
    kb = keyboard.Controller()
    with kb.pressed(keyboard.Key.ctrl):
        kb.tap("v")
    time.sleep(1.0)
    if win32clipboard.GetClipboardSequenceNumber() == ours:
        restore_clipboard(saved)

def make_click_through(root):

    import win32gui
    hwnd = win32gui.GetParent(root.winfo_id()) or root.winfo_id()
    style = win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
    win32gui.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, style | win32con.WS_EX_NOACTIVATE
                           | win32con.WS_EX_TOOLWINDOW | win32con.WS_EX_TRANSPARENT | win32con.WS_EX_LAYERED)

class Light:

    LOOKS = mic_light.LOOKS
    RECORDING = ("listening", "handsfree", "command", "command_handsfree")
    VOICE_GAIN = 12.0
    FALL = 0.82

    def __init__(self, root, previous=None):
        import win32gui
        self.root, self.state, self.shown, self.level = root, None, None, 0.0
        self.error_until = 0.0
        self._placed = None
        self._last_monitor = None
        self._glow, self._painted = 0.0, None
        self._ever_shown = False
        root.overrideredirect(True)
        root.attributes("-alpha", 0.0)
        root.geometry(screen_layout.tk_geometry(1, 1, 0, 0))
        root.update()
        make_click_through(root)
        self.painter = mic_light.MicPainter()
        try:
            self.window = mic_light.MicWindow()
        except Exception:
            log.exception("could not make the light's window; VoiceTool carries on without it")
            self.window = None

        if previous and win32gui.GetForegroundWindow() != previous:
            try:
                win32gui.SetForegroundWindow(previous)
            except Exception as e:
                log.info("could not hand focus back: %s", e)

    def set(self, state):

        if state == "error":
            self.error_until = time.monotonic() + 1.5
            state = None
        self.state = state

    def _monitor(self):

        found = None
        try:
            found = screen_layout.foreground_monitor()
        except Exception as e:
            log.info("could not ask which monitor is in front: %s", e)
        if found is None:
            found = self._last_monitor
        if found is None:
            try:
                found = screen_layout.primary_monitor()
            except Exception:
                found = ((0, 0, self.root.winfo_screenwidth(), self.root.winfo_screenheight()), screen_layout.BASE_DPI)
        self._last_monitor = found
        return found

    def draw(self):
        state = self.state
        if state is None and time.monotonic() < self.error_until:
            state = "error"
        if not CONFIG.get("show_light", True) or self.window is None:
            state = None
        if state is None:
            if self.shown is not None and self.window is not None:
                self.window.hide()
            self.shown, self._painted, self._glow = None, None, 0.0
            return
        if state != self.shown:
            work, dpi = self._monitor()
            self._placed = mic_light.geometry(work, dpi) + (dpi,)
            self.shown = state
        side, x, y, dpi = self._placed
        heard = min(1.0, self.level * self.VOICE_GAIN) if state in self.RECORDING or state == "meeting" else 0.0
        self._glow = max(heard, self._glow * self.FALL)
        now = time.monotonic()
        spin = mic_light.MicPainter.spin_index(now) if self.LOOKS[state][2] == "spin" else 0
        key = (state, x, y, dpi, mic_light.MicPainter.level_step(self._glow), spin)
        if key != self._painted:
            self.window.paint(self.painter.frame(state, dpi, self._glow, now), x, y, now)
            if self._painted is None and not self._ever_shown:
                self._ever_shown = True
                log.info("light shown for the first time: %s at (%d, %d), %d dpi", state, x, y, dpi)
            self._painted = key

def strip_punctuation(text):
    return re.sub(r"^[\W_]+|[\W_]+$", "", text.strip())

def keep_punctuation(selection, fix):

    m = re.match(r"^(\s*[\W_]*)(.*?)([\W_]*\s*)$", selection, re.S)
    return (m.group(1) + fix + m.group(3)) if m and selection else fix

def bring_to_front(hwnd, tries=10):

    import win32gui
    for _ in range(tries):
        if win32gui.GetForegroundWindow() == hwnd:
            return True
        try:
            win32api.keybd_event(MASK_VK, 0, 0, 0)
            win32api.keybd_event(MASK_VK, 0, win32con.KEYEVENTF_KEYUP, 0)
            win32gui.SetForegroundWindow(hwnd)
        except Exception:
            pass
        time.sleep(0.05)
    return win32gui.GetForegroundWindow() == hwnd

class TeachWindow:

    BG, FG, FIELD = "#1f1f23", "#f2f2f2", "#2c2c33"

    def __init__(self, root, heard, target, on_save):
        import win32gui
        self.target, self.on_save, self.selection = target, on_save, heard
        work, dpi = screen_layout.work_area_and_dpi()

        def s(n):
            return screen_layout.scaled(n, dpi)
        win = self.win = tk.Toplevel(root)
        win.title("Teach Chirpy a word")
        win.attributes("-topmost", True)
        win.configure(bg=self.BG, padx=s(16), pady=s(12))
        win.resizable(False, False)
        tk.Label(win, text="Teach Chirpy a word", fg="#6fe6d6", bg=self.BG, font=("Segoe UI Semibold", 11)).grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, s(8)))
        self.heard = tk.StringVar(value=strip_punctuation(heard))
        self.meant = tk.StringVar()
        for row, (label, var) in enumerate((("It wrote", self.heard), ("You meant", self.meant)), start=1):
            tk.Label(win, text=label, fg="#a0a0a8", bg=self.BG, font=("Segoe UI", 10)).grid(row=row, column=0, sticky="w", padx=(0, s(10)))
            entry = tk.Entry(win, textvariable=var, width=32, bg=self.FIELD, fg=self.FG, insertbackground=self.FG,
                             relief="flat", font=("Segoe UI", 11))
            entry.grid(row=row, column=1, pady=s(4), ipady=s(4))
            if var is self.meant:
                self.meant_entry = entry
        tk.Label(win, text="Leave “It wrote” empty to just add the word to your list.   Enter = save · Esc = cancel",
                 fg="#7c7c86", bg=self.BG, font=("Segoe UI", 9)).grid(row=3, column=0, columnspan=2, sticky="w", pady=(s(8), 0))
        if not self.heard.get():
            for footer in win.grid_slaves(row=3):
                footer.grid_configure(row=4)
            tk.Label(win, text=light_keys.teach_hint(light_keys.resolve(CONFIG)[1]), fg="#f5c26b", bg=self.BG,
                     font=("Segoe UI", 10), justify="left", anchor="w", wraplength=s(380)).grid(
                row=3, column=0, columnspan=2, sticky="w", pady=(s(8), 0))
        win.bind("<Return>", lambda e: self.save())
        win.bind("<Escape>", lambda e: self.cancel())
        win.protocol("WM_DELETE_WINDOW", self.cancel)
        win.update_idletasks()
        x, y = screen_layout.centered_above_bottom(work, dpi, win.winfo_reqwidth(), win.winfo_reqheight())
        win.geometry(screen_layout.tk_position(x, y))
        win.deiconify()
        win.lift()
        win.focus_force()
        self.meant_entry.focus_set()
        try:
            win32gui.SetForegroundWindow(win32gui.GetParent(win.winfo_id()) or win.winfo_id())
        except Exception:
            pass

    def save(self):
        heard, meant = self.heard.get().strip(), self.meant.get().strip()
        self.win.destroy()
        if meant:

            selection = self.selection if strip_punctuation(self.selection) == heard else heard
            threading.Thread(target=self.on_save, args=(heard, meant, self.target, selection), daemon=True).start()
        else:
            self.cancel()

    def cancel(self):
        try:
            self.win.destroy()
        except Exception:
            pass
        threading.Thread(target=bring_to_front, args=(self.target,), daemon=True).start()

class ResultCard:

    BG = "#1f1f23"

    def __init__(self, root):
        self.root = root
        self.win = None

    def show(self, title, body, buttons=None, seconds=60):

        import win32gui
        previous = win32gui.GetForegroundWindow()
        self.close()
        work, dpi = screen_layout.work_area_and_dpi()

        def s(n):
            return screen_layout.scaled(n, dpi)
        win = self.win = tk.Toplevel(self.root)
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        win.configure(bg=self.BG)
        frame = tk.Frame(win, bg=self.BG, padx=s(14), pady=s(10))
        frame.pack(fill="both", expand=True)
        tk.Label(frame, text=title, fg="#6fe6d6", bg=self.BG, font=("Segoe UI Semibold", 10), anchor="w").pack(fill="x")
        tk.Label(frame, text=body, fg="#f2f2f2", bg=self.BG, font=("Segoe UI", 10), justify="left", anchor="w",
                 wraplength=s(400)).pack(fill="x", pady=(s(4), s(8)))
        row = tk.Frame(frame, bg=self.BG)
        row.pack(fill="x")
        if buttons:
            actions = [(label, lambda a=action: (self.close(), a())) for label, action in reversed(buttons)]
        else:
            actions = [("Close", self.close), ("Copy", lambda: set_clipboard_text(body))]
        for label, action in actions:
            tk.Button(row, text=label, command=action, relief="flat", bg="#33333a", fg="#f2f2f2", padx=s(10),
                      activebackground="#44444c", activeforeground="#ffffff", font=("Segoe UI", 9)).pack(side="right", padx=(s(6), 0))
        win.update_idletasks()
        w, h, x, y = screen_layout.corner_geometry(work, dpi, win.winfo_reqwidth(), win.winfo_reqheight(), min_width=320)
        win.geometry(screen_layout.tk_geometry(w, h, x, y))
        win.update()
        hwnd = win32gui.GetParent(win.winfo_id()) or win.winfo_id()
        style = win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
        win32gui.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, style | win32con.WS_EX_NOACTIVATE | win32con.WS_EX_TOOLWINDOW)
        if previous and win32gui.GetForegroundWindow() != previous:
            try:
                win32gui.SetForegroundWindow(previous)
            except Exception:
                pass
        win.after(seconds * 1000, lambda shown=win: self.close() if self.win is shown else None)

    def close(self):
        if self.win is not None:
            try:
                self.win.destroy()
            except Exception:
                pass
            self.win = None

IDLE, HOLDING, TAP_WAIT, LOCKED, TEACH = "idle", "holding", "tap_wait", "locked", "teach"

class _KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", wintypes.DWORD), ("scanCode", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]

class KeyboardHook(threading.Thread):

    _c, _w = ctypes, wintypes
    _DATA = _KBDLLHOOKSTRUCT
    HOOK_ID = 13
    THREAD_NAME = "keyboard hook"
    _PROC = _c.WINFUNCTYPE(_c.c_ssize_t, _c.c_int, _w.WPARAM, _w.LPARAM)
    _user32 = _c.WinDLL("user32", use_last_error=True)
    _kernel32 = _c.WinDLL("kernel32")
    _SetHook = _user32.SetWindowsHookExW
    _SetHook.argtypes = (_c.c_int, _PROC, _w.HINSTANCE, _w.DWORD)
    _SetHook.restype = _w.HHOOK
    _Unhook = _user32.UnhookWindowsHookEx
    _Unhook.argtypes = (_w.HHOOK,)

    _CallNext = _c.PyDLL("user32").CallNextHookEx
    _CallNext.argtypes = (_w.HHOOK, _c.c_int, _w.WPARAM, _w.LPARAM)
    _CallNext.restype = _c.c_ssize_t
    _PostThreadMessage = _user32.PostThreadMessageW
    _PostThreadMessage.argtypes = (_w.DWORD, _w.UINT, _w.WPARAM, _w.LPARAM)

    def __init__(self, on_key):
        super().__init__(daemon=True, name=self.THREAD_NAME)
        self.on_key = on_key
        self.installed = False
        self.handle = None
        self.error = 0
        self._thread_id = None
        self._ready = threading.Event()
        self._proc = self._PROC(self._callback)

    def suppress_event(self):
        raise _SUPPRESS()

    def _callback(self, code, wparam, lparam):
        if code == 0:
            try:
                self.on_key(wparam, self._c.cast(lparam, self._c.POINTER(self._DATA)).contents)
            except _SUPPRESS:
                return 1
            except Exception:
                pass
        return self._CallNext(None, code, wparam, lparam)

    def run(self):
        k32 = self._kernel32
        self._thread_id = k32.GetCurrentThreadId()
        k32.SetThreadPriority(k32.GetCurrentThread(), 2)
        msg = self._w.MSG()
        self._user32.PeekMessageW(self._c.byref(msg), None, 0, 0, 0)
        hook = self._SetHook(self.HOOK_ID, self._proc, None, 0)
        self.handle = hook
        self.installed = bool(hook)
        self.error = 0 if hook else self._c.get_last_error()
        self._ready.set()
        if not hook:
            return
        try:
            while self._user32.GetMessageW(self._c.byref(msg), None, 0, 0) > 0:
                pass
        finally:
            self._Unhook(hook)

    def wait(self, timeout=5):
        return self._ready.wait(timeout)

    def stop(self):
        self._ready.wait(5)
        if self._thread_id:
            self._PostThreadMessage(self._thread_id, 0x0012, 0, 0)

class _MSLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("x", wintypes.LONG), ("y", wintypes.LONG), ("mouseData", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]

class MouseHook(KeyboardHook):

    _DATA = _MSLLHOOKSTRUCT
    HOOK_ID = 14
    THREAD_NAME = "mouse hook"

class Capture:

    def __init__(self):
        self.context = None

class Job:

    def __init__(self, t, target=None):
        self.name = t.name
        self.pressed_at = t.pressed_at
        self.app = dict(t.app or {})
        self.capture = t.capture
        self.target = target

    @property
    def context(self):
        return self.capture.context

    @property
    def looks(self):
        return ("listening", "handsfree") if self.name == "dictate" else ("command", "command_handsfree")

class Trigger:

    def __init__(self, name, key_name, let_through):
        self.name = name
        self.vk = getattr(keyboard.Key, key_name).value.vk

        self.ctrl = key_name in CTRL_KEYS
        self.masks_lone_press = not self.ctrl
        self.pending = None

        self.let_through = let_through or self.ctrl

        self.mic_delay = CTRL_MIC_DELAY_SECONDS if self.ctrl else 0.0
        self.mic_on = False
        self.state = IDLE
        self.key_down = False
        self.pressed_at = 0.0
        self.tap_deadline = 0.0
        self.swallow_release = False
        self.cued = False
        self.hook_down = False
        self.hook_alone = False

        self.missed_down_since = None
        self.missed_up_since = None
        self.hook_at = 0.0

        self.capture = Capture()
        self.capturing = False
        self.app = {}

    @property
    def context(self):
        return self.capture.context

    @context.setter
    def context(self, value):
        self.capture.context = value

    @property
    def looks(self):
        return ("listening", "handsfree") if self.name == "dictate" else ("command", "command_handsfree")

def make_trigger(name, key_id):

    spec = light_keys.KEYS[key_id]
    trigger = Trigger(name, key_id, spec.let_through)
    trigger.let_through = spec.let_through
    trigger.masks_lone_press = spec.masks
    trigger.language = spec.language
    trigger.teach_with = None
    trigger.chord = None
    trigger.gen = 0
    return trigger

class VoiceTool:
    def __init__(self, recorder=None, transcriber=None, cleaner=None, with_ui=True):

        self._keys_gen = 0
        self.triggers, self._shared = {}, {}
        self._build_keys()
        self.active = None
        self.events = queue.Queue()
        self.jobs = queue.Queue()
        self.ui = queue.Queue()
        self.recorder = recorder or Recorder()
        self.transcribe = transcriber or Transcriber()
        self._busy_lock = threading.Lock()
        self._timers = set()
        self.mouse_hook = None
        self.with_ui = with_ui
        self.light = None
        self.card = None
        self.icon = None
        self.listener = None
        self.settings = None
        self.app_window = None
        self.test_mode = False
        self._hook_gen = 0
        self._last_tick = None
        self._stuck_logged = False
        self._quitting = False
        self._hook_restarted_at = float("-inf")

    def _filter(self, msg, data):
        try:
            return self._key_event(msg, data)
        except _SUPPRESS:
            raise
        except Exception:

            log.exception("keyboard hook error (the key was passed on)")
            return True

    def _key_event(self, msg, data):
        if data.flags & LLKHF_INJECTED:
            return True
        if data.vkCode == VK_LCONTROL and data.scanCode == ALTGR_CTRL_SCAN:
            return True
        down = msg in (win32con.WM_KEYDOWN, win32con.WM_SYSKEYDOWN)
        trigger = self.triggers.get(data.vkCode)
        if trigger and not (CONFIG.get("enabled", True) and CONFIG.get(f"{trigger.name}_enabled", True)):
            trigger = None
        if trigger is None and CONFIG.get("enabled", True) and CONFIG.get("typed_fix", True):
            trigger = self._shared.get(data.vkCode)
        if trigger:
            trigger.hook_at = time.monotonic()
            self.events.put(("trigger", trigger, down, trigger.hook_at))
            if not trigger.let_through:
                self.listener.suppress_event()
            if down:
                if not trigger.hook_down:
                    trigger.hook_down, trigger.hook_alone = True, True
                    if ((trigger.name == "teach" or trigger.teach_with is not None) and trigger.language
                            and trigger.let_through and CONFIG.get("typed_fix", True)
                            and win32api.GetAsyncKeyState(win32con.VK_SHIFT) & 0x8000):
                        win32api.keybd_event(trigger.vk, 0, 0, 0)
                        win32api.keybd_event(MASK_VK, 0, 0, 0)
                        win32api.keybd_event(MASK_VK, 0, win32con.KEYEVENTF_KEYUP, 0)
                        self.listener.suppress_event()
            else:
                trigger.hook_down = False
                if trigger.hook_alone and trigger.masks_lone_press:

                    win32api.keybd_event(MASK_VK, 0, 0, 0)
                    win32api.keybd_event(MASK_VK, 0, win32con.KEYEVENTF_KEYUP, 0)
                    win32api.keybd_event(trigger.vk, 0, win32con.KEYEVENTF_KEYUP, 0)
                    self.listener.suppress_event()
        elif down:
            for t in self.triggers.values():
                t.hook_alone = False
            self.events.put(("other", None, data.vkCode, time.monotonic()))
        return False

    def run(self, hardware=True):

        import win32gui
        previous = win32gui.GetForegroundWindow()
        root = tk.Tk()

        root.report_callback_exception = lambda kind, value, tb: log.error(
            "window callback failed", exc_info=(kind, value, tb))
        self.light = Light(root, previous)
        self.card = ResultCard(root)
        threading.Thread(target=self._worker, daemon=True).start()
        if hardware:
            self._install_hook()
            threading.Thread(target=self._watch_keys, daemon=True, name="key watchdog").start()
            threading.Thread(target=self._watch_daily, daemon=True, name="daily watch").start()
            log.info("ready: hold %s to talk; double-tap to lock on, tap again to stop", light_keys.resolve(CONFIG)[0])
            beep((880, 60), (1320, 60))
            if light_start:
                light_start.started()
        if hardware:
            self._start_tray()
        root.after(30, self._tick)
        root.mainloop()

    def _install_hook(self):

        gen = self._hook_gen + 1
        old = self.listener
        heard = []

        def on_key(msg, data):
            if gen != self._hook_gen:
                return True
            if not heard:
                heard.append(True)
                log.info("keyboard hook is hearing keys")
            return self._filter(msg, data)

        listener = KeyboardHook(on_key)
        listener.start()
        if not listener.wait() or not listener.installed:
            log.error("Windows refused the keyboard hook (error %s)", listener.error)
        self.listener = listener
        self._hook_gen = gen
        if old is not None:
            try:
                old.stop()
            except Exception:
                log.exception("could not stop the old keyboard hook")

    def _watch_keys(self):

        while True:
            time.sleep(WATCH_EVERY_SECONDS)
            try:
                self._check_keys(time.monotonic(), lambda vk: bool(win32api.GetAsyncKeyState(vk) & 0x8000))
            except Exception:
                log.exception("keyboard watchdog check failed")
                time.sleep(5)

    def _check_keys(self, now, key_is_down):

        if self._quitting:
            return
        if self.listener is not None and not self.listener.is_alive() and now - self._hook_restarted_at >= 2:
            self._hook_restarted_at = now
            log.warning("keyboard hook had stopped; re-installing it")
            self._install_hook()
            log.info("keyboard hook re-installed")
        for t in self.triggers.values():
            enabled = CONFIG.get("enabled", True) and CONFIG.get(f"{t.name}_enabled", True)

            if not enabled or not t.let_through:
                t.missed_down_since = t.missed_up_since = None
                continue
            down = key_is_down(t.vk)
            if down and t.vk == VK_LCONTROL and key_is_down(win32con.VK_RMENU):
                down = False
            if down and not t.hook_down:

                t.missed_up_since = None
                if t.missed_down_since is None:
                    t.missed_down_since = now
                elif t.missed_down_since != -1 and now - t.missed_down_since >= HOOK_MISSED_PRESS_SECONDS:
                    t.missed_down_since = -1
                    log.warning("keyboard hook missed a %s press (in %s); re-installing it", t.name,
                                self._front_window().get("app") or "?")
                    self._install_hook()
                    log.info("keyboard hook re-installed")
            elif not down and t.hook_down and now - t.hook_at >= HOOK_MISSED_RELEASE_SECONDS:

                t.missed_down_since = None
                if t.missed_up_since is None:
                    t.missed_up_since = now
                elif now - t.missed_up_since >= HOOK_MISSED_RELEASE_SECONDS:
                    since, t.missed_up_since = t.missed_up_since, None
                    t.hook_down = False
                    self.events.put(("trigger", t, False, since))
                    log.warning("keyboard hook missed a %s release (in %s); handled it and re-installing the hook",
                                t.name, self._front_window().get("app") or "?")
                    self._install_hook()
                    log.info("keyboard hook re-installed")
            elif not down:
                t.missed_down_since = None
                if not t.hook_down:
                    t.missed_up_since = None
            else:
                t.missed_up_since = None
                if t.missed_down_since != -1:
                    t.missed_down_since = None
        stuck = 0.0 if self._last_tick is None else now - self._last_tick
        if stuck >= LOOP_STUCK_SECONDS and not self._stuck_logged:
            self._stuck_logged = True
            import traceback
            frame = sys._current_frames().get(threading.main_thread().ident)
            where = "".join(traceback.format_stack(frame)[-6:]) if frame is not None else "?"
            log.warning("the window loop has been stuck for %.1fs (keys wait until it's free); it is in:\n%s", stuck, where)
        elif stuck < LOOP_STUCK_SECONDS and self._stuck_logged:
            self._stuck_logged = False
            log.info("the window loop is running again")

    def _icon_image(self, paused=False):
        return settings_window.icon_image(paused)

    def _refresh_icon(self):
        if getattr(self, "icon", None) is not None:
            self.icon.icon = self._icon_image(not CONFIG.get("enabled", True))
            self.icon.title = self._tooltip()
            self.icon.update_menu()

    def _start_tray(self):
        try:
            import pystray
        except Exception:
            log.warning("tray icon unavailable (pystray not installed)")
            self.icon = None
            return
        ask = self.ui.put
        menu = pystray.Menu(
            pystray.MenuItem("Open Chirpy", lambda: ask(self._open_app), default=True),
            pystray.MenuItem("Settings", lambda: ask(self._open_settings)),
            pystray.MenuItem("Pause", lambda: ask(lambda: self._pause(CONFIG.get("enabled", True))),
                             checked=lambda item: not CONFIG.get("enabled", True)),
            pystray.MenuItem("Send feedback", lambda: ask(self._send_feedback)),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quit Chirpy", lambda: ask(self._quit)),
        )
        self.icon = pystray.Icon("VoiceTool", self._icon_image(not CONFIG.get("enabled", True)), self._tooltip(), menu)
        self.icon.run_detached()

    def _send_feedback(self):

        url = light_keys.feedback_link(light_keys.feedback_url(CONFIG), light_keys.app_version(HERE))
        try:
            os.startfile(url)
        except Exception:
            log.exception("could not open the feedback page")
            self._card("Send feedback", "Chirpy couldn't open your browser. You can write to us at " + light_keys.feedback_url(CONFIG), seconds=30)

    def _tooltip(self):
        if not CONFIG.get("enabled", True):
            return "Chirpy: paused"
        return f"Chirpy: hold {light_keys.label(light_keys.resolve(CONFIG)[0])} to talk"

    def _open_app(self, page="home"):

        try:
            if self.app_window is None:
                import app_window_win
                import ui_api
                api = ui_api.API(WinHost(self), CONFIG, settings_window.save)
                mode = {"hidden": "hidden", "1": True}.get(os.environ.get("VOICETOOL_TEST_WINDOW", ""), self.test_mode)
                python = os.environ.get("VOICETOOL_TEST_WINDOW_PYTHON") if self.test_mode else None
                self.app_window = app_window_win.AppWindow(
                    api, log, on_failed=lambda reason: self.ui.put(lambda: self._window_failed(reason)), test_mode=mode,
                    python=python)
            self.app_window.show(page)
        except Exception:
            log.exception("could not open the app window")
            self._open_settings_tk()

    def _open_settings(self):
        self._open_app("settings")

    def _window_failed(self, reason):
        log.error("the app window did not start (%s); opening the simple settings window", reason[:200])
        self._open_settings_tk()

    def _open_settings_tk(self):

        if self.settings is not None and self.settings.win.winfo_exists():
            self.settings.win.deiconify()
            self.settings.win.lift()
            self.settings.win.focus_force()
            return
        self.settings = settings_window.SettingsWindow(self.light.root, CONFIG, {
            "quit": self._quit,
            "paused": lambda paused: self._pause(paused, from_settings=True),
        })

    def _pause(self, paused, from_settings=False):
        if not from_settings:
            settings_window.save(CONFIG, {"enabled": not paused})
            if self.settings is not None and self.settings.win.winfo_exists():
                self.settings.pause.set(paused)
        if self.active is not None:
            self._drop(self.active)
        log.info("paused" if paused else "resumed")
        self._refresh_icon()

    def _quit(self):
        log.info("quit from the menu")
        self._quitting = True
        try:
            if self.listener is not None:
                self.listener.stop()
            if self.app_window is not None:
                self.app_window.close()
            if self.icon is not None:
                self.icon.stop()
        finally:
            self.light.window.destroy()
            self.light.root.destroy()

    def _light(self, state):
        if self.light is not None:
            self.light.set(state)

    def _card(self, title, body, seconds=60, speak=False, buttons=None):

        if self.with_ui:
            self.ui.put(lambda: self.card.show(title, body, buttons=buttons, seconds=seconds))

    def _tick(self):
        self._last_tick = time.monotonic()
        try:
            self._tick_once()
        except Exception:
            if not self._quitting:
                log.exception("window loop step failed")
        finally:

            try:
                self.light.root.after(30, self._tick)
            except tk.TclError:
                pass

    def _tick_once(self):
        try:
            while True:
                kind, trigger, value, at = self.events.get_nowait()
                self._handle(kind, trigger, value, at)
        except queue.Empty:
            pass
        except Exception:
            log.exception("key handling failed")
        try:
            while True:
                self.ui.get_nowait()()
        except queue.Empty:
            pass
        except Exception:
            log.exception("window update failed")
        now = time.monotonic()
        t = self.active
        if t is not None and t.state == HOLDING and not t.mic_on and now - t.pressed_at >= t.mic_delay:
            if not self._open_mic(t):
                self._drop(t)
                t = self.active
        self._watch_mouse()
        if t is not None and t.state == TAP_WAIT and now > t.tap_deadline and not t.key_down:
            self._drop(t)
        elif t is not None and t.state == HOLDING and not t.cued and now - t.pressed_at >= CONFIG["hold_seconds"]:

            t.cued = True
            log.info("%s key held: listening", t.name)
            self.light.set(t.looks[0])
            beep((880, 40) if t.name == "dictate" else (740, 40))
        self.light.level = self.recorder.level
        self.light.draw()

    def _watch_mouse(self):

        t = self.active
        want = t is not None and t.ctrl and t.key_down
        if want and self.mouse_hook is None:
            self.mouse_hook = hook = MouseHook(self._mouse_event)
            hook.start()
        elif not want and self.mouse_hook is not None:
            hook, self.mouse_hook = self.mouse_hook, None
            hook.stop()

    def _mouse_event(self, msg, data):

        if msg in MOUSE_USED and not data.flags & LLMHF_INJECTED:
            self.events.put(("mouse", None, msg, time.monotonic()))
        return False

    def _open_mic(self, t):

        try:
            self.recorder.start()
        except Exception:
            log.exception("could not open the microphone")
            beep((300, 300))
            self._card("No microphone", MIC_TEXT, seconds=30)
            return False
        t.mic_on = True
        return True

    def _drop(self, t, quietly=True):
        self.recorder.stop()
        t.mic_on = False
        t.pending = None
        t.state = IDLE
        t.swallow_release = t.key_down
        self.active = None
        self._light(None)
        if not quietly:
            log.info("cancelled")
            beep((400, 120))

    def _shortcut_used(self, t):

        if t.state in (HOLDING, TEACH):
            self._drop(t)
        elif t.pending == "lock":
            self._drop(t)
        elif t.pending == "stop":
            t.pending = None

    def _handle(self, kind, trigger, value, at):
        t = self.active
        if kind == "mouse":
            if t is not None and t.ctrl and t.key_down:
                self._shortcut_used(t)
            return
        if kind == "other":
            if t is None:
                return
            if value == win32con.VK_ESCAPE and t.state in (HOLDING, LOCKED, TAP_WAIT) and (t.mic_on or not t.ctrl):
                self._drop(t, quietly=False)
            else:
                self._shortcut_used(t)
            return
        t, down = trigger, value
        if t.gen != self._keys_gen:
            return
        if t.name == "teach":
            self._teach_key(t, down)
            return
        if t.teach_with is not None and self._chord(t, down):
            return
        if self.active is not None and self.active is not t:
            if down and not t.key_down:
                log.info("%s key ignored: the %s key is still %s", t.name, self.active.name, self.active.state)
            t.key_down = down
            return
        if down:
            if t.key_down:
                return
            t.key_down = True
            if t.ctrl and t.state in (LOCKED, TAP_WAIT):
                t.pending = "stop" if t.state == LOCKED else "lock"
            elif t.state == LOCKED:
                self._finish(t)
                t.swallow_release = True
            elif t.state == TAP_WAIT:
                t.swallow_release = True
                self._hands_free_on(t)
            elif (t.state == IDLE and t.name == "command" and CONFIG.get("typed_fix", True)
                  and win32api.GetAsyncKeyState(win32con.VK_SHIFT) & 0x8000):
                t.state, t.capture, t.capturing = TEACH, Capture(), False
                self.active = t
            elif t.state == IDLE:

                if t.mic_delay <= 0 and not self._open_mic(t):
                    return
                t.state, t.pressed_at, t.cued = HOLDING, at, False
                t.capture, t.capturing = Capture(), False
                t.app = self._front_window() if t.name == "dictate" else {}
                self.active = t
        else:
            t.key_down = False
            pending, t.pending = t.pending, None
            if pending == "stop":
                self._finish(t)
                return
            if pending == "lock":
                if not t.mic_on and not self._open_mic(t):
                    self._drop(t)
                    return
                self._hands_free_on(t)
                if t.name == "command" and not t.capturing:
                    t.capturing = True
                    threading.Thread(target=self._capture, args=(t, t.capture), daemon=True).start()
                return
            if t.swallow_release:
                t.swallow_release = False
                return
            if t.state == TEACH:
                t.state = IDLE
                self.active = None
                threading.Thread(target=self._teach, args=(t,), daemon=True).start()
                return
            if t.state != HOLDING:
                return

            held = (t.mic_on or not t.ctrl) and at - t.pressed_at >= CONFIG["hold_seconds"]
            if t.name == "command" and not t.capturing and (held or not t.ctrl):
                t.capturing = True
                threading.Thread(target=self._capture, args=(t, t.capture), daemon=True).start()
            if held:
                self._finish(t)
            else:
                t.state = TAP_WAIT
                t.tap_deadline = at + CONFIG["double_tap_seconds"]

    DAILY_CHECK_SECONDS = 1.5
    _pasting = False

    def _watch_daily(self, check=None, every=None):

        check = check or daily_running
        while not self._quitting:
            try:
                if check():
                    self.ui.put(self._step_aside)
                    return
            except Exception:
                log.exception("could not check for the everyday VoiceTool")
            time.sleep(every or self.DAILY_CHECK_SECONDS)

    def _step_aside(self):

        log.warning("the everyday VoiceTool started; Chirpy is stepping aside and closing")
        self._quitting = True
        if self.active is not None:
            self._drop(self.active)
        for _ in range(75):
            if not self._pasting:
                break
            time.sleep(0.02)
        try:
            notice(DAILY_STARTED_TEXT)
        except Exception:
            log.exception("could not show the stepping-aside message")
        self._quit()

    def _build_keys(self):

        dictate_key, teach_key = light_keys.resolve(CONFIG)
        self._keys_gen += 1
        dictate = make_trigger("dictate", dictate_key)
        teach = make_trigger("teach", teach_key)
        teach.masks_lone_press = False
        dictate.gen = teach.gen = self._keys_gen
        triggers, shared = {dictate.vk: dictate}, {}
        if teach.vk == dictate.vk:
            dictate.teach_with = teach
            shared = {teach.vk: teach}
        else:
            triggers[teach.vk] = teach
        self._shared = shared
        self.triggers = triggers

    def _apply_keys(self):

        if self.active is not None:
            self._drop(self.active)
        self._build_keys()
        dictate_key, teach_key = light_keys.resolve(CONFIG)
        log.info("keys changed: dictate %s, teach Shift + %s", dictate_key, teach_key)
        self._refresh_icon()

    def _chord(self, t, down):

        teach = t.teach_with
        if down:
            if t.chord:
                return True
            if t.key_down or not win32api.GetAsyncKeyState(win32con.VK_SHIFT) & 0x8000:
                return False
            if t.state == TAP_WAIT:
                self._drop(t)
            elif t.state != IDLE:
                return False
            if self.active is not None and self.active is not t:
                return False
            if not CONFIG.get("typed_fix", True):
                t.chord = "ignored"
                return True
            t.chord = "teach"
            self._teach_key(teach, True)
            return True
        chord, t.chord = t.chord, None
        if chord == "teach":
            self._teach_key(teach, False)
        return bool(chord)

    def _teach_key(self, t, down):

        if self.active is not None and self.active is not t:
            t.key_down = down
            return
        if down:
            if t.key_down:
                return
            t.key_down = True
            if (t.state == IDLE and CONFIG.get("enabled", True) and CONFIG.get("typed_fix", True)
                    and win32api.GetAsyncKeyState(win32con.VK_SHIFT) & 0x8000):
                t.state, t.capture, t.capturing = TEACH, Capture(), False
                self.active = t
            return
        t.key_down = False
        t.swallow_release = False
        if t.state == TEACH:
            t.state = IDLE
            self.active = None
            threading.Thread(target=self._teach, args=(t,), daemon=True).start()

    def _hands_free_on(self, t):

        t.state = LOCKED
        self._light(t.looks[1])
        log.info("%s hands-free on", t.name)
        beep((1320, 50), (1320, 50))

    @staticmethod
    def _front_window():
        try:
            return commands.window_info()
        except Exception:
            return {}

    def _capture(self, t, box=None):

        box = box or t.capture
        try:
            time.sleep(0.05)
            box.context = commands.capture_context(_open_clipboard, save_clipboard, restore_clipboard)
        except Exception:
            log.exception("could not read the screen context")
            box.context = {}

    def _teach(self, t):

        import win32gui
        target = win32gui.GetForegroundWindow()
        for _ in range(50):
            if not win32api.GetAsyncKeyState(win32con.VK_SHIFT) & 0x8000:
                break
            time.sleep(0.02)
        self._capture(t)
        context = dict(t.context or {})
        selection = context.get("selection", "")
        self.ui.put(lambda: TeachWindow(self.light.root, selection, target,
                                        lambda heard, meant, target, selection: self._learn_typed(heard, meant, target, selection, context)))

    def _learn_typed(self, heard, meant, target, selection, context=None):

        if heard and target:
            if bring_to_front(target):
                time.sleep(0.1)
                context = context or {}
                self._deliver_now(keep_punctuation(selection, meant), target, "fix", context if commands.has_selection(context) else None,
                                  kept="Copy the corrected text here.")
            else:
                log.warning("couldn't switch back to the app; fix learned but not pasted")
        self._remember(heard, meant, "typed fix")

    def _remember(self, heard, meant, source):

        heard = strip_punctuation(heard)
        if heard:
            result = corrections.add_saved(heard, meant, source)
            if result["saved"]:
                body = f"From now on, “{heard}” becomes “{meant}”."
                if len(meant.split()) <= 3:
                    word = corrections.remember_word(meant)
                    if not word["saved"]:
                        body += f" Chirpy couldn't add “{meant}” to your word list."
                if result["kept"]:
                    body += " " + light_files.kept_note(result["kept"])
                log.info("learned a fix (%s): %r -> %r", source, heard, meant)
                self._card("Learned", body)
            elif result["failed"]:
                log.warning("a fix was not saved: %s", result["error"])
                self._card("Couldn't save", light_files.NOT_SAVED)
        elif meant:
            word = corrections.remember_word(meant)
            if word["saved"]:
                log.info("added word: %r", meant)
                self._card("Learned", f"“{meant}” added to your word list." + (" " + light_files.kept_note(word["kept"]) if word["kept"] else ""))
            else:
                log.warning("a word was not saved: %s", word["error"])
                self._card("Couldn't save", light_files.NOT_SAVED)

    def _finish(self, t):
        audio = self.recorder.stop()
        t.mic_on = False
        t.state = IDLE
        self.active = None
        beep((660, 40))
        peak = float(np.abs(audio).max()) if len(audio) else 0.0
        if len(audio) > RATE * 0.3 and peak < CONFIG.get("min_peak", 0.005):
            log.info("%.1fs audio too quiet (peak %.4f), skipped", len(audio) / RATE, peak)
            self._light(None)
            if len(audio) >= RATE * 2:

                try:
                    sd._terminate()
                    sd._initialize()
                    self.recorder.device = find_mic()
                    log.info("microphone list refreshed after a silent try (device %s)", self.recorder.device)
                except Exception as e:
                    log.warning("microphone refresh failed: %s", e)
                self._card("Didn't hear you", "Your microphone came through almost silent, so I reconnected to it. "
                           "Please try again. If it happens again, check the mic is plugged in and not muted, "
                           "and speak into the end of it.", seconds=12)
            elif len(audio) >= RATE * 0.7:
                self._card("Didn't hear you", "Your microphone is coming through almost silent, so nothing was typed. "
                           "Check it's plugged in and not muted, and speak into the end of the mic.", seconds=10)
        elif len(audio) > RATE * 0.3:
            self._light("working" if t.name == "dictate" else "thinking")

            self.jobs.put((Job(t, target=(self._front_window() or {}).get("hwnd")), audio))
        else:
            held = time.monotonic() - t.pressed_at
            if held >= 1.0:
                log.warning("key held %.1fs but the microphone gave only %.2fs of sound; nothing typed",
                            held, len(audio) / RATE)
            self._light(None)

    def _worker(self):
        while True:
            t, audio = self.jobs.get()
            try:
                self._dictate(t, audio)
            except Exception:
                log.exception("%s failed", t.name)
                self._light("error")
                beep((300, 300))

    def _wait_for_release(self, t):

        for _ in range(3000):
            if not any(x.key_down for x in self.triggers.values()):
                return
            time.sleep(0.02)

    @staticmethod
    def _recopy_selection():

        return commands.selected_text(_open_clipboard, save_clipboard, restore_clipboard)

    def _window_problem(self, context, now):

        if "title" in context and "title" in now and context["title"] != now["title"]:
            return "changed"
        before, after = context.get("focus") or 0, now.get("focus") or 0
        if before and after and before != after:
            return "changed"
        if "title" not in context or "title" not in now or bool(before) != bool(after):
            return "unchecked"
        return None

    def _selection_problem(self, context, now, recopy=True):

        try:
            if context.get("selection_cut"):
                return "too long"
            if (context.get("app") or "").lower() in commands.NO_COPY_APPS or (now.get("app") or "").lower() in commands.NO_COPY_APPS:
                return "unchecked"
            problem = self._window_problem(context, now)
            if problem or not context.get("selection_id"):
                return problem or "unchecked"
            if not recopy:
                return None
            again = self._recopy_selection()
            if not (again or "").strip():
                return "unchecked"
            return None if commands.selection_identity(again) == context["selection_id"] else "changed"
        except Exception:
            log.exception("could not check that the selection is unchanged")
            return "unchecked"

    def _deliver(self, t, text, target, what, replacing=None):

        self._wait_for_release(t)
        return self._deliver_now(text, target, what, replacing)

    def _delivery_problem(self, target, now, what, replacing, recopy=True):

        here = now.get("hwnd")
        if not target or not here:
            log.warning("%s not typed: couldn't tell which window it belongs in", what)
            return ("Not typed: unknown window",
                    f"Chirpy couldn't tell which window your {what} was meant for, so it wasn't typed. " if not target else
                    f"Chirpy couldn't tell which window you're in now, so your {what} wasn't typed. ")
        if here != target:
            log.warning("%s not typed: the window in front changed while it was being written", what)
            return "Not typed: you switched windows", f"Your {what} wasn't typed into the window you're in now. "
        if replacing:
            problem = self._selection_problem(replacing, now, recopy)
            if problem:
                log.warning("%s not typed over the selection: %s", what, problem)
                if problem == "changed":
                    return ("Not replaced: the selection changed",
                            f"The text you had selected has changed, so your {what} wasn't put over what's selected now. ")
                if problem == "too long":
                    return ("Not replaced: the selection was too long",
                            f"Only the first part of what you had selected could be used for your {what}, so it wasn't put "
                            "over the whole selection. ")
                return ("Not replaced: couldn't check the selection",
                        f"Chirpy couldn't check that your selected text is still the same, so your {what} wasn't put over it. ")
        return None

    def _deliver_now(self, text, target, what, replacing=None, kept="It's saved in History, or copy it here."):

        problem = self._delivery_problem(target, self._front_window() or {}, what, replacing)
        if problem is None and replacing:

            problem = self._delivery_problem(target, self._front_window() or {}, what, replacing, recopy=False)
        if problem:
            self._card(problem[0], problem[1] + kept,
                       seconds=60, buttons=[("Copy", lambda: set_clipboard_text(text)), ("Close", lambda: None)])
            return False
        if self._quitting:
            log.info("%s not typed: Chirpy is closing", what)
            return False
        self._pasting = True
        try:
            paste(text)
        finally:
            self._pasting = False
        return True

    @staticmethod
    def _target(t, context=None):

        if context and context.get("selection") and context.get("hwnd"):
            return context["hwnd"]
        return getattr(t, "target", None) or (context or {}).get("hwnd") or (getattr(t, "app", None) or {}).get("hwnd")

    def _done(self, ok=True):
        if self.jobs.empty() and self.active is None:
            self._light(None if ok else "error")

    def _history(self, entry):
        entry = {"at": time.strftime("%Y-%m-%d %H:%M:%S"), **entry}
        light_files.append_history(HERE / "history.jsonl", json.dumps(entry) + "\n")

    def _dictate(self, t, audio):

        t0 = time.perf_counter()
        raw = self.transcribe(audio)
        t1 = time.perf_counter()
        text, fixes = corrections.apply_counted(raw)
        text = spell_words(text, known_words())
        app = t.app or {}
        log.info("%.1fs audio (peak %.3f) | speech %.2fs | %d words",
                 len(audio) / RATE, float(np.abs(audio).max()), t1 - t0, len(text.split()))
        self._history({"raw": raw, "text": text, "app": app.get("app") or "", "fixes": fixes})
        target = self._target(t)
        last = getattr(self, "_last_insert", None)
        before = last["text"] if last and last["hwnd"] == target and time.monotonic() - last["at"] <= light_keys.CONTINUE_SECONDS else ""
        typing = light_keys.continue_dictation(text, before)
        typed = bool(text) and self._deliver(t, typing, target, "dictation")
        if typed:
            self._last_insert = {"hwnd": target, "text": typing, "at": time.monotonic()}
        self._done(typed or not text)

class WinHost:

    def __init__(self, tool):
        self.tool = tool
        self._signed_in = (0.0, False)

    def computer_name(self):
        return os.environ.get("COMPUTERNAME") or "This PC"

    def paused(self, paused):
        self.tool.ui.put(lambda: self.tool._pause(paused, from_settings=True))

    def settings_notice(self):
        return _NOTICE["text"] or ""

    def notice_seen(self):
        _NOTICE["text"] = None

    def send_feedback(self):
        self.tool._send_feedback()

    def startup_enabled(self):
        return settings_window.startup_enabled()

    def set_startup(self, on):
        settings_window.set_startup(on)

    def open_path(self, path):
        os.startfile(str(path))

    def copy_text(self, text):
        set_clipboard_text(text)

    def quit(self):
        self.tool.ui.put(self.tool._quit)

    def theme(self):
        import app_window_win
        return app_window_win.windows_theme()

    def theme_changed(self, theme):
        window = self.tool.app_window
        if window is not None:
            window.set_theme(theme)

    def keys_changed(self):
        self.tool.ui.put(self.tool._apply_keys)

OPEN_SETTINGS_EVENT = "VoiceToolLight-open-settings"
MUTEX_NAME = "VoiceToolLight-single-instance"
DAILY_MUTEX = "VoiceTool-single-instance"
DAILY_RUNNING_TEXT = ("Your everyday VoiceTool is running, and both can't listen for keys at once. "
                      "Quit VoiceTool from its icon by the clock, then start Chirpy again.")

def daily_running():

    try:
        handle = win32event.OpenMutex(0x00100000, False, DAILY_MUTEX)
    except Exception:
        return False
    try:
        win32api.CloseHandle(handle)
    except Exception:
        pass
    return True

def tell(text):
    ctypes.windll.user32.MessageBoxW(None, text, "Chirpy", 0x40 | 0x10000)

DAILY_STARTED_TEXT = ("Your everyday VoiceTool started, so Chirpy stepped aside and closed: both can't listen "
                      "for keys at once. To use Chirpy, quit the everyday VoiceTool, then start Chirpy again.")

def notice(text):

    pythonw = Path(sys.executable).with_name("pythonw.exe")
    code = ("import ctypes, sys; ctypes.windll.user32.MessageBoxW(None, sys.argv[1], 'Chirpy', 0x40 | 0x10000)")
    subprocess.Popen([str(pythonw), "-s", "-E", "-c", code, text], creationflags=subprocess.CREATE_NO_WINDOW)

class _TestRecorder:

    device = None
    level = 0.0

    def start(self):
        pass

    def stop(self):
        return np.zeros(0, np.float32)

def hide_console_windows():

    original = subprocess.Popen.__init__
    if getattr(original, "hides_console", False):
        return

    def init(self, *args, **kwargs):
        if not kwargs.get("creationflags") and len(args) < 14:
            kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
        original(self, *args, **kwargs)
    init.hides_console = True
    subprocess.Popen.__init__ = init

def _open_log():

    log.removeHandler(_early)
    log.addHandler(_handler)
    _early.setTarget(_handler)
    _early.close()

def _start_settings():

    problem, notice = light_keys.start(HERE / "config.json", CONFIG)
    left = light_files.cleanup(HERE)
    if left:
        log.warning("removed a temporary file a cut-off save left: " + ", ".join(left))
    if problem:
        log.warning(problem)
    _NOTICE["text"] = notice

def main():
    hide_console_windows()
    screen_layout.make_dpi_aware()

    test = os.environ.get("VOICETOOL_TEST") == "1"
    suffix = os.environ.get("VOICETOOL_INSTANCE", "") or ("test" if test else "")
    mutex_name = MUTEX_NAME + (f"-{suffix}" if suffix else "")
    event_name = OPEN_SETTINGS_EVENT + (f"-{suffix}" if suffix else "")
    if test:
        os.environ.setdefault("VOICETOOL_TASK", "VoiceTool-test-none")
        light_keys.force(CONFIG, beeps=False)

    show_window = "--settings" in sys.argv
    if not test and daily_running():
        log.warning("not started: the everyday VoiceTool is running")
        tell(DAILY_RUNNING_TEXT)
        return
    mutex = win32event.CreateMutex(None, False, mutex_name)
    if win32api.GetLastError() == winerror.ERROR_ALREADY_EXISTS:
        if show_window:
            try:
                import ctypes
                ctypes.windll.user32.AllowSetForegroundWindow(-1)
                win32event.SetEvent(win32event.OpenEvent(win32event.EVENT_MODIFY_STATE, False, event_name))
            except Exception:
                log.exception("could not reach the running VoiceTool")
        else:
            log.info("already running")
        return
    _open_log()
    if not test:

        try:
            import win32process
            win32process.SetPriorityClass(win32api.GetCurrentProcess(), win32process.ABOVE_NORMAL_PRIORITY_CLASS)
        except Exception as e:
            log.warning("could not raise priority: %s", e)
    tool = None
    requested = threading.Event()

    def wait_for_requests(event):
        while True:
            win32event.WaitForSingleObject(event, win32event.INFINITE)
            if tool is not None:
                tool.ui.put(tool._open_app)
            else:
                requested.set()
    try:

        event = win32event.CreateEvent(None, False, False, event_name)
        threading.Thread(target=wait_for_requests, args=(event,), daemon=True).start()
        _start_settings()
        if test:
            time.sleep(float(os.environ.get("VOICETOOL_TEST_START_DELAY", "0")))
            tool = VoiceTool(recorder=_TestRecorder(), transcriber=lambda audio: "")
            tool.test_mode = True
        else:
            tool = VoiceTool()
        if show_window or requested.is_set():
            tool.ui.put(tool._open_app)
        tool.run(hardware=not test)
    except Exception:
        log.exception("VoiceTool stopped")
        raise
    finally:
        del mutex

if __name__ == "__main__":
    main()
