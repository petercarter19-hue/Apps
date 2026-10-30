"""VoiceTool: hold Left Alt anywhere, talk, let go, and the text is pasted at the cursor.
Hold the Left Windows key and say what to do: rewrite the selection, fix a word, write something, answer, open things,
jot a note, translate, journal, run meeting notes (if turned on), or have Claude act (if turned on).

Double-tap the key to keep the mic on hands-free; tap once more to stop. Esc cancels (and stops a spoken answer).
Everything runs on this computer: speech-to-text with Parakeet, tidy-up and commands with a small model (Qwen3 4B on
llama.cpp, see local_cleanup_win.py). Azure OpenAI is an advanced option, off unless "offline_only" is turned off and an
Azure endpoint is set in config.json.
The VoiceTool window (ui/index.html, shown by app_window_win.py) is opened from the tray icon or the Start menu.
Settings live in config.json, known words in words.txt. Log: voicetool.log. History: history.jsonl.
"""
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
import local_cleanup_win
import settings_window
import styles
import usage

try:  # what pynput's suppress_event() raises; the hook must let it through, and nothing else
    from pynput._util.win32 import SystemHook
    _SUPPRESS = SystemHook.SuppressException
except Exception:  # (not on Windows)
    class _SUPPRESS(Exception):
        pass

HERE = Path(__file__).resolve().parent
CONFIG = json.loads((HERE / "config.json").read_text(encoding="utf-8-sig"))  # (Notepad and PowerShell can add a BOM)
RATE = 16000
MAX_CHUNK_SECONDS = 25  # Parakeet works best on clips under ~30 s; longer recordings are split at quiet points
LLKHF_INJECTED = 0x10
MASK_VK = 0xE8  # an unassigned key code, used to stop a lone Alt from opening menus
MEETING_AUDIO = HERE / "recordings" / "meetings"  # the two audio tracks while a meeting is recorded
CLAUDE_OFF_TEXT = "Email and calendar need Claude Code with your own Claude plan; turn it on in Settings."
SCREEN_OFF_TEXT = "Questions about the screen need the online option."

log = logging.getLogger("voicetool")
log.setLevel(logging.INFO)
_handler = logging.handlers.RotatingFileHandler(HERE / "voicetool.log", maxBytes=1_000_000, backupCount=2, encoding="utf-8")
_handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
log.addHandler(_handler)
if sys.stdout:
    log.addHandler(logging.StreamHandler(sys.stdout))
# pynput stops its keyboard listener (and so the hook) after any error in it, and only says so on stderr, which
# pythonw throws away. Send its warnings to our log so a stopped listener is never silent again.
logging.getLogger("pynput").setLevel(logging.WARNING)
logging.getLogger("pynput").addHandler(_handler)

# The keyboard watchdog (see VoiceTool._check_keys): how long a trigger key may be down, or up, before the tool
# decides the keyboard hook missed it; and how long the window loop may be stuck before that is logged.
HOOK_MISSED_PRESS_SECONDS = 0.4
HOOK_MISSED_RELEASE_SECONDS = 0.3
WATCH_EVERY_SECONDS = 0.05
LOOP_STUCK_SECONDS = 3.0


_BEEP_DIR = Path(os.environ.get("TEMP", HERE)) / "voicetool-sounds"


def _soft_tone_file(tones, volume):
    """A short, gentle WAV for these (frequency, ms) tones: quiet sine waves with soft fade in/out, cached on disk.
    (winsound.Beep can't be made quieter, so a soft tone is played from a file instead.)"""
    name = "_".join(f"{f}-{d}" for f, d in tones) + f"_v{int(volume * 100)}.wav"
    path = _BEEP_DIR / name
    if not path.exists():
        _BEEP_DIR.mkdir(parents=True, exist_ok=True)
        rate, parts = 22050, []
        for f, d in tones:
            t = np.arange(int(rate * d / 1000)) / rate
            tone = np.sin(2 * np.pi * f * t)
            fade = np.minimum(1, np.minimum(t, t[-1] - t) / 0.012) if len(t) else t  # 12 ms fade: no click
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
        pass  # a sound must never break dictation


def known_words():
    path = HERE / "words.txt"
    if not path.exists():
        return []
    return [w.strip() for w in path.read_text(encoding="utf-8").splitlines() if w.strip() and not w.startswith("#")]


# ---------- microphone ----------

def find_mic():
    want = CONFIG.get("mic_name_contains", "").lower()
    for i, d in enumerate(sd.query_devices()):
        if d["max_input_channels"] > 0 and d["hostapi"] == 0 and want and want in d["name"].lower():
            return i
    return None  # system default


class Recorder:
    def __init__(self):
        self.device = find_mic()
        self.frames = []
        self.stream = None
        self.level = 0.0  # loudness of the latest audio, for the recording light

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


# ---------- speech to text ----------

def speech_model_folder():
    """The speech model's folder (config.json "speech_model_path", relative to this folder) if it is there, else None."""
    value = CONFIG.get("speech_model_path") or ""
    if not value:
        return None
    import local_cleanup_win
    folder = local_cleanup_win.resolve(value, HERE)
    return folder if folder.is_dir() else None


SPEECH_FILES = ("config.json", "vocab.txt", "decoder_joint-model.int8.onnx", "encoder-model.int8.onnx")  # what Parakeet needs
SPEECH_MISSING_TEXT = "The speech model is missing. Open VoiceTool and click Finish setup."
SPEECH_INCOMPLETE_TEXT = "The speech model is incomplete (a file in it is missing or damaged). Open VoiceTool and click Finish setup."


class SpeechModelMissing(RuntimeError):
    """The speech model isn't there (or can't be loaded). VoiceTool stays open; "Finish setup" downloads it again."""


def speech_model_missing(transcriber=None):
    """True when dictation can't work: the speech model's folder or one of its files is gone, or it couldn't be loaded."""
    if getattr(transcriber, "missing", False):
        return True
    if not (CONFIG.get("speech_model_path") or ""):
        return False
    folder = speech_model_folder()
    return folder is None or any(not (folder / name).is_file() for name in SPEECH_FILES)


class Transcriber:
    """Speech to text. If the model is missing or broken this does NOT download anything by itself (that needs a package
    that isn't installed, and a download nobody asked for): it stays empty (`missing`), calling it raises
    SpeechModelMissing, and "Finish setup" fetches the model, after which reload() brings it in without a restart."""

    def __init__(self):
        self.model = None
        self.missing = True
        self.reload()

    def reload(self):
        """(Re)load the model from the folder in config.json; sets `missing`. Never raises."""
        t = time.perf_counter()
        configured = CONFIG.get("speech_model_path")
        folder = speech_model_folder()
        model = None
        self.problem = SPEECH_MISSING_TEXT
        try:
            if configured and folder is None:
                log.warning("the speech model folder %s isn't there; dictation waits for Finish setup", configured)
            elif configured:
                lost = [n for n in SPEECH_FILES if not (folder / n).is_file()]
                if lost:
                    log.warning("the speech model folder is incomplete; missing: %s", ", ".join(lost))
                self.problem = SPEECH_INCOMPLETE_TEXT
                model = onnx_asr.load_model(CONFIG["speech_model"], path=folder, quantization="int8")
            else:  # no folder named in config.json (a copy for developers): the default download cache
                model = onnx_asr.load_model(CONFIG["speech_model"], quantization="int8")
            if model is not None:
                model.recognize(np.zeros(RATE, np.float32), sample_rate=RATE)  # warm-up
        except Exception:
            log.exception("the speech model couldn't be loaded; dictation waits for Finish setup")
            model = None
        self.model, self.missing = model, model is None
        if model is not None:
            log.info("speech model ready in %.1fs", time.perf_counter() - t)
        return not self.missing

    def __call__(self, audio):
        if self.model is None:
            raise SpeechModelMissing(getattr(self, "problem", SPEECH_MISSING_TEXT))
        text = " ".join(t for t in (self.model.recognize(c, sample_rate=RATE).strip() for c in split_at_pauses(audio)) if t)
        return " ".join(text.replace("<unk>", " ").split())  # room noise can come out as "<unk>" markers; never words


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


# ---------- the model: on this computer (the default) or Azure OpenAI (advanced) ----------

MACHINE_KEYS = ("offline_backend", "offline_args", "offline_server", "offline_server_cpu", "offline_model",
                "speech_model_path", "offline_port")  # what fetch_models.configure() writes for this computer


def reload_machine_settings():
    """Read this computer's settings (MACHINE_KEYS) from config.json on disk into CONFIG. The downloader writes them
    in its own process; without this, the next time a switch is saved the older copy in memory would overwrite them."""
    try:
        disk = json.loads((HERE / "config.json").read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as e:
        log.warning("could not re-read config.json after the download: %s", e)
        return False
    for key in MACHINE_KEYS:
        if key in disk:
            CONFIG[key] = disk[key]
        else:
            CONFIG.pop(key, None)
    return True


class TidyUpNotSetUp(RuntimeError):
    """The on-device model (llama.cpp and the Qwen file) isn't installed yet; fetch_models.py downloads it."""


def azure_configured():
    return bool(str(CONFIG.get("azure_openai_base_url") or "").strip())


class Cleaner:
    """Every model call goes through `_client()`. With "offline_only" on (the default) that is a client for the model on
    this computer, and nothing about Azure is created, fetched or warmed up. Azure is built lazily, only when "offline_only"
    is off and an Azure endpoint is set."""

    def __init__(self):
        self._OpenAI = None  # (tests may set it) the OpenAI client class
        self._credential = None
        self._token = None
        self._openai = None
        self._local_client = None  # (port, client) for the model on this computer
        self._last_used = 0.0
        self._lock = threading.Lock()
        if not CONFIG.get("offline_only", False) and azure_configured():
            threading.Thread(target=self._client, daemon=True).start()  # fetch the sign-in token early

    def _openai_class(self):
        if self._OpenAI is None:
            from openai import OpenAI
            self._OpenAI = OpenAI
        return self._OpenAI

    def _client(self):
        """The client for the model calls: the one on this computer when "offline_only" is on, else Azure's."""
        if CONFIG.get("offline_only", False):
            return self._local_openai()
        return self._azure_client()

    def _local_openai(self):
        import local_cleanup_win
        if not local_cleanup_win.available(CONFIG):
            raise TidyUpNotSetUp("the tidy-up model isn't set up yet")
        cleaner = self._local_cleaner()
        cleaner.start()  # starts the server and waits for it if it isn't up (instant if it is)
        with self._lock:
            if self._local_client is None or self._local_client[0] != cleaner.port:
                self._local_client = (cleaner.port, cleaner.client(CONFIG.get("offline_timeout_seconds", 60)))
            return self._local_client[1]

    def _azure_client(self):
        # One client for the whole session, so the connection to Azure is reused; rebuilt only when the sign-in renews.
        if not azure_configured():
            raise RuntimeError("Azure isn't set up: there is no Azure endpoint in config.json")
        with self._lock:
            if self._credential is None:
                from azure.identity import AzureCliCredential
                # "azure_tenant_id" is optional: name it if the sign-in ever lands in the wrong tenant.
                self._credential = AzureCliCredential(tenant_id=CONFIG.get("azure_tenant_id") or None)
            if self._token is None or self._token.expires_on - time.time() < 300:
                self._token = self._credential.get_token("https://cognitiveservices.azure.com/.default")
                self._openai = self._openai_class()(base_url=CONFIG["azure_openai_base_url"], api_key=self._token.token,
                                                    timeout=CONFIG.get("cleanup_timeout_seconds", 4), max_retries=0)
            return self._openai

    def warm_up(self):
        """Called when the key goes down: if the connection to Azure has gone idle, reopen it while the user is still
        talking. Does nothing when everything stays on this computer."""
        if CONFIG.get("offline_only", False) or not azure_configured():
            return
        if time.time() - self._last_used < 60:
            return
        self._last_used = time.time()

        def ping():
            try:
                reply = self._client().chat.completions.create(
                    model=CONFIG["cleanup_model"], reasoning_effort="none", max_completion_tokens=16,
                    messages=[{"role": "user", "content": "ok"}])
                usage.azure(reply, CONFIG["cleanup_model"], "warm-up")
            except Exception as e:
                log.info("warm-up skipped: %s", str(e)[:120])
        threading.Thread(target=ping, daemon=True).start()

    local = None  # the on-device model's server (local_cleanup_win.LocalCleaner), made when it is wanted

    def _local_cleaner(self):
        import local_cleanup_win
        if self.local is None:
            self.local = local_cleanup_win.LocalCleaner(CONFIG, log)
        return self.local

    def sync_offline(self):
        """Start or stop the on-device model's server so it matches the switches (called at start-up, whenever one is
        changed, and when the model has just been downloaded). Starting never makes anyone wait: it loads in the
        background (a few seconds)."""
        import local_cleanup_win
        wanted = CONFIG.get("offline_tidyup", True) or CONFIG.get("offline_only", False)
        if wanted and local_cleanup_win.available(CONFIG):
            self._local_cleaner().start_in_background()
        elif self.local is not None:
            self.local.stop()

    def __call__(self, text, hint=""):
        """Tidy up dictated text. hint: extra words for the prompt (the app's style NAME; see styles.dictation_hint)."""
        if CONFIG.get("offline_only", False):  # everything stays on this computer: Azure is never asked
            return self._local(self._system(hint, small=True), text)
        try:
            return self._azure(self._system(hint), text)
        except Exception as e:
            if not CONFIG.get("offline_tidyup", True):
                raise
            import local_cleanup_win
            if not local_cleanup_win.available(CONFIG):
                raise
            log.info("Azure tidy-up unavailable (%s); tidying up on this PC", str(e)[:80])
            return self._local(self._system(hint, small=True), text)

    def _local(self, system, text):
        t = time.perf_counter()
        client = self._local_openai()  # TidyUpNotSetUp if the model isn't installed
        reply = client.chat.completions.create(
            model=CONFIG.get("cleanup_model") or "local", temperature=0,
            max_completion_tokens=max(64, int(len(text.split()) * 2.2) + 40),
            messages=[{"role": "system", "content": system}, {"role": "user", "content": text}])
        usage.azure(reply, "local", "tidy-up")  # logged as free
        choice = reply.choices[0]
        out = (choice.message.content or "").strip()
        # A reply that hit our own size limit is a cut-off sentence: refuse it, so the words are pasted as heard instead.
        if not out or choice.finish_reason == "length":
            raise RuntimeError("the tidy-up was cut off" if out else "the tidy-up came back empty")
        log.info("tidy-up on this PC %.2fs", time.perf_counter() - t)
        return out

    def _system(self, hint="", small=False):
        """The tidy-up instructions. small=True is a shorter version for the on-device model, which gets confused by
        long lists (mishearings are fixed before and after the tidy-up anyway)."""
        words = ", ".join(known_words())
        if small:
            return ("Fix this dictated text. Rules: add punctuation and capitals; delete um, uh, 'you know'; if the speaker "
                    "says 'no wait' or 'I mean', keep only the corrected part; spell texting shorthand in full "
                    "(u -> you, tmrw -> tomorrow); keep contractions and every other word exactly; never reword, never make "
                    "lists, never answer questions. "
                    + (f"Spell these exactly: {words}. " if words else "")
                    + (hint + " " if hint else "")
                    + (f"Then translate the fixed text into {CONFIG['translate_to']} and output only the translation."
                       if CONFIG.get("translate_to") else "Output only the fixed text."))
        return ("You clean up dictated text before it is pasted. Fix punctuation and capitalization, remove filler words "
                "(um, uh, you know), and apply the speaker's self-corrections: when they say something, then 'no wait' "
                "or 'I mean' and a replacement, keep only the replacement, and keep every other word they said "
                "(days, names, numbers) exactly as spoken. "
                "Keep the speaker's own words and tone; do not add, answer, or summarize anything. "
                "If the text is a question or instruction, clean it up as text; never respond to it. "
                "Use American English spelling (favor, color, organize, canceled). "
                + (f"Spell these words exactly like this when they appear: {words}. " if words else "")
                + (f"Known mishearings (heard -> meant): {corrections.describe()}. " if corrections.load() else "")
                + (f"Respect the speaker's writing habits (spelling, punctuation; never change their words or tone):\n"
                   f"{commands.style_guide('Writing habits')}\n" if CONFIG.get("use_style", True) else "")
                + (hint + " " if hint else "")
                + (f"Finally, translate the cleaned text into {CONFIG['translate_to']}; output only the translation. "
                   if CONFIG.get("translate_to") else "Output only the cleaned text."))

    def _azure(self, system, text):
        self._last_used = time.time()
        reply = self._client().chat.completions.create(
            model=CONFIG["cleanup_model"], reasoning_effort="none",
            messages=[{"role": "system", "content": system}, {"role": "user", "content": text}])
        usage.azure(reply, CONFIG["cleanup_model"], "tidy-up")
        return reply.choices[0].message.content.strip()


# ---------- paste at the cursor ----------

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
                if isinstance(data, (bytes, str)):  # skip raw handles (bitmaps, metafiles): they die with the old clipboard
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
        # Keep dictation out of clipboard history and cloud sync. Nice to have, so never let it block the paste
        # (Windows rejects zero-length data, hence the 4-byte payloads).
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
    time.sleep(1.0)  # slow apps (new Teams) read the clipboard late: wait before putting the old contents back
    if win32clipboard.GetClipboardSequenceNumber() == ours:
        restore_clipboard(saved)


# ---------- the recording light ----------

class Light:
    """A small pill at the bottom of the screen. It never takes focus and clicks pass straight through it."""
    W, H = 280, 40
    KEY = "#010203"  # drawn in this colour = see-through
    LOOKS = {  # state: (dot colour, text)
        "listening": ("#ff4d4f", "Listening"),
        "handsfree": ("#ff4d4f", "Hands-free · tap to stop"),
        "working": ("#f5b700", "Writing…"),
        "error": ("#9aa0a6", "Didn't catch that"),
        "command": ("#2bbfae", "Command · listening"),
        "command_handsfree": ("#2bbfae", "Command hands-free · tap to stop"),
        "thinking": ("#2bbfae", "Thinking…"),
        "claude": ("#d97757", "Claude is working…"),
        "meeting": ("#ff4d4f", "Taking meeting notes"),
        "meeting_notes": ("#f5b700", "Writing meeting notes…"),
    }

    def __init__(self, root, previous=None):
        import win32gui
        self.root, self.state, self.shown, self.level = root, None, None, 0.0
        self.error_until = 0.0
        self.claude_busy = 0  # Claude jobs running in the background; shown whenever nothing else is
        self.meeting = None  # "meeting" while taking notes, "meeting_notes" while writing them up
        root.overrideredirect(True)
        root.attributes("-topmost", True)
        root.attributes("-alpha", 0.0)
        root.config(bg=self.KEY)
        root.attributes("-transparentcolor", self.KEY)
        work = win32api.GetMonitorInfo(win32api.MonitorFromPoint((0, 0)))["Work"]
        x = (work[0] + work[2]) // 2 - self.W // 2
        y = work[3] - self.H - 28
        root.geometry(f"{self.W}x{self.H}+{x}+{y}")
        self.canvas = tk.Canvas(root, width=self.W, height=self.H, bg=self.KEY, highlightthickness=0)
        self.canvas.pack()
        r, w, h = self.H // 2, self.W, self.H
        self.canvas.create_oval(0, 0, 2 * r, h, fill="#1f1f23", outline="")
        self.canvas.create_oval(w - 2 * r, 0, w, h, fill="#1f1f23", outline="")
        self.canvas.create_rectangle(r, 0, w - r, h, fill="#1f1f23", outline="")
        self.dot = self.canvas.create_oval(0, 0, 0, 0, fill="#ff4d4f", outline="")
        self.text = self.canvas.create_text(46, h // 2, anchor="w", fill="#f2f2f2", font=("Segoe UI", 10))
        root.update()
        hwnd = win32gui.GetParent(root.winfo_id()) or root.winfo_id()
        style = win32gui.GetWindowLong(hwnd, win32con.GWL_EXSTYLE)
        win32gui.SetWindowLong(hwnd, win32con.GWL_EXSTYLE, style | win32con.WS_EX_NOACTIVATE
                               | win32con.WS_EX_TOOLWINDOW | win32con.WS_EX_TRANSPARENT | win32con.WS_EX_LAYERED)
        # Creating a Tk window makes it the active window; hand focus straight back to whatever had it.
        if previous and win32gui.GetForegroundWindow() != previous:
            try:
                win32gui.SetForegroundWindow(previous)
            except Exception as e:
                log.info("could not hand focus back: %s", e)

    def set(self, state):
        """Safe to call from any thread; the main loop draws it."""
        if state == "error":  # a brief flash: draw() shows it until error_until, then the pill hides by itself
            self.error_until = time.monotonic() + 1.5
            state = None
        self.state = state

    def draw(self):
        state = self.state
        if state is None and time.monotonic() < self.error_until:
            state = "error"
        if state is None and self.meeting:
            state = self.meeting
        if state is None and self.claude_busy:
            state = "claude"
        if not CONFIG.get("show_light", True):
            state = None
        if state != self.shown:
            if state is None:
                self.root.attributes("-alpha", 0.0)
            else:
                colour, words = self.LOOKS[state]
                self.canvas.itemconfigure(self.dot, fill=colour)
                self.canvas.itemconfigure(self.text, text=words)
                self.root.attributes("-alpha", 0.92)
            self.shown = state
        if state in ("listening", "handsfree", "command", "command_handsfree"):  # the dot grows with your voice
            size = 5 + min(5, self.level * 60)
        else:
            size = 5
        cx, cy = 26, self.H // 2
        self.canvas.coords(self.dot, cx - size, cy - size, cx + size, cy + size)


# ---------- teaching a word by typing ----------

def strip_punctuation(text):
    return re.sub(r"^[\W_]+|[\W_]+$", "", text.strip())


def keep_punctuation(selection, fix):
    """'Cooper nettys.' fixed to 'Kubernetes' pastes 'Kubernetes.' so the sentence keeps its full stop."""
    m = re.match(r"^(\s*[\W_]*)(.*?)([\W_]*\s*)$", selection, re.S)
    return (m.group(1) + fix + m.group(3)) if m and selection else fix


def bring_to_front(hwnd, tries=10):
    """Windows only lets the program that got the last keypress switch windows, so send a harmless (unassigned)
    key first, then switch, and check it actually happened."""
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
    """Shift + command key: 'It wrote' (the selection) and 'You meant' (typed). Enter saves, Esc cancels."""
    BG, FG, FIELD = "#1f1f23", "#f2f2f2", "#2c2c33"

    def __init__(self, root, heard, target, on_save):
        import win32gui
        self.target, self.on_save, self.selection = target, on_save, heard
        win = self.win = tk.Toplevel(root)
        win.title("Teach VoiceTool a word")
        win.attributes("-topmost", True)
        win.configure(bg=self.BG, padx=16, pady=12)
        win.resizable(False, False)
        tk.Label(win, text="Teach VoiceTool a word", fg="#6fe6d6", bg=self.BG, font=("Segoe UI Semibold", 11)).grid(
            row=0, column=0, columnspan=2, sticky="w", pady=(0, 8))
        self.heard = tk.StringVar(value=strip_punctuation(heard))
        self.meant = tk.StringVar()
        for row, (label, var) in enumerate((("It wrote", self.heard), ("You meant", self.meant)), start=1):
            tk.Label(win, text=label, fg="#a0a0a8", bg=self.BG, font=("Segoe UI", 10)).grid(row=row, column=0, sticky="w", padx=(0, 10))
            entry = tk.Entry(win, textvariable=var, width=32, bg=self.FIELD, fg=self.FG, insertbackground=self.FG,
                             relief="flat", font=("Segoe UI", 11))
            entry.grid(row=row, column=1, pady=4, ipady=4)
            if var is self.meant:
                self.meant_entry = entry
        tk.Label(win, text="Leave “It wrote” empty to just add the word to your list.   Enter = save · Esc = cancel",
                 fg="#7c7c86", bg=self.BG, font=("Segoe UI", 9)).grid(row=3, column=0, columnspan=2, sticky="w", pady=(8, 0))
        win.bind("<Return>", lambda e: self.save())
        win.bind("<Escape>", lambda e: self.cancel())
        win.protocol("WM_DELETE_WINDOW", self.cancel)
        win.update_idletasks()
        work = win32api.GetMonitorInfo(win32api.MonitorFromPoint((0, 0)))["Work"]
        w, h = win.winfo_reqwidth(), win.winfo_reqheight()
        win.geometry(f"+{(work[0] + work[2] - w) // 2}+{work[3] - h - 120}")
        win.deiconify()
        win.lift()
        win.focus_force()
        self.meant_entry.focus_set()
        try:  # typing needs real keyboard focus, so this window (unlike the light and cards) takes it
            win32gui.SetForegroundWindow(win32gui.GetParent(win.winfo_id()) or win.winfo_id())
        except Exception:
            pass

    def save(self):
        heard, meant = self.heard.get().strip(), self.meant.get().strip()
        self.win.destroy()
        if meant:
            # Paste over the original selection (with its spaces and punctuation) if it wasn't edited in the box.
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


# ---------- result card (answers, and what Claude did) ----------

class ResultCard:
    """A small note in the bottom-right corner. It never takes focus and closes itself after a minute."""
    BG = "#1f1f23"

    def __init__(self, root):
        self.root = root
        self.win = None

    def show(self, title, body, buttons=None, seconds=60):
        """buttons: [(label, action)] shown instead of Copy/Close (each also closes the card)."""
        import win32gui
        previous = win32gui.GetForegroundWindow()
        self.close()
        win = self.win = tk.Toplevel(self.root)
        win.overrideredirect(True)
        win.attributes("-topmost", True)
        win.configure(bg=self.BG)
        frame = tk.Frame(win, bg=self.BG, padx=14, pady=10)
        frame.pack(fill="both", expand=True)
        tk.Label(frame, text=title, fg="#6fe6d6", bg=self.BG, font=("Segoe UI Semibold", 10), anchor="w").pack(fill="x")
        tk.Label(frame, text=body, fg="#f2f2f2", bg=self.BG, font=("Segoe UI", 10), justify="left", anchor="w",
                 wraplength=400).pack(fill="x", pady=(4, 8))
        row = tk.Frame(frame, bg=self.BG)
        row.pack(fill="x")
        if buttons:
            actions = [(label, lambda a=action: (self.close(), a())) for label, action in reversed(buttons)]
        else:
            actions = [("Close", self.close), ("Copy", lambda: set_clipboard_text(body))]
        for label, action in actions:
            tk.Button(row, text=label, command=action, relief="flat", bg="#33333a", fg="#f2f2f2", padx=10,
                      activebackground="#44444c", activeforeground="#ffffff", font=("Segoe UI", 9)).pack(side="right", padx=(6, 0))
        win.update_idletasks()
        work = win32api.GetMonitorInfo(win32api.MonitorFromPoint((0, 0)))["Work"]
        w, h = max(320, win.winfo_reqwidth()), win.winfo_reqheight()
        win.geometry(f"{w}x{h}+{work[2] - w - 20}+{work[3] - h - 20}")
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


# ---------- the keys: hold to talk, double-tap to lock, tap to stop ----------

IDLE, HOLDING, TAP_WAIT, LOCKED, TEACH = "idle", "holding", "tap_wait", "locked", "teach"


class _KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("vkCode", wintypes.DWORD), ("scanCode", wintypes.DWORD), ("flags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class KeyboardHook(threading.Thread):
    """Windows' low-level keyboard hook, on a thread of its own that does nothing else. Windows waits for this thread
    on every key pressed anywhere, so it is kept as small as possible: on_key(msg, data) is called for each key
    (data has vkCode and flags); calling suppress_event() inside it drops the key. Replaces pynput's listener, which
    (a) stops for good, silently, after any error inside it, and (b) builds a keyboard-layout table each time one is
    made: ~0.01 s normally, but measured at ~5 s when another thread is busy, too slow to put a hook back quickly."""

    _c, _w = ctypes, wintypes
    _KBD = _KBDLLHOOKSTRUCT
    _PROC = _c.WINFUNCTYPE(_c.c_ssize_t, _c.c_int, _w.WPARAM, _w.LPARAM)
    _user32 = _c.WinDLL("user32", use_last_error=True)
    _kernel32 = _c.WinDLL("kernel32")
    _SetHook = _user32.SetWindowsHookExW
    _SetHook.argtypes = (_c.c_int, _PROC, _w.HINSTANCE, _w.DWORD)
    _SetHook.restype = _w.HHOOK
    _Unhook = _user32.UnhookWindowsHookEx
    _Unhook.argtypes = (_w.HHOOK,)
    # CallNextHookEx is instant, so it's called without letting go of Python's lock: letting go means waiting to get it
    # back if another thread is busy, and Windows is waiting on us meanwhile.
    _CallNext = _c.PyDLL("user32").CallNextHookEx
    _CallNext.argtypes = (_w.HHOOK, _c.c_int, _w.WPARAM, _w.LPARAM)
    _CallNext.restype = _c.c_ssize_t
    _PostThreadMessage = _user32.PostThreadMessageW
    _PostThreadMessage.argtypes = (_w.DWORD, _w.UINT, _w.WPARAM, _w.LPARAM)

    def __init__(self, on_key):
        super().__init__(daemon=True, name="keyboard hook")
        self.on_key = on_key
        self.installed = False
        self.handle = None
        self.error = 0
        self._thread_id = None
        self._ready = threading.Event()
        self._proc = self._PROC(self._callback)  # kept here: Windows calls it for every key

    def suppress_event(self):
        raise _SUPPRESS()

    def _callback(self, code, wparam, lparam):
        if code == 0:  # HC_ACTION
            try:
                self.on_key(wparam, self._c.cast(lparam, self._c.POINTER(self._KBD)).contents)
            except _SUPPRESS:
                return 1
            except Exception:
                pass  # (on_key logs its own errors) the key goes on as normal, and the hook stays
        return self._CallNext(None, code, wparam, lparam)

    def run(self):
        k32 = self._kernel32
        self._thread_id = k32.GetCurrentThreadId()
        k32.SetThreadPriority(k32.GetCurrentThread(), 2)  # THREAD_PRIORITY_HIGHEST: it only ever runs for a key
        msg = self._w.MSG()
        self._user32.PeekMessageW(self._c.byref(msg), None, 0, 0, 0)  # make the message queue before anyone posts
        hook = self._SetHook(13, self._proc, None, 0)  # WH_KEYBOARD_LL
        self.handle = hook
        self.installed = bool(hook)
        self.error = 0 if hook else self._c.get_last_error()
        self._ready.set()
        if not hook:
            return
        try:
            while self._user32.GetMessageW(self._c.byref(msg), None, 0, 0) > 0:
                pass  # Windows calls _callback from inside GetMessage
        finally:
            self._Unhook(hook)

    def wait(self, timeout=5):
        return self._ready.wait(timeout)

    def stop(self):
        self._ready.wait(5)
        if self._thread_id:
            self._PostThreadMessage(self._thread_id, 0x0012, 0, 0)  # WM_QUIT


class Trigger:
    """One key and where it is in its hold / double-tap / tap gesture."""

    def __init__(self, name, key_name, let_through):
        self.name = name  # "dictate" or "command"
        self.vk = getattr(keyboard.Key, key_name).value.vk
        # A left-side key (e.g. Left Alt) still has its normal jobs (Alt+Tab, Alt+F4), so Windows keeps seeing it.
        self.let_through = let_through
        self.state = IDLE
        self.key_down = False
        self.pressed_at = 0.0
        self.tap_deadline = 0.0
        self.swallow_release = False
        self.cued = False
        self.hook_down = False
        self.hook_alone = False
        # The watchdog's own view of the key (Windows' key state, polled): when it went down / up without the hook
        # saying so. None when the hook and Windows agree.
        self.missed_down_since = None
        self.missed_up_since = None
        self.hook_at = 0.0  # when the hook last heard this key (a held key repeats, so this keeps moving while held)
        # Command key: what was on screen. Captured at the first release, never while the key is down: a held
        # Win key would turn our Ctrl+C into Win+Ctrl+C (a Windows shortcut).
        self.context = None
        self.capturing = False
        self.app = {}  # dictation key: the program and window in front when the key went down (for app rules)

    @property
    def looks(self):
        return ("listening", "handsfree") if self.name == "dictate" else ("command", "command_handsfree")


class VoiceTool:
    def __init__(self, recorder=None, transcriber=None, cleaner=None, with_ui=True):
        """recorder, transcriber, cleaner: for tests (and the test-mode start); normally they're made here."""
        dictate = Trigger("dictate", CONFIG["dictate_key"], CONFIG.get("let_key_through", True))
        self.triggers = {dictate.vk: dictate}
        if CONFIG.get("command_key"):
            # Left Win keeps its shortcuts (Win+E, Win+Shift+S); a lone press is masked so it can't open Start.
            command = Trigger("command", CONFIG["command_key"], CONFIG.get("command_let_key_through", True))
            self.triggers[command.vk] = command
        self.active = None  # the trigger currently recording, if any
        self.events = queue.Queue()
        self.jobs = queue.Queue()
        self.ui = queue.Queue()  # things only the main (window) thread may do
        self.recorder = recorder or Recorder()
        self.transcribe = transcriber or Transcriber()
        self.azure = cleaner if cleaner is not None else Cleaner()
        self._busy_lock = threading.Lock()
        self.claude_session = commands.ClaudeSession(CONFIG.get("claude_model", "sonnet"))
        self.with_ui = with_ui
        self.light = None
        self.card = None
        self.icon = None
        self.listener = None
        self.settings = None  # the old tkinter settings window, only used if the app window can't start
        self.app_window = None
        self.meeting = None
        self.meeting_auto_stop = False
        self.meeting_app = ""  # the call app the notes were offered for ("" if started by hand)
        self.meeting_watch = None
        self.test_mode = False
        self._hook_gen = 0  # only the newest keyboard hook acts on keys (see _install_hook)
        self._last_tick = None  # when the window loop last ran (the watchdog notices if it gets stuck)
        self._stuck_logged = False
        self._quitting = False
        self._hook_restarted_at = float("-inf")

    # Runs inside Windows' keyboard hook: must return fast, so it only queues events.
    def _filter(self, msg, data):
        try:
            return self._key_event(msg, data)
        except _SUPPRESS:
            raise  # suppress_event(): how the hook tells Windows to drop the key
        except Exception:
            # Any other error here would make pynput stop the listener, which removes the hook for good (that is
            # one way VoiceTool went deaf until restarted). Log it and let the key through instead.
            log.exception("keyboard hook error (the key was passed on)")
            return True

    def _key_event(self, msg, data):
        if data.flags & LLKHF_INJECTED:
            return True
        down = msg in (win32con.WM_KEYDOWN, win32con.WM_SYSKEYDOWN)
        trigger = self.triggers.get(data.vkCode)
        if trigger and not (CONFIG.get("enabled", True) and CONFIG.get(f"{trigger.name}_enabled", True)):
            trigger = None  # paused or switched off in settings: the key does its normal job, untouched
        if trigger:
            trigger.hook_at = time.monotonic()
            self.events.put(("trigger", trigger, down, trigger.hook_at))
            if not trigger.let_through:
                self.listener.suppress_event()  # Windows never sees this key, so it can't open menus or Start
            if down:
                if not trigger.hook_down:
                    trigger.hook_down, trigger.hook_alone = True, True
            else:
                trigger.hook_down = False
                if trigger.hook_alone:
                    # Pressed on its own: slip in an unused key before releasing it, so a lone Alt can't open
                    # app menus or ribbon shortcuts (the same trick AutoHotkey uses). Shortcuts like Alt+Tab are untouched.
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
        """The main thread: draw the pill and cards, run the tray icon, handle the keys. hardware=False (test mode)
        leaves out the keyboard hook, the tray icon and the call watcher, so a test copy can run beside the real one."""
        import win32gui
        previous = win32gui.GetForegroundWindow()
        root = tk.Tk()
        # An error in a window callback used to go to stderr (thrown away by pythonw); log it instead.
        root.report_callback_exception = lambda kind, value, tb: log.error(
            "window callback failed", exc_info=(kind, value, tb))
        self.light = Light(root, previous)
        self.card = ResultCard(root)
        threading.Thread(target=self._worker, daemon=True).start()
        if hardware:
            self._install_hook()
            threading.Thread(target=self._watch_keys, daemon=True, name="key watchdog").start()
            log.info("ready: hold %s to talk, %s for commands; double-tap to lock on, tap again to stop",
                     CONFIG["dictate_key"], CONFIG.get("command_key") or "no key")
            beep((880, 60), (1320, 60))
        if hardware and speech_model_missing(self.transcribe):
            log.warning("the speech model is missing; VoiceTool stays open so Finish setup can download it")
            self._card("Finish setup first", getattr(self.transcribe, "problem", SPEECH_MISSING_TEXT) or SPEECH_MISSING_TEXT, 30)
        if hardware and hasattr(self.azure, "sync_offline"):
            try:
                self.azure.sync_offline()  # the on-device model starts loading now, so it's ready when it's first needed
            except Exception:
                log.exception("could not start the on-device model")
        import meetings
        gone = meetings.delete_other_side_audio(MEETING_AUDIO)  # left behind if VoiceTool was cut off mid-meeting
        if gone:
            log.info("deleted the other side's audio left by a meeting that was cut off: %d file(s)", len(gone))
        if hardware:
            self._start_tray()
            self.meeting_watch = meetings.MeetingWatch(
                on_start=lambda app: self.ui.put(lambda: self._offer_meeting(app)),
                on_end=lambda: self.ui.put(self._call_ended),
                enabled=lambda: CONFIG.get("enabled", True) and CONFIG.get("meeting_notes", False)
                and CONFIG.get("meeting_auto_offer", False))
        root.after(30, self._tick)
        root.mainloop()

    # ---------- the keyboard hook and its watchdog ----------

    def _install_hook(self):
        """Install the keyboard hook (again) on a fresh listener thread. Only the newest hook acts on keys: an older
        one that is still installed passes every key straight on, so a key is never handled twice."""
        gen = self._hook_gen + 1
        old = self.listener

        def on_key(msg, data):
            if gen != self._hook_gen:
                return True
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
        """A light thread that checks, 20 times a second, that the keyboard hook is still alive and agrees with
        Windows' own key state. If the hook goes deaf (Windows can silently remove a hook; pynput stops its listener
        after an error), a new one is put in while the key is still held, so the recording starts a moment late
        instead of never, and nobody has to restart VoiceTool. Every such repair is logged."""
        while True:
            time.sleep(WATCH_EVERY_SECONDS)
            try:
                self._check_keys(time.monotonic(), lambda vk: bool(win32api.GetAsyncKeyState(vk) & 0x8000))
            except Exception:
                log.exception("keyboard watchdog check failed")
                time.sleep(5)

    def _check_keys(self, now, key_is_down):
        """One look (the watchdog). key_is_down(vk): Windows' own idea of whether the key is held right now."""
        if self._quitting:
            return
        if self.listener is not None and not self.listener.is_alive() and now - self._hook_restarted_at >= 2:
            self._hook_restarted_at = now  # (at most every 2 s, in case Windows keeps refusing)
            log.warning("keyboard hook had stopped; re-installing it")
            self._install_hook()
            log.info("keyboard hook re-installed")
        for t in self.triggers.values():
            enabled = CONFIG.get("enabled", True) and CONFIG.get(f"{t.name}_enabled", True)
            # A key the hook swallows never reaches Windows' key state, so it can't be checked this way.
            if not enabled or not t.let_through:
                t.missed_down_since = t.missed_up_since = None
                continue
            down = key_is_down(t.vk)
            if down and not t.hook_down:
                # Held, but the hook hasn't said so. A held key repeats many times a second, so a live hook always
                # knows within a few ms. Put a new hook in once per press: it hears the key's next repeat and the
                # recording starts then (the press itself isn't replayed, so a shortcut like Alt+Tab done in a window
                # the hook can't see, e.g. an admin one, never turns into a dictation).
                t.missed_up_since = None
                if t.missed_down_since is None:
                    t.missed_down_since = now
                elif t.missed_down_since != -1 and now - t.missed_down_since >= HOOK_MISSED_PRESS_SECONDS:
                    t.missed_down_since = -1  # done for this press
                    log.warning("keyboard hook missed a %s press (in %s); re-installing it", t.name,
                                self._front_window().get("app") or "?")
                    self._install_hook()
                    log.info("keyboard hook re-installed")
            elif not down and t.hook_down and now - t.hook_at >= HOOK_MISSED_RELEASE_SECONDS:
                # The hook saw the key go down but never come up (it was let go on the lock screen, in an admin
                # window, or the hook died meanwhile), and has heard nothing from it since: a key really held keeps
                # repeating, so another program hiding the key from Windows can't fool this. Handle the release, or
                # the key would stay "held" for good.
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

    # ---------- the icon by the clock, and the app window ----------

    def _icon_image(self, paused=False):
        return settings_window.icon_image(paused, recording=getattr(self, "meeting", None) is not None)

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
            pystray.MenuItem("Open VoiceTool", lambda: ask(self._open_app), default=True),
            pystray.MenuItem("Settings", lambda: ask(self._open_settings)),
            pystray.MenuItem("Pause", lambda: ask(lambda: self._pause(CONFIG.get("enabled", True))),
                             checked=lambda item: not CONFIG.get("enabled", True)),
            pystray.MenuItem(lambda item: "Stop meeting notes" if self.meeting else "Start meeting notes",
                             lambda: ask(self._stop_meeting if self.meeting else self._start_meeting)),
            pystray.MenuItem("Meeting notes folder", lambda: self._open_meeting_folder()),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quit VoiceTool", lambda: ask(self._quit)),
        )
        self.icon = pystray.Icon("VoiceTool", self._icon_image(not CONFIG.get("enabled", True)), self._tooltip(), menu)
        self.icon.run_detached()

    def _tooltip(self):
        if getattr(self, "meeting", None) is not None:
            return "VoiceTool: taking meeting notes (right-click to stop)"
        if not CONFIG.get("enabled", True):
            return "VoiceTool: paused"
        return "VoiceTool: hold Left Alt to talk, the Left Windows key for commands"

    def _open_app(self, page="home"):
        """The VoiceTool window, at `page` (main thread). If it can't start, the old settings window opens instead."""
        try:
            if self.app_window is None:
                import app_window_win
                import ui_api
                api = ui_api.API(self._win_host(), CONFIG, settings_window.save)
                mode = {"hidden": "hidden", "1": True}.get(os.environ.get("VOICETOOL_TEST_WINDOW", ""), self.test_mode)
                python = os.environ.get("VOICETOOL_TEST_WINDOW_PYTHON") if self.test_mode else None  # (tests: a broken window)
                self.app_window = app_window_win.AppWindow(
                    api, log, on_failed=lambda reason: self.ui.put(lambda: self._window_failed(reason)), test_mode=mode,
                    python=python)
            self.app_window.show(page)
        except Exception:
            log.exception("could not open the app window")
            self._open_settings_tk()

    _host = None

    def _win_host(self):
        """The one WinHost (it remembers whether the Finish setup download is running)."""
        if self._host is None:
            self._host = WinHost(self)
        return self._host

    def _open_settings(self):
        self._open_app("settings")

    def _window_failed(self, reason):
        log.error("the app window did not start (%s); opening the simple settings window", reason[:200])
        self._open_settings_tk()

    def _open_settings_tk(self):
        """The original small settings window: the fallback when the app window can't start."""
        if self.settings is not None and self.settings.win.winfo_exists():
            self.settings.win.deiconify()
            self.settings.win.lift()
            self.settings.win.focus_force()
            return
        self.settings = settings_window.SettingsWindow(self.light.root, CONFIG, {
            "quit": self._quit,
            "stop_claude": self.claude_session.stop,
            "refresh_style": self._refresh_style,
            "paused": lambda paused: self._pause(paused, from_settings=True),
            "offline": lambda: getattr(self.azure, "sync_offline", lambda: None)(),
            "finish_setup": lambda: self._win_host().finish_setup(),
            "setup_missing": lambda: self._win_host().setup_status()["missing"],
        })

    def _pause(self, paused, from_settings=False):
        if not from_settings:
            CONFIG["enabled"] = not paused
            settings_window.save(CONFIG)
            if self.settings is not None and self.settings.win.winfo_exists():
                self.settings.pause.set(paused)
        if self.active is not None:
            self._drop(self.active)
        log.info("paused" if paused else "resumed")
        self._refresh_icon()

    def choose_folder_tk(self, start=""):
        """A Windows folder picker on the main thread (used when the VoiceTool window isn't there to show its own)."""
        result, done = {}, threading.Event()

        def pick():
            try:
                from tkinter import filedialog
                result["path"] = filedialog.askdirectory(initialdir=start or str(Path.home()), title="Choose a folder")
            except Exception:
                log.exception("the folder picker failed")
            finally:
                done.set()
        self.ui.put(pick)
        done.wait(600)
        return result.get("path", "") or ""

    def _quit(self):
        log.info("quit from the menu")
        self._quitting = True  # the keyboard watchdog must not put the hook back
        try:
            if self.meeting is not None:  # don't leave a recording running (or the other side's audio on disk)
                self._abandon_meeting()
            if self.listener is not None:
                self.listener.stop()
            self.claude_session.stop()
            self._hush()
            if getattr(self.azure, "local", None) is not None:
                self.azure.local.stop()
            if self.app_window is not None:
                self.app_window.close()
            if self.icon is not None:
                self.icon.stop()
        finally:
            self.light.root.destroy()

    # ---------- small helpers for the pill, cards and spoken answers ----------

    def _light(self, state):
        if self.light is not None:
            self.light.set(state)

    def _card(self, title, body, seconds=60, speak=False, buttons=None):
        """Show a card (from any thread) and, for answers, read it out if Spoken answers is on."""
        if self.with_ui:
            self.ui.put(lambda: self.card.show(title, body, buttons=buttons, seconds=seconds))
        if speak:
            self._say(body)

    def _say(self, text):
        """Spoken answers (Settings > Spoken answers). Esc stops it."""
        if not CONFIG.get("speak_answers", False) or not text:
            return
        self._read(text[:1500])

    _read_file = None

    def _read(self, text):
        """Read text aloud with the chosen voice and speed (read_aloud.py; the PC's voices are Windows' own). Esc stops it."""
        import actions_windows
        import read_aloud
        text = read_aloud.clean_for_speech(text)
        if not text:
            return
        self._hush()
        try:
            # only if everything isn't kept on this computer and an online voice is named in config (off by default)
            if not CONFIG.get("offline_only", False) and read_aloud.natural_voice_available(CONFIG):
                path = read_aloud.azure_speech_file(self.azure._client(), CONFIG, text)
                self._read_file = path
                actions_windows.play_audio_file(path)
                threading.Timer(1800, lambda: Path(path).unlink(missing_ok=True)).start()
                return
        except Exception as e:
            log.warning("natural voice unavailable (%s); using Windows' voice", str(e)[:120])
        actions_windows.speak(text, voice=CONFIG.get("read_voice", ""), rate=CONFIG.get("read_rate", "normal"),
                              limit=read_aloud.MAX_CHARS, log=log)

    def _hush(self):
        import actions_windows
        actions_windows.stop_speaking()
        if self._read_file is not None:
            Path(self._read_file).unlink(missing_ok=True)
            self._read_file = None

    # ---------- meeting notes (off unless turned on; always asks first) ----------

    def _offer_meeting(self, app):
        """MeetingWatch saw a call start: offer to take notes (main thread). The offer is itself the consent card."""
        if self.meeting is not None or not CONFIG.get("meeting_notes", False):
            return
        if self._meeting_blocked(card=False):
            return  # (no offer while the models are missing; starting by hand explains why)
        import meetings
        log.info("call started in %s; offering notes", app)
        self.card.show(f"Meeting in {app}?", "Want VoiceTool to take notes? It records you and the call and writes a "
                       "summary with action items when it ends. " + meetings.CONSENT_REMINDER,
                       buttons=[("Everyone agreed, start", lambda: self._begin_meeting(app)),
                                ("Not this time", lambda: None)], seconds=45)

    def _call_ended(self):
        """MeetingWatch saw the call end: finish the notes if they were started for this call."""
        if self.meeting is not None and self.meeting_auto_stop:
            log.info("call ended; finishing notes")
            self._stop_meeting()

    def _start_meeting(self, app=None):
        """Starting notes (from the icon menu, the window or a spoken command; main thread): first a card asks whether
        everyone on the call agreed to be recorded. Nothing is recorded until that card is answered."""
        if self.meeting is not None:
            return
        if not CONFIG.get("meeting_notes", False):
            self._card("Meeting notes are switched off", "Turn on “Meeting notes” in VoiceTool settings (Meetings).", 8)
            return
        if self._meeting_blocked():
            return
        import meetings
        self._card("Before recording", meetings.CONSENT_REMINDER, 60,
                   buttons=[("Everyone agreed, start", lambda: self._begin_meeting(app)), ("Cancel", lambda: None)])

    def _begin_meeting(self, app=None):
        """The consent card was answered "Everyone agreed, start": start recording (main thread). app: the call app it
        was offered for (notes then stop when the call ends)."""
        if self.meeting is not None or not CONFIG.get("meeting_notes", False):
            return
        if self._meeting_blocked():
            return
        import meetings
        folder = MEETING_AUDIO / time.strftime("%Y-%m-%d_%H%M%S")
        try:
            self.meeting = meetings.MeetingRecorder(folder, self.transcribe, self.recorder.device,
                                                    CONFIG.get("min_peak", 0.005), log)
        except Exception as e:
            log.exception("could not start meeting notes")
            self._card("Couldn't start meeting notes", str(e)[:300])
            return
        self.meeting_auto_stop = app is not None  # started for a detected call: stops when that call ends
        self.meeting_app = app or ""
        self._light_meeting("meeting")
        log.info("meeting notes started%s", f" ({app})" if app else "")
        beep((660, 60), (880, 60))
        self._refresh_icon()
        self._card("Taking meeting notes", "Stop any time from the VoiceTool icon (right-click) or the VoiceTool window. "
                   + ("They'll also stop when the call ends." if app else ""), 8)

    def _meeting_blocked(self, card=True):
        """True (after a plain card) while a model the notes need is missing: a meeting is never recorded that can't be
        written up. Needs the speech model, and the tidy-up model unless everything runs online."""
        if speech_model_missing(self.transcribe):
            if card:
                self._card("Finish setup first", "Meeting notes need the speech model, which is missing. "
                           "Open VoiceTool and click Finish setup.", 12)
            return True
        if CONFIG.get("offline_only", False) and not local_cleanup_win.available(CONFIG):
            if card:
                self._card("Finish setup first", "Meeting notes need the tidy-up model, which isn't set up yet. "
                           "Open VoiceTool and click Finish setup.", 12)
            return True
        return False

    def _light_meeting(self, state):
        if self.light is not None:
            self.light.meeting = state

    def _stop_meeting(self):
        if self.meeting is None:
            return
        recorder, self.meeting = self.meeting, None
        self._light_meeting("meeting_notes")
        self._refresh_icon()
        beep((880, 60), (660, 60))
        threading.Thread(target=self._finish_meeting, args=(recorder,), daemon=True).start()

    def _abandon_meeting(self):
        """Quitting in the middle of a meeting: stop recording without writing notes, and tidy the audio."""
        recorder, self.meeting = self.meeting, None
        if recorder is None:
            return
        try:
            recorder.abort()
        except Exception:
            log.exception("could not stop the meeting recording")
        self._tidy_meeting_audio(recorder)
        self._light_meeting(None)

    def _follow_up_wanted(self):
        """The follow-up email draft needs Claude: it is only written when Claude actions are on as well."""
        return bool(CONFIG.get("meeting_followup", False) and CONFIG.get("claude_actions", False))

    def _plain_notes_problem(self, e):
        """Why the notes couldn't be written, in a few plain words (for the card and the document)."""
        if isinstance(e, TidyUpNotSetUp):
            return "the tidy-up model isn't set up on this computer"
        if isinstance(e, local_cleanup_win.ModelServerUnavailable):
            return "the tidy-up model wouldn't start"
        if isinstance(e, TimeoutError) or "timeout" in type(e).__name__.lower():
            return "the tidy-up model took too long"
        if isinstance(e, ValueError):
            return "the tidy-up model's answer couldn't be read"
        return "the tidy-up model ran into a problem"

    def _finish_meeting(self, recorder):
        import meetings
        lines, path = [], None
        try:
            lines = recorder.stop()
            log.info("meeting ended: %d minutes, %d transcript lines", round((recorder.ended_at - recorder.started_at) / 60), len(lines))
            if not lines:
                self._card("No meeting notes", "Nothing was heard, so there's nothing to write up.")
                return
            followup = self._follow_up_wanted()
            problem = ""
            try:
                client = self.azure._client()
                notes = meetings.make_notes(client, CONFIG["cleanup_model"], lines, recorder.started_at,
                                            recorder.ended_at, style=self._style_guide(),
                                            local=bool(CONFIG.get("offline_only", False)), followup=followup)
            except Exception as e:
                # Never lose the transcript: if the notes can't be written, the Word document has the transcript only.
                log.exception("the meeting notes couldn't be written; saving the transcript only")
                problem = self._plain_notes_problem(e)
                notes, followup = meetings.transcript_only_notes(problem), False
            path = meetings.write_document(notes, lines, recorder.started_at, recorder.ended_at)
            log.info("meeting notes saved: %s", path.name)
            self._history({"meeting": notes.get("title"), "notes": str(path), "minutes":
                           round((recorder.ended_at - recorder.started_at) / 60)})
            actions = len(notes.get("action_items") or [])
            if problem:
                self._card("Meeting transcript saved", f"The summary couldn't be written ({problem}), so the document has "
                           "the full transcript only. It's saved in your Meeting notes folder.",
                           seconds=120, buttons=[("Open notes", lambda: os.startfile(path)), ("Close", lambda: None)])
                return
            self._card("Meeting notes ready", f"“{notes.get('title')}”: summary, {actions} action item"
                       f"{'' if actions == 1 else 's'} and the transcript, saved in your Meeting notes folder."
                       + (" Drafting the follow-up email…" if followup else ""),
                       seconds=120, buttons=[("Open notes", lambda: os.startfile(path)), ("Close", lambda: None)])
            if followup and notes.get("follow_up_email"):
                instruction = meetings.FOLLOW_UP_INSTRUCTION.format(
                    start=time.strftime("%A %d %B %Y, %I:%M %p", time.localtime(recorder.started_at)),
                    end=time.strftime("%I:%M %p", time.localtime(recorder.ended_at)), body=notes["follow_up_email"])
                self._claude(instruction, {"app": "VoiceTool meeting notes", "title": notes.get("title"), "selection": ""})
        except Exception as e:
            log.exception("meeting notes failed")
            saved = None
            if lines and path is None:  # the Word document couldn't be made: keep the words in a plain text file
                try:
                    saved = meetings.save_transcript_text(lines, recorder.started_at, recorder.ended_at)
                except Exception:
                    log.exception("the transcript couldn't be saved as text either")
            if saved is not None:
                self._card("Meeting transcript saved", f"The Word document couldn't be made ({str(e)[:150]}), so the words "
                           f"were saved as a text file: {saved}", seconds=120,
                           buttons=[("Open", lambda: os.startfile(saved)), ("Close", lambda: None)])
            else:
                self._card("Meeting notes failed", f"{str(e)[:250]} Your own recording is kept in "
                           "VoiceTool › recordings › meetings if “Save recordings” is on.")
        finally:
            self._light_meeting(None)
            self._tidy_meeting_audio(recorder)

    def _tidy_meeting_audio(self, recorder):
        """The other people's audio is never kept; your own track only if 'Save recordings' is on."""
        try:
            (recorder.folder / "others.wav").unlink(missing_ok=True)
            if not CONFIG.get("save_recordings"):
                (recorder.folder / "me.wav").unlink(missing_ok=True)
        except Exception as e:
            log.warning("meeting audio not tidied: %s", e)

    def _open_meeting_folder(self):
        import meetings
        os.startfile(meetings.notes_folder())

    # ---------- the key loop ----------

    def _tick(self):
        self._last_tick = time.monotonic()
        try:
            self._tick_once()
        except Exception:
            if not self._quitting:  # (after Quit the window is gone, so drawing it fails: expected)
                log.exception("window loop step failed")
        finally:
            # Always come round again: if this stopped, keys would queue up and nothing would ever happen.
            try:
                self.light.root.after(30, self._tick)
            except tk.TclError:
                pass  # the window was closed (Quit)

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
        if t is not None and t.state == TAP_WAIT and now > t.tap_deadline:
            self._drop(t)  # a single short tap does nothing
        elif t is not None and t.state == HOLDING and not t.cued and now - t.pressed_at >= CONFIG["hold_seconds"]:
            # Held long enough to be a real hold, not a shortcut: now show the light and beep.
            t.cued = True
            self.light.set(t.looks[0])
            beep((880, 40) if t.name == "dictate" else (740, 40))
        self.light.level = self.recorder.level
        self.light.draw()

    def _drop(self, t, quietly=True):
        self.recorder.stop()
        t.state = IDLE
        t.swallow_release = t.key_down
        self.active = None
        self._light(None)
        if not quietly:
            log.info("cancelled")
            beep((400, 120))

    def _handle(self, kind, trigger, value, at):
        t = self.active
        if kind == "other":
            if value == win32con.VK_ESCAPE:
                self._hush()  # Esc also stops a spoken answer
            if t is None:
                return
            if value == win32con.VK_ESCAPE and t.state in (HOLDING, LOCKED, TAP_WAIT):
                self._drop(t, quietly=False)
            elif t.state in (HOLDING, TEACH):
                self._drop(t)  # the key was part of a shortcut (e.g. Alt+Tab, Win+Shift+S), not ours
            return
        t, down = trigger, value
        if self.active is not None and self.active is not t:
            if down and not t.key_down:  # (said once per press, so a key that seems dead always shows why)
                log.info("%s key ignored: the %s key is still %s", t.name, self.active.name, self.active.state)
            t.key_down = down  # the other key is busy recording; ignore this one
            return
        if down:
            if t.key_down:
                return  # key auto-repeat
            t.key_down = True
            if t.state == LOCKED:
                self._finish(t)
                t.swallow_release = True
            elif t.state == TAP_WAIT:
                t.state = LOCKED
                t.swallow_release = True
                self._light(t.looks[1])
                log.info("%s hands-free on", t.name)
                beep((1320, 50), (1320, 50))
            elif (t.state == IDLE and t.name == "command" and CONFIG.get("typed_fix", True)
                  and win32api.GetAsyncKeyState(win32con.VK_SHIFT) & 0x8000):
                t.state, t.context, t.capturing = TEACH, None, False  # Shift + command key: type a fix instead
                self.active = t
            elif t.state == IDLE:
                try:
                    self.recorder.start()
                except Exception:
                    log.exception("could not open the microphone")
                    beep((300, 300))
                    return
                # Record from the first instant so no word is lost; the light and beep wait until it's clearly a hold.
                t.state, t.pressed_at, t.cued = HOLDING, at, False
                t.context, t.capturing = None, False
                t.app = self._front_window() if t.name == "dictate" else {}
                self.active = t
                if (CONFIG.get("cleanup") and not CONFIG.get("offline_only")) or t.name == "command":
                    self.azure.warm_up()
        else:
            t.key_down = False
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
            if t.name == "command" and not t.capturing:
                t.capturing = True  # the key is up now, so copying the selection is safe
                threading.Thread(target=self._capture, args=(t,), daemon=True).start()
            if at - t.pressed_at >= CONFIG["hold_seconds"]:
                self._finish(t)
            else:
                t.state = TAP_WAIT
                t.tap_deadline = at + CONFIG["double_tap_seconds"]

    @staticmethod
    def _front_window():
        try:
            return commands.window_info()
        except Exception:
            return {}

    def _capture(self, t):
        try:
            time.sleep(0.05)  # let Windows finish releasing the key first
            t.context = commands.capture_context(_open_clipboard, save_clipboard, restore_clipboard)
        except Exception:
            log.exception("could not read the screen context")
            t.context = {}

    def _teach(self, t):
        """Shift + command key: grab the selection, then ask what it should have been."""
        import win32gui
        target = win32gui.GetForegroundWindow()
        for _ in range(50):  # copying while Shift is still down sends Ctrl+Shift+C (Edge opens its inspector)
            if not win32api.GetAsyncKeyState(win32con.VK_SHIFT) & 0x8000:
                break
            time.sleep(0.02)
        self._capture(t)
        selection = (t.context or {}).get("selection", "")
        self.ui.put(lambda: TeachWindow(self.light.root, selection, target, self._learn_typed))

    def _learn_typed(self, heard, meant, target, selection):
        """Called by the teach window: put the fix over the selection and remember it."""
        if heard and target:
            if bring_to_front(target):
                time.sleep(0.1)
                paste(keep_punctuation(selection, meant))
            else:
                log.warning("couldn't switch back to the app; fix learned but not pasted")
        self._remember(heard, meant, "typed fix")

    def _remember(self, heard, meant, source):
        heard = strip_punctuation(heard)
        if heard and corrections.add(heard, meant, source):
            if len(meant.split()) <= 3:
                corrections.remember_word(meant)
            log.info("learned a fix (%s): %r -> %r", source, heard, meant)
            self._card("Learned", f"From now on, “{heard}” becomes “{meant}”.")
        elif meant and not heard:
            corrections.remember_word(meant)
            log.info("added word: %r", meant)
            self._card("Learned", f"“{meant}” added to your word list.")

    def _finish(self, t):
        audio = self.recorder.stop()
        t.state = IDLE
        self.active = None
        beep((660, 40))
        peak = float(np.abs(audio).max()) if len(audio) else 0.0
        if len(audio) > RATE * 0.3 and peak < CONFIG.get("min_peak", 0.005):
            log.info("%.1fs audio too quiet (peak %.4f), skipped", len(audio) / RATE, peak)  # stops stray one-word pastes
            self._light(None)
            if len(audio) >= RATE * 2 and self.meeting is None:
                # (never while meeting notes are recording: refreshing the audio system would cut that recording off)
                # A long, silent try usually means the USB mic reconnected and our device list went stale (only a
                # restart used to fix it). Refresh the audio system's device list and find the mic again.
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
            elif len(audio) >= RATE * 0.7:  # a real attempt, not a stray tap: say why nothing was typed
                self._card("Didn't hear you", "Your microphone is coming through almost silent, so nothing was typed. "
                           "Check it's plugged in and not muted, and speak into the end of the mic.", seconds=10)
        elif len(audio) > RATE * 0.3:
            self._light("working" if t.name == "dictate" else "thinking")
            self.jobs.put((t, audio))
        else:
            held = time.monotonic() - t.pressed_at
            if held >= 1.0:  # a real try that the microphone gave (almost) nothing for: never fail silently
                log.warning("key held %.1fs but the microphone gave only %.2fs of sound; nothing typed",
                            held, len(audio) / RATE)
            self._light(None)

    def _worker(self):
        while True:
            t, audio = self.jobs.get()
            try:
                if t.name == "dictate":
                    self._dictate(t, audio)
                else:
                    self._command(t, audio)
            except SpeechModelMissing:
                log.warning("%s: the speech model is missing or incomplete", t.name)
                self._light("error")
                beep((300, 300))
                self._tell_speech_missing(str(sys.exc_info()[1]))
            except Exception:
                log.exception("%s failed", t.name)
                self._light("error")
                beep((300, 300))
                self._card("Couldn't do that", "Please try again.", 8)

    _speech_card_at = 0.0

    def _tell_speech_missing(self, text=SPEECH_MISSING_TEXT):
        """A plain card (not more than one every 20 seconds) saying the speech model needs Finish setup."""
        if time.monotonic() - self._speech_card_at >= 20:
            self._speech_card_at = time.monotonic()
            self._card("Finish setup first", text or SPEECH_MISSING_TEXT, 15)

    def _wait_for_release(self, t):
        # Never paste while ANY trigger key is held (Alt+Ctrl+V is a different shortcut): the key may already be held
        # for the next dictation while this one is still being written. Jobs run one at a time, so order stays right.
        for _ in range(3000):  # up to 60 s, then go ahead anyway
            if not any(x.key_down for x in self.triggers.values()):
                return
            time.sleep(0.02)

    def _done(self, ok=True):
        if self.jobs.empty() and self.active is None:
            self._light(None if ok else "error")

    def _save_audio(self, audio, kind):
        """Keep the recording (16 kHz mono WAV) on this computer, if "Save recordings" is on."""
        if not CONFIG.get("save_recordings"):
            return None
        try:
            folder = HERE / "recordings" / time.strftime("%Y-%m-%d")
            folder.mkdir(parents=True, exist_ok=True)
            path = folder / f"{time.strftime('%H%M%S')}_{kind}.wav"
            with wave.open(str(path), "wb") as w:
                w.setnchannels(1)
                w.setsampwidth(2)
                w.setframerate(RATE)
                w.writeframes((np.clip(audio, -1, 1) * 32767).astype(np.int16).tobytes())
            return str(path.relative_to(HERE))
        except Exception as e:
            log.warning("recording not saved: %s", e)
            return None

    def _history(self, entry):
        entry = {"at": time.strftime("%Y-%m-%d %H:%M:%S"), **entry}
        with open(HERE / "history.jsonl", "a", encoding="utf-8") as f:
            f.write(json.dumps(entry) + "\n")

    def _dictate(self, t, audio):
        t0 = time.perf_counter()
        raw = self.transcribe(audio)
        usage.speech(len(audio) / RATE, "dictate")
        t1 = time.perf_counter()
        text, fixes = corrections.apply_counted(raw)  # (fixes = learned fixes made, for the Home counter)
        app = t.app or {}
        rule = styles.rule_for(app.get("app"), app.get("bundle"), title=app.get("title"))
        translating = bool(CONFIG.get("translate_to"))
        if styles.is_raw(rule):
            log.info("no tidy-up in %s (app rule)", app.get("app"))  # terminals and code editors: exactly as spoken
        elif raw and (translating or (CONFIG.get("cleanup") and len(raw.split()) > 2)):
            try:
                tidied = self.azure(text, styles.dictation_hint(rule, app.get("app")))
                if not (tidied or "").strip():
                    raise RuntimeError("the tidy-up came back empty")  # keep what was said instead of pasting nothing
                text, more = corrections.apply_counted(tidied)
                fixes += more
                if CONFIG.get("auto_learn", True) and not translating:  # (a translation can't be compared with what was said)
                    for heard, meant in corrections.learn_from(raw, text, known_words()):
                        log.info("learned a mishearing: %r -> %r", heard, meant)
            except TidyUpNotSetUp:
                # The model isn't downloaded yet: paste the words as heard (learned fixes applied), never nothing.
                log.info("tidy-up isn't set up yet; pasting the words as heard")
                self._tell_once_tidy_up_missing()
            except local_cleanup_win.ModelServerUnavailable:
                # The model's program wouldn't start (twice): stop trying for this run; the words are pasted as heard.
                log.warning("tidy-up unavailable: the model server won't start; pasting the words as heard")
                self._tell_once_model_unavailable()
            except Exception as e:
                log.warning("cleanup skipped: %s", str(e)[:200])
                if translating:
                    self._card("Couldn't translate", "The text was pasted as you said it. " + str(e)[:150], 8)
        t2 = time.perf_counter()
        log.info("%.1fs audio (peak %.3f) | speech %.2fs | cleanup %.2fs | %d words",
                 len(audio) / RATE, float(np.abs(audio).max()), t1 - t0, t2 - t1, len(text.split()))
        # Saved before pasting, so a failed paste never loses what was said.
        self._history({"raw": raw, "text": text, "app": app.get("app") or "", "audio": self._save_audio(audio, "dictate"),
                       "fixes": fixes})
        self._wait_for_release(t)
        if text:
            paste(text + " ")
        self._done(bool(text))

    _tidy_up_card_shown = False

    def _tell_once_tidy_up_missing(self):
        """One card per run: dictation is being typed as heard because the tidy-up model isn't downloaded yet."""
        if not self._tidy_up_card_shown:
            self._tidy_up_card_shown = True
            self._card("Tidy-up isn't set up yet", "Tidy-up isn't set up yet, so your words are typed as heard. "
                       "Open VoiceTool and click Finish setup.", 15)

    _model_unavailable_card_shown = False

    def _tell_once_model_unavailable(self):
        """One card per run: the tidy-up model's program won't start, so dictation is typed as heard."""
        if not self._model_unavailable_card_shown:
            self._model_unavailable_card_shown = True
            self._card("Tidy-up won't start", local_cleanup_win.UNAVAILABLE_TEXT.format(
                effect="your words are typed as heard"), 15)

    def _model_client(self, what="Commands"):
        """The client for a model call, or None (after a plain card) if the on-device model isn't set up yet or won't start."""
        try:
            return self.azure._client()
        except TidyUpNotSetUp:
            self._card("Finish setup first", f"{what} need the tidy-up model, which isn't set up yet. "
                       "Open VoiceTool and click Finish setup.", 12)
            return None
        except local_cleanup_win.ModelServerUnavailable:
            self._card("Tidy-up won't start", local_cleanup_win.UNAVAILABLE_TEXT.format(
                effect=f"{what.lower()} can't run right now"), 15)
            return None

    def _command(self, t, audio):
        t0 = time.perf_counter()
        said = corrections.apply(self.transcribe(audio))
        usage.speech(len(audio) / RATE, "command")
        if not said:
            self._done(False)
            return
        for _ in range(75):  # the screen context is usually ready long before the speech is
            if t.context is not None:
                break
            time.sleep(0.02)
        context = t.context or commands.window_info()
        client = self._model_client("Commands")
        if client is None:  # the on-device model isn't downloaded yet: a plain card, nothing else
            self._done()
            return
        kind, text, extra = commands.route(client, CONFIG["cleanup_model"], said, context, self._style(context))
        log.info("command | %s | %s | %.2fs | selection %d chars", kind, context.get("app"), time.perf_counter() - t0,
                 len(context.get("selection") or ""))
        self._history({"command": said, "route": kind, "app": context.get("app"), "title": context.get("title"),
                       "result": text, "audio": self._save_audio(audio, "command")})
        if kind == "fix" and text:
            # "That should be ...": correct the selection now and remember the pair for every future dictation.
            text = {w.lower(): w for w in known_words()}.get(text.lower(), text)  # spelled-out letters lose capitals
            heard = context.get("selection") or ""
            self._wait_for_release(t)
            paste(keep_punctuation(heard, text))
            self._remember(heard, text, "fix that")
            self._done()
        elif kind in ("rewrite", "write") and text:
            if kind == "rewrite":
                # Keep the selection's own spaces and line breaks around the new text so it drops back in cleanly.
                selected = context.get("selection") or ""
                text = selected[:len(selected) - len(selected.lstrip())] + text + selected[len(selected.rstrip()):]
            self._wait_for_release(t)
            paste(text)
            self._done()
        elif kind == "answer" and text:
            self._done()
            self._card("Answer", text, speak=True)
        elif kind == "system":
            self._done()
            self._system(t, extra, context)
        elif kind == "transcribe":
            self._done()
            self._card("Not included", "Transcribing links isn't included in this version.", 8)
        elif kind == "read":
            self._done()
            selection = (context.get("selection") or "").strip()
            if not selection:
                self._card("Nothing selected", "Select some text first, then hold the Left Windows key and say "
                           "\u201cread this to me\u201d.", 8)
            else:
                log.info("reading %d characters aloud", len(selection))
                self._card("Reading it to you", "Press Esc to stop.", 5, buttons=[("Stop", self._hush), ("Close", lambda: None)])
                self._read(selection)
        elif kind == "translate":
            self._done()
            self._set_translation(extra.get("target"))
        elif kind == "journal":
            self._journal(text or said)
            self._done()
        elif kind == "meeting":
            self._done()
            if (extra.get("target") or "").lower() == "stop":
                self.ui.put(self._stop_meeting)
            else:
                self.ui.put(self._start_meeting)
        elif kind == "team":
            self._done()
            self._card("Not included", "Messages to an AI team aren't included in this version.", 8)
        elif kind in ("screen", "open", "note", "history"):
            self._more(kind, text or said, extra, context)
            self._done()
        elif kind in ("action", "claude"):
            self._done()
            if not CONFIG.get("claude_actions", False):
                self._card("Email and calendar are off", CLAUDE_OFF_TEXT)
            else:
                threading.Thread(target=self._claude, args=(said, context), daemon=True).start()
        else:  # a route that came back empty or that isn't one we know: say so instead of guessing
            self._done()
            self._card("Couldn't do that", "I couldn't work out what to do with that. Try saying it another way.", 8)

    def _system(self, t, extra, context):
        """"Mute", "volume up", "lock the screen"...: one of the FIXED list in system_actions.ACTIONS, nothing else. Keys are
        sent only after the Command key is up (a held Win key would turn them into other shortcuts)."""
        import system_actions
        import system_windows
        self._wait_for_release(t)
        try:
            msg = system_actions.run(system_windows, extra.get("target"), extra.get("value"), context)
            log.info("computer command: %s", extra.get("target"))
            if extra.get("target") not in ("lock_screen", "close_window"):
                self._card("Done", msg, 4)
        except Exception as e:
            log.warning("computer command %s failed: %s", extra.get("target"), e)
            self._card("Couldn't do that", str(e)[:200], 6)

    def _set_translation(self, target):
        """"Translate everything into Spanish" / "stop translating" on the command key."""
        language = "" if (target or "").strip().lower() in ("", "off", "none", "stop", "english", "no") else target.strip().title()
        CONFIG["translate_to"] = language
        settings_window.save(CONFIG)
        log.info("translate dictation: %s", language or "off")
        self._card("Translation", f"Dictation now comes out in {language}. Say “stop translating” to go back."
                   if language else "Translation is off: dictation comes out as you speak it.", 8)

    def _journal(self, said):
        import voice_actions as va
        client = self._model_client("The journal")
        if client is None:
            return
        try:
            path = va.journal(client, CONFIG["cleanup_model"], said)
            self._card("Journal", f"Added to today's journal ({path.name}). It stays on this PC.", 8)
        except Exception as e:
            log.exception("journal failed")
            self._card("Couldn't save that", str(e)[:300])

    def _azure_only_client(self):
        """The online client, for the one job that has no on-device version (screen questions)."""
        return self.azure._azure_client()

    def _more(self, kind, text, extra, context):
        """The newer command-key jobs (voice_actions.py, with the Windows parts in actions_windows.py)."""
        import actions_windows as platform
        import voice_actions as va
        try:
            if kind == "screen":
                if CONFIG.get("offline_only", False) or not azure_configured():
                    self._card("Not available here", SCREEN_OFF_TEXT, 8)  # a vision model is needed; none runs offline
                    return
                shot = platform.screenshot_front_window(context.get("pid"), context.get("hwnd"))
                if shot is None:
                    self._card("Nothing to look at", "There's no window in front to read.")
                    return
                try:
                    answer = va.ask_screen(self._azure_only_client(), CONFIG["cleanup_model"], text, shot, context)
                finally:
                    Path(shot).unlink(missing_ok=True)  # never kept
                self._card("About your screen", answer, speak=True)
            elif kind == "open":
                target = (extra.get("target") or text or "").strip()
                if not platform.has_words(target):
                    self._card("Open", "I didn't catch what to open. Try “open Edge” or “open Notepad”.", 8)
                    return
                self._card("Open", va.open_thing(platform, target), 8)
            elif kind == "history":
                self._card("From your history", va.search_history(extra.get("target") or text), speak=True)
            elif kind == "note":
                where = va.save_note(text, extra.get("topic") or text, CONFIG, platform)
                self._card("Noted", where, 8)
                log.info("voice note saved")
            beep((988, 60), (1319, 80))
        except Exception as e:
            log.exception("%s command failed", kind)
            self._card("Couldn't do that", str(e)[:300])
            beep((300, 300))

    def _style(self, context=None):
        """Your style guide (if "Write like me" is on), plus the app's style and your named styles."""
        context = context or {}
        rule = styles.rule_for(context.get("app"), context.get("bundle"), title=context.get("title"))
        parts = [self._style_guide(), styles.writing_hint(rule, context.get("app"))]
        return "\n\n".join(p for p in parts if p)

    @staticmethod
    def _style_guide():
        return commands.style_guide() if CONFIG.get("use_style", True) else ""

    def _refresh_style(self):
        """Settings button: re-learn the style guide from sent emails in the background (a few minutes). Needs Claude."""
        if not CONFIG.get("claude_actions", False):
            self._card("Claude is off", CLAUDE_OFF_TEXT)
            return

        def check_then_run():
            problem = commands.claude_problem()  # (runs a program, so not on the window thread)
            if problem:
                self._card("Claude isn't ready", problem, 15)
            else:
                job()

        def job():
            with self._busy_lock:
                self.light.claude_busy += 1
            self._card("Learning your style", "Reading your sent emails. This takes a few minutes; "
                       "keep working, a card will say when it's done.")
            try:
                commands.learn_style(CONFIG.get("claude_model", "sonnet"))
                log.info("style guide refreshed")
                self._card("Style updated", "Your style guide was re-learned from your sent "
                           "emails. Open “My style” in settings to read or edit it.")
            except Exception as e:
                log.exception("style refresh failed")
                self._card("Couldn't update your style", str(e)[:300])
            finally:
                with self._busy_lock:
                    self.light.claude_busy -= 1
        threading.Thread(target=check_then_run, daemon=True).start()

    def _claude(self, said, context):
        problem = commands.claude_problem()  # not installed, or not signed in: say so on a card (never a crash)
        if problem:
            self._card("Claude isn't ready", problem, 15)
            return
        with self._busy_lock:
            if self.light is not None:
                self.light.claude_busy += 1
        try:
            try:
                if not CONFIG.get("claude_keep_running", True):
                    raise RuntimeError("keep-running is switched off")
                result, denied, secs = self.claude_session.ask(said, context, style=self._style(context))
            except Exception as e:
                log.warning("kept-running Claude not used (%s); starting a fresh one", str(e)[:200])
                result, denied, secs = commands.run_claude(said, context, CONFIG.get("claude_model", "sonnet"),
                                                           style=self._style(context))
            log.info("claude | %.1fs | denied %s", secs, denied or "nothing")
            self._history({"command": said, "route": "claude", "result": result, "denied": denied})
            self._card("Claude", result or "Done.", speak=True)
            beep((988, 60), (1319, 80))
        except Exception as e:
            log.exception("claude job failed")
            self._card("Claude couldn't do that", str(e)[:400])
            beep((300, 300))
        finally:
            with self._busy_lock:
                if self.light is not None:
                    self.light.claude_busy -= 1


# ---------- what the app window (ui_api.API) asks of the PC ----------

class WinHost:
    """The PC half of the app window's host: everything ui_api.API can ask for that depends on this computer.
    Called from the window's request threads, so anything that touches the pill, cards, tray or recorder is queued onto
    the main thread (tool.ui) instead of done here."""

    def __init__(self, tool):
        self.tool = tool
        self._signed_in = (0.0, False)

    def computer_name(self):
        return os.environ.get("COMPUTERNAME") or "This PC"

    def claude_signed_in(self):
        checked, value = self._signed_in  # the check runs a program, so ask at most once a minute
        if time.time() - checked > 60:
            value = commands.signed_in()
            self._signed_in = (time.time(), value)
        return value

    def paused(self, paused):
        self.tool.ui.put(lambda: self.tool._pause(paused, from_settings=True))  # the setting is already saved

    def startup_enabled(self):
        return settings_window.startup_enabled()

    def set_startup(self, on):
        settings_window.set_startup(on)

    def stop_claude(self):
        self.tool.claude_session.stop()

    def refresh_style(self):
        self.tool._refresh_style()

    def open_path(self, path):
        import actions_windows
        actions_windows.open_path(path)

    def copy_text(self, text):
        set_clipboard_text(text)

    def quit(self):
        self.tool.ui.put(self.tool._quit)

    def start_meeting(self):
        self.tool.ui.put(self.tool._start_meeting)

    def stop_meeting(self):
        self.tool.ui.put(self.tool._stop_meeting)

    def meeting_status(self):
        m, light = self.tool.meeting, self.tool.light
        return {"recording": m is not None, "seconds": round(time.time() - m.started_at) if m is not None else 0,
                "app": self.tool.meeting_app if m is not None else "",
                "writing": bool(light is not None and light.meeting == "meeting_notes")}

    def open_meeting_folder(self):
        self.tool._open_meeting_folder()

    def offline_available(self):
        import local_cleanup_win
        return local_cleanup_win.available(CONFIG)

    def list_voices(self):
        """The voices built into Windows (what Settings lists for "Read it to me")."""
        import actions_windows
        try:
            return actions_windows.list_voices()
        except Exception:
            log.exception("could not list the Windows voices")
            return []

    def read_aloud(self, text):
        self.tool._read(text)

    def note_destinations(self):
        # Notes are Markdown files in a folder: that works with Obsidian, OneNote's import and anything else that reads files.
        return ["folder"]

    def choose_folder(self, start=""):
        """A folder picker (the window's own dialog, else a Windows one); returns the chosen path or ''."""
        window = self.tool.app_window
        if window is not None and window.is_open() and window.ready:
            return window.choose_folder(start)
        return self.tool.choose_folder_tk(start)

    # ----- "Finish setup": downloading the tidy-up model (fetch_models.py shows its own progress window) -----

    _setup_job = None  # {"code": None while the downloader runs, else its exit code}

    def setup_status(self):
        """What the Home page needs: is the tidy-up model missing, and is the downloader running or did it fail?"""
        job = self._setup_job
        speech = speech_model_missing(self.tool.transcribe)
        return {"missing": speech or not local_cleanup_win.available(CONFIG), "speech_missing": speech,
                "running": bool(job and job["code"] is None),
                "failed": bool(job and job["code"] not in (None, 0))}

    def finish_setup(self):
        """Start `pythonw.exe fetch_models.py --window` from this folder's .venv (no console window). When
        it ends with code 0 the on-device model is started (sync_offline) and the button goes away."""
        script = HERE / "fetch_models.py"
        if self._setup_job is not None and self._setup_job["code"] is None:
            return {"started": False, "running": True, "message": "The download is already running in its own window."}
        if not script.exists():
            return {"started": False, "message": "The downloader isn't in this install (fetch_models.py is missing). "
                                                 "Run Setup again to repair VoiceTool."}
        python = HERE / ".venv" / "Scripts" / "pythonw.exe"
        if not python.exists():
            python = Path(sys.executable)
        try:
            proc = subprocess.Popen([str(python), str(script), "--window"], cwd=str(HERE),
                                    creationflags=subprocess.CREATE_NO_WINDOW)
        except Exception as e:
            log.exception("could not start the downloader")
            return {"started": False, "message": f"Couldn't start the downloader: {str(e)[:150]}"}
        job = self._setup_job = {"code": None}
        log.info("finish setup: the downloader started")

        def watch():
            try:
                code = proc.wait()
            except Exception:
                code = -1
            log.info("finish setup: the downloader ended with code %s", code)
            # The downloader wrote this computer's settings (which tidy-up build, where the models are) into config.json
            # in its own process. Take them into memory first, so a later settings save can't overwrite them.
            reload_machine_settings()
            local = getattr(self.tool.azure, "local", None)
            if local is not None and hasattr(local, "refresh"):
                local.refresh()
            try:
                if getattr(self.tool.transcribe, "missing", False) and hasattr(self.tool.transcribe, "reload"):
                    self.tool.transcribe.reload()  # dictation starts working without a restart
            except Exception:
                log.exception("could not load the speech model after the download")
            job["code"] = code
            if code == 0 and hasattr(self.tool.azure, "sync_offline"):
                try:
                    self.tool.azure.sync_offline()
                except Exception:
                    log.exception("could not start the on-device model after the download")
        threading.Thread(target=watch, daemon=True, name="finish-setup").start()
        return {"started": True}

    def setting_changed(self, key, on):
        """The window switched a setting: the two offline ones start or stop the model's server."""
        if key in ("offline_tidyup", "offline_only") and hasattr(self.tool.azure, "sync_offline"):
            self.tool.azure.sync_offline()


OPEN_SETTINGS_EVENT = "VoiceTool-open-settings"  # the Start-menu entry sets this: "open the VoiceTool window"
MUTEX_NAME = "VoiceTool-single-instance"


class _TestRecorder:
    """Test mode: no microphone."""
    device = None
    level = 0.0

    def start(self):
        pass

    def stop(self):
        return np.zeros(0, np.float32)


class _TestCleaner:
    """Test mode: no model at all."""
    local = None

    def warm_up(self):
        pass

    def _client(self):
        raise RuntimeError("test mode: no model")


def hide_console_windows():
    """VoiceTool runs without a console (pythonw), so any console program it starts gets a black window of its own.
    Our own calls already ask for none, but libraries don't: the Azure sign-in runs az.cmd about once an hour, and that
    window flashed up on the first dictation and again "after a while". From now on every program started from here
    (including inside libraries) starts without a window unless the caller asks for something else."""
    original = subprocess.Popen.__init__
    if getattr(original, "hides_console", False):
        return

    def init(self, *args, **kwargs):
        if not kwargs.get("creationflags") and len(args) < 14:  # (creationflags is Popen's 14th parameter)
            kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW
        original(self, *args, **kwargs)
    init.hides_console = True
    subprocess.Popen.__init__ = init


def main():
    hide_console_windows()
    # Test mode (VOICETOOL_TEST=1): a copy for testing the app window beside the real tool. It has its own single-
    # instance names, no keyboard hook, no microphone, no speech model, no tray icon and no call watcher.
    test = os.environ.get("VOICETOOL_TEST") == "1"
    suffix = os.environ.get("VOICETOOL_INSTANCE", "") or ("test" if test else "")
    mutex_name = MUTEX_NAME + (f"-{suffix}" if suffix else "")
    event_name = OPEN_SETTINGS_EVENT + (f"-{suffix}" if suffix else "")
    if test:
        os.environ.setdefault("VOICETOOL_TASK", "VoiceTool-test-none")
        CONFIG["beeps"] = False
    # Started from the Start menu ("--settings"): if VoiceTool is already running, ask it to open its window and stop.
    show_window = "--settings" in sys.argv
    mutex = win32event.CreateMutex(None, False, mutex_name)
    if win32api.GetLastError() == winerror.ERROR_ALREADY_EXISTS:
        if show_window:
            try:
                import ctypes
                ctypes.windll.user32.AllowSetForegroundWindow(-1)  # we were just clicked: let the window come to the front
                win32event.SetEvent(win32event.OpenEvent(win32event.EVENT_MODIFY_STATE, False, event_name))
            except Exception:
                log.exception("could not reach the running VoiceTool")
        else:
            log.info("already running")
        return
    if not test:
        # VoiceTool needs the processor for a second or so each time you talk; above-normal priority keeps it quick
        # when other heavy work (browser test runs, builds) is busy (measured: 1.3 s -> 0.8 s for a 13 s clip).
        try:
            import win32process
            win32process.SetPriorityClass(win32api.GetCurrentProcess(), win32process.ABOVE_NORMAL_PRIORITY_CLASS)
        except Exception as e:
            log.warning("could not raise priority: %s", e)
    tool = None
    requested = threading.Event()  # a click on the Start-menu entry while VoiceTool is still starting up

    def wait_for_requests(event):
        while True:
            win32event.WaitForSingleObject(event, win32event.INFINITE)
            if tool is not None:
                tool.ui.put(tool._open_app)
            else:
                requested.set()
    try:
        # made before the (slow) speech model loads, so an early click is remembered instead of lost
        event = win32event.CreateEvent(None, False, False, event_name)
        threading.Thread(target=wait_for_requests, args=(event,), daemon=True).start()
        if test:
            time.sleep(float(os.environ.get("VOICETOOL_TEST_START_DELAY", "0")))  # (tests: a slow start-up)
            tool = VoiceTool(recorder=_TestRecorder(), transcriber=lambda audio: "", cleaner=_TestCleaner())
            tool.test_mode = True
        else:
            tool = VoiceTool()
        if show_window or requested.is_set() or (CONFIG.get("first_run") and not test):
            tool.ui.put(tool._open_app)  # (the first start opens the window on its Welcome card)
        tool.run(hardware=not test)
    except Exception:
        log.exception("VoiceTool stopped")
        raise
    finally:
        del mutex


if __name__ == "__main__":
    main()
