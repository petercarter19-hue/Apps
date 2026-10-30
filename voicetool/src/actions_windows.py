"""The PC's half of voice_actions: a screenshot of the window in front, finding apps and files, opening things.
(The Mac has its own file, actions_mac.py, with the same function names.)

Everything here only OPENS or READS. Nothing is ever run as a command:
- open_path() refuses programs and scripts (.exe, .bat, .ps1 ...); only documents, folders, links and Start-menu
  shortcuts open. The one exception is an app you listed yourself as a voice shortcut of kind "app".
- voice shortcuts are an allow-list in shortcuts.json (kinds: url, app, file, shortcut), checked by voice_actions.
"""
import ctypes
import json
import os
import re
import subprocess
import tempfile
import threading
import time
import webbrowser
from pathlib import Path
from urllib.parse import urlparse

STOPWORDS = {"the", "my", "a", "an", "open", "launch", "start", "show", "me", "app", "application", "program", "file",
             "folder", "document", "please"}
# Programs and scripts: found by name, they are never opened (a stray "open the build script" must not run it).
RISKY_EXT = {".exe", ".bat", ".cmd", ".com", ".scr", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
             ".msi", ".msp", ".msc", ".cpl", ".hta", ".reg", ".jar", ".pif", ".gadget"}
SKIP_DIRS = {".venv", "venv", "node_modules", ".git", "__pycache__", "site-packages", "$recycle.bin",
             "system volume information", ".cache", ".tox", "appdata"}
SEARCH_SECONDS = 4.0        # hard limit for one file search, however slow a network or cloud drive is
SEARCH_MAX_ENTRIES = 80_000  # and a limit on how many names are looked at


def start_menu_folders():
    folders = [Path(os.environ.get("ProgramData", r"C:\ProgramData")) / "Microsoft" / "Windows" / "Start Menu" / "Programs"]
    if os.environ.get("APPDATA"):
        folders.append(Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu" / "Programs")
    return folders


def search_roots():
    """(folder, how many levels down to look), most likely places first."""
    home = Path.home()
    roots = [(home / "Desktop", 5), (home / "Documents", 5), (home / "Downloads", 5)]
    if os.environ.get("OneDrive"):
        roots.append((Path(os.environ["OneDrive"]), 5))
    return roots


# ---------- a picture of the window in front ----------

def _set_thread_dpi_aware():
    """Make THIS THREAD DPI-aware so window sizes come back in real pixels. Thread-only on purpose: setting it for the
    whole process would change how VoiceTool's own pill and cards are drawn."""
    try:
        return ctypes.windll.user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2
    except Exception:
        return None


def _restore_dpi(previous):
    if previous:
        try:
            ctypes.windll.user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(previous))
        except Exception:
            pass


def _usable(hwnd):
    import win32gui
    try:
        if not (win32gui.IsWindow(hwnd) and win32gui.IsWindowVisible(hwnd)) or win32gui.IsIconic(hwnd):
            return False
        l, t, r, b = win32gui.GetWindowRect(hwnd)
        return r - l > 50 and b - t > 50
    except Exception:
        return False


def _pick_window(pid, hwnd):
    """The window to photograph: the one remembered when the command key was pressed, else the front app's biggest
    window, else whatever is in front now. Never one of VoiceTool's own windows."""
    import win32gui
    import win32process
    own = os.getpid()

    def pid_of(h):
        try:
            return win32process.GetWindowThreadProcessId(h)[1]
        except Exception:
            return 0
    if hwnd and _usable(hwnd) and pid_of(hwnd) != own:
        return hwnd
    if pid and pid != own:
        found = []

        def each(h, _):
            if pid_of(h) == pid and _usable(h) and not win32gui.GetWindow(h, 4):  # 4 = GW_OWNER: top-level, unowned
                l, t, r, b = win32gui.GetWindowRect(h)
                found.append(((r - l) * (b - t), h))
        win32gui.EnumWindows(each, None)
        if found:
            return max(found)[1]
    front = win32gui.GetForegroundWindow()
    if front and _usable(front) and pid_of(front) != own:
        return front
    return None


def _extended_frame(hwnd):
    """The visible edges of a window (without the invisible resize border Windows 10 and 11 add)."""
    from ctypes import wintypes
    rect = wintypes.RECT()
    try:
        ok = ctypes.windll.dwmapi.DwmGetWindowAttribute(wintypes.HWND(hwnd), 9, ctypes.byref(rect), ctypes.sizeof(rect))
        if ok == 0:
            return rect.left, rect.top, rect.right, rect.bottom
    except Exception:
        pass
    import win32gui
    return win32gui.GetWindowRect(hwnd)


def _print_window(hwnd):
    """Ask the window to draw itself into a bitmap (works even if another window is on top of it)."""
    import win32gui
    import win32ui
    from PIL import Image
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    w, h = right - left, bottom - top
    hdc = win32gui.GetWindowDC(hwnd)
    src = win32ui.CreateDCFromHandle(hdc)
    mem = src.CreateCompatibleDC()
    bitmap = win32ui.CreateBitmap()
    try:
        bitmap.CreateCompatibleBitmap(src, w, h)
        mem.SelectObject(bitmap)
        ok = ctypes.windll.user32.PrintWindow(hwnd, mem.GetSafeHdc(), 2)  # 2 = PW_RENDERFULLCONTENT
        if not ok:
            return None
        image = Image.frombuffer("RGB", (w, h), bitmap.GetBitmapBits(True), "raw", "BGRX", 0, 1)
    finally:
        win32gui.DeleteObject(bitmap.GetHandle())
        mem.DeleteDC()
        src.DeleteDC()
        win32gui.ReleaseDC(hwnd, hdc)
    el, et, er, eb = _extended_frame(hwnd)
    box = (max(0, el - left), max(0, et - top), min(w, er - left), min(h, eb - top))
    return image.crop(box) if box[2] > box[0] and box[3] > box[1] else image


def _blank(image):
    return all(lo == hi for lo, hi in image.getextrema())


def capture_window_image(pid=None, hwnd=None):
    """A full-size picture (PIL image) of just the window in front, or None if there is none. Not the whole screen, and
    never one of VoiceTool's own windows."""
    from PIL import ImageGrab
    previous = _set_thread_dpi_aware()
    try:
        target = _pick_window(pid, hwnd)
        if target is None:
            return None
        image = None
        try:
            image = _print_window(target)
        except Exception:
            image = None
        if image is None or _blank(image):  # some GPU-drawn windows print black: copy their pixels from the screen
            image = ImageGrab.grab(bbox=_extended_frame(target), all_screens=True).convert("RGB")
        return image.convert("RGB")
    finally:
        _restore_dpi(previous)


def screenshot_front_window(pid=None, hwnd=None, max_side=1600):
    """A JPEG (in the temp folder; the caller deletes it) of just the window in front, shrunk for the model. None if
    there's no window to photograph."""
    from PIL import Image
    image = capture_window_image(pid, hwnd)
    if image is None:
        return None
    if max(image.size) > max_side:
        scale = max_side / max(image.size)
        image = image.resize((max(1, round(image.width * scale)), max(1, round(image.height * scale))), Image.LANCZOS)
    fd, name = tempfile.mkstemp(prefix="voicetool-screen-", suffix=".jpg")
    os.close(fd)
    image.save(name, "JPEG", quality=82)
    return Path(name)


def screenshots_folder():
    """Where Windows keeps screenshots: Pictures\\Screenshots."""
    return Path(os.environ.get("USERPROFILE") or Path.home()) / "Pictures" / "Screenshots"


def save_window_screenshot(pid=None, hwnd=None, folder=None):
    """"Screenshot this window": the window in front, full size, saved as Pictures\\Screenshots\\VoiceTool <time>.png.
    Returns the path, or None if there is no window to photograph."""
    image = capture_window_image(pid, hwnd)
    if image is None:
        return None
    folder = Path(folder) if folder else screenshots_folder()
    folder.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y-%m-%d %H%M%S")
    path, n = folder / f"VoiceTool {stamp}.png", 2
    while path.exists():
        path = folder / f"VoiceTool {stamp} {n}.png"
        n += 1
    image.save(path, "PNG")
    return path


# ---------- apps ----------

_cache = {"menu": (0.0, {}), "store": (0.0, [])}


def _clean_words(words):
    return [w for w in re.findall(r"[a-z0-9+#.]+", str(words).lower()) if w not in STOPWORDS]


def has_words(words):
    """False if nothing but filler was said ("open", "the", "my"): there is nothing to look for."""
    return bool(_clean_words(words))


def _start_menu_items():
    """{shortcut name in lower case: path of its .lnk}, from both Start-menu folders (cached for a minute)."""
    stamp, items = _cache["menu"]
    if time.time() - stamp < 60 and items:
        return items
    items = {}
    for folder in start_menu_folders():
        try:
            for lnk in folder.rglob("*.lnk"):
                name = lnk.stem.lower()
                if "uninstall" in name or name in items:
                    continue
                items[name] = str(lnk)
        except OSError:
            continue
    _cache["menu"] = (time.time(), items)
    return items


def _store_apps():
    """[(name, shell address)] for Store apps like the new Teams or Outlook, which have no .lnk file. Slow (a second
    or two), so only asked when no shortcut matched, and cached for ten minutes."""
    stamp, apps = _cache["store"]
    if time.time() - stamp < 600 and apps:
        return apps
    apps = []
    try:
        out = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command",
                              "Get-StartApps | ConvertTo-Json -Compress"], capture_output=True, text=True, timeout=10,
                             creationflags=subprocess.CREATE_NO_WINDOW).stdout
        data = json.loads(out) if out.strip() else []
        for d in data if isinstance(data, list) else [data]:
            if d.get("Name") and d.get("AppID") and "!" in d["AppID"]:  # Store apps only (Package!App)
                apps.append((d["Name"], "shell:AppsFolder\\" + d["AppID"]))
    except Exception:
        pass
    _cache["store"] = (time.time(), apps)
    return apps


def _best(wanted, names):
    """The best match for the spoken words among `names`: exact, else starts with, else all the words appear."""
    text = " ".join(wanted)
    exact = [n for n in names if n.lower() == text]
    if exact:
        return exact[0]
    if len(text) < 3:
        return None
    loose = [n for n in names
             if n.lower().startswith(text) or all(w in re.findall(r"[a-z0-9+#.]+", n.lower()) for w in wanted)]
    return min(loose, key=len) if loose else None  # the shortest name: "Word" before "Word Viewer"


def find_app(words):
    """An installed app whose name matches what was said ("open Edge", "open Outlook"). Returns something
    open_path() can open: a Start-menu .lnk, or a shell:AppsFolder address for Store apps. None if nothing matches."""
    wanted = _clean_words(words)
    if not wanted:
        return None
    items = _start_menu_items()
    name = _best(wanted, list(items))
    if name:
        return items[name.lower()]
    store = {n: addr for n, addr in _store_apps()}
    name = _best(wanted, list(store))
    return store[name] if name else None


def app_label(path):
    """What to call an app in 'Opened ...' (the Start-menu title, not 'Package!App')."""
    p = str(path)
    if p.lower().startswith("shell:appsfolder"):
        for name, addr in _cache["store"][1]:
            if addr == p:
                return name
        return p.split("\\")[-1].split("!")[-1]
    return Path(p).stem


# ---------- files ----------

def _normalise(name):
    return re.sub(r"[_\-.()\[\]]+", " ", name.lower())


def search_files(words, limit=5, roots=None, seconds=SEARCH_SECONDS):
    """Files and folders whose name contains every spoken word, newest first. Looks in Desktop, Documents, Downloads,
    OneDrive, skips project internals (.venv, node_modules, .git),
    never lists programs or scripts, and gives up after `seconds` (cloud and network drives can be slow)."""
    terms = _clean_words(words)
    if not terms:
        return []
    roots = roots if roots is not None else search_roots()
    hits, seen = [], set()
    deadline = time.monotonic() + seconds
    entries = [0]

    def scan(root, max_depth):
        stack = [(Path(root), 0)]
        while stack and time.monotonic() < deadline and entries[0] < SEARCH_MAX_ENTRIES and len(hits) < limit * 6:
            folder, depth = stack.pop(0)
            folder = Path(folder)
            try:
                real = os.path.normcase(os.path.realpath(folder))
                if real in seen:
                    continue
                seen.add(real)
                with os.scandir(folder) as it:
                    children = list(it)
            except OSError:
                continue
            for entry in children:
                entries[0] += 1
                name = entry.name
                try:
                    is_dir = entry.is_dir(follow_symlinks=False)
                except OSError:
                    continue
                if name.lower() in SKIP_DIRS or (name.lower() == "recordings" and folder.name.lower().startswith("voicetool")):
                    continue  # (VoiceTool's own saved voice clips)
                if is_dir and depth + 1 < max_depth:
                    stack.append((entry.path, depth + 1))
                if Path(name).suffix.lower() in RISKY_EXT and not is_dir:
                    continue
                flat = _normalise(name)
                if all(t in flat for t in terms):
                    hits.append(entry.path)

    def run():
        for root, depth in roots:
            if time.monotonic() >= deadline:
                break
            try:
                if Path(root).exists():
                    scan(root, depth)
            except Exception:
                continue  # one odd folder or drive must not lose what was found elsewhere

    worker = threading.Thread(target=run, daemon=True)
    worker.start()
    worker.join(seconds + 0.5)  # a drive that never answers can't hold the command up past this
    found = list(dict.fromkeys(hits))

    def age(p):
        try:
            return os.path.getmtime(p)
        except OSError:
            return 0
    return sorted(found, key=age, reverse=True)[:limit]


# ---------- opening things ----------

def open_path(path, allow_launch=False):
    """Open a document, folder, link or Start-menu shortcut with whatever Windows uses for it. Programs and scripts
    are refused unless allow_launch (only for an app you listed as a voice shortcut)."""
    text = str(path)
    if not text.lower().startswith("shell:appsfolder") and Path(text).suffix.lower() in RISKY_EXT and not allow_launch:
        raise PermissionError(f"VoiceTool only opens documents, folders, links and apps, not programs or scripts: {Path(text).name}")
    os.startfile(text)


def open_shortcut(sc):
    """Open one voice shortcut from shortcuts.json. kind: url (a web link), app (an installed app or a program
    you named), file (a document or folder), shortcut (a Windows shortcut, by Start-menu name or .lnk/.url path)."""
    kind, target = sc["kind"], str(sc["target"]).strip()
    if kind == "url":
        if urlparse(target).scheme not in ("http", "https"):
            raise ValueError("A website shortcut has to start with http:// or https://")
        webbrowser.open(target)
    elif kind == "app":
        if Path(target).exists():
            open_path(target, allow_launch=True)
        else:
            found = find_app(target)
            if not found:
                raise FileNotFoundError(f"I couldn't find an app called “{target}”")
            open_path(found)
    elif kind == "file":
        path = Path(os.path.expandvars(os.path.expanduser(target)))
        if not path.exists():
            raise FileNotFoundError(f"That isn't there: {target}")
        open_path(path)
    elif kind == "shortcut":
        path = Path(os.path.expandvars(target))
        if path.suffix.lower() in (".lnk", ".url") and path.exists():
            open_path(path)
        else:
            items = _start_menu_items()
            if target.lower() not in items:
                raise FileNotFoundError(f"I couldn't find a Windows shortcut called “{target}”")
            open_path(items[target.lower()])
    else:
        raise ValueError(f"unknown shortcut kind {kind!r}")


# ====================================================================================================== reading aloud
# (the speaking half of read_aloud.py: the computer's own voices, free and private; see speaker_windows.py)

RATES = {"slow": -2, "normal": 0, "fast": 3}  # the shared slow / normal / fast setting as SAPI rates (-10 slowest .. 10 fastest)
_speaker = None


def _the_speaker(log=None):
    global _speaker
    if _speaker is None:
        import speaker_windows
        _speaker = speaker_windows.Speaker(log)
    return _speaker


def speak(text, voice="", rate="normal", limit=20000, log=None):
    """Read `text` aloud with a built-in Windows voice (SAPI), at once and without waiting; it replaces anything still
    being read. voice: a voice's name, or part of it ("" = the Windows default). rate: "slow", "normal", "fast" (or a
    SAPI number). One voice setting serves "read this to me" and spoken answers."""
    number = RATES.get(rate, rate) if isinstance(rate, str) else rate
    _the_speaker(log).say(text, voice=voice, rate=int(number) if number is not None else None, limit=limit)


def stop_speaking():
    """Cut off whatever is being read (the voice is purged, so it stops at once; a natural-voice file stops too)."""
    if _speaker is not None:
        _speaker.stop()
    stop_audio()


def list_voices():
    """The Windows voices installed on this PC, as [{"name": ..., "lang": ...}] (what the Settings page lists)."""
    import speaker_windows
    return [{"name": n, "lang": ""} for n in speaker_windows.list_voices()]


def _mci(command):
    return ctypes.windll.winmm.mciSendStringW(command, None, 0, None)


def play_audio_file(path):
    """Play an audio file (the natural Azure voice's mp3) in the background; stop_audio() / Esc stops it."""
    stop_audio()
    if _mci(f'open "{path}" type mpegvideo alias voicetoolread') == 0:
        _mci("play voicetoolread")


def stop_audio():
    _mci("stop voicetoolread")
    _mci("close voicetoolread")


def app_note(where, title, text, when):
    """A note app as a notes destination (the Mac has Apple Notes). The PC has none: OneNote has no supported, reliable
    way in from here, so the destination isn't offered; this refuses and the note is saved in the folder instead."""
    raise RuntimeError(f"notes can't be sent to {where} from this PC")
