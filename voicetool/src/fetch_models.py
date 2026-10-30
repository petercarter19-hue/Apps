"""Downloads the big pieces VoiceTool needs and checks them: the speech model (only if it is missing), the tidy-up model
and the program that runs it. Setup runs this, and so does the "Finish setup" button in VoiceTool.

    pythonw fetch_models.py --window     a small window with a progress bar (what the Finish setup button runs)
    python  fetch_models.py              the same, as text in a console (what Setup uses when it has no window to show)
    python  fetch_models.py --check      only checks what is already here; downloads nothing

Exit code 0 means everything is here and checked. Anything else means something is missing (the window or the console
says what, in plain words); VoiceTool still dictates (raw) without the tidy-up pieces.

How it behaves: files are checked against a SHA-256 written in this file, so a damaged or swapped download is thrown away
and fetched again. A dropped connection resumes where it stopped (HTTP Range) and retries by itself, waiting a little
longer each time. HTTPS is checked with Windows' own certificate store (the truststore package that Setup installs), because
Python alone doesn't fetch the root certificates a fresh Windows leaves out; if truststore can't load, certifi's list is
used. Certificates are never switched off. Apart from that this file needs nothing but the standard library.

Options for tests and Setup:  --root DIR (the VoiceTool folder), --cache DIR (keep the downloaded files there),
--manifest FILE.json (a different list of files), --configure (only write the machine-specific settings), --defaults FILE.
"""
import argparse
import collections
import hashlib
import http.client
import json
import os
import re
import shutil
import socket
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
CHUNK = 256 * 1024
CREATE_NO_WINDOW = 0x08000000

LLAMA_TAG = "b11388"
LLAMA_URL = f"https://github.com/ggml-org/llama.cpp/releases/download/{LLAMA_TAG}/"
# Qwen's own Hugging Face account does not publish this GGUF file, so it comes from Unsloth's repository (the same file,
# byte for byte, as the one VoiceTool was developed with), pinned to one exact revision. See THIRD-PARTY-NOTICES.md.
QWEN_URL = ("https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/"
            "a06e946bb6b655725eafa393f4a9745d460374c9/Qwen3-4B-Instruct-2507-Q4_K_M.gguf")
PARAKEET_URL = ("https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/"
                "0bbb45a3365852604aef28b538a8f066f4ccaa85/")

QWEN_REL = r"models\qwen3-4b-instruct-2507\Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
PARAKEET_REL = r"models\parakeet-tdt-0.6b-v2"

# kind "zip": unpacked into dest (a folder); "marker" is a file that must exist there afterwards.
# kind "file": saved as dest (a file). The speech model comes first: it is what dictation needs.
MANIFEST = [
    {"id": "parakeet-config", "label": "the speech model", "kind": "file", "name": "parakeet-config.json",
     "url": PARAKEET_URL + "config.json",
     "sha256": "666903c76b9798caf2c210afd4f6cd60b08a8dbf9800ec8d7a3bc0d2148ac466", "size": 97,
     "dest": PARAKEET_REL + r"\config.json"},
    {"id": "parakeet-vocab", "label": "the speech model", "kind": "file", "name": "parakeet-vocab.txt",
     "url": PARAKEET_URL + "vocab.txt",
     "sha256": "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d", "size": 9384,
     "dest": PARAKEET_REL + r"\vocab.txt"},
    {"id": "parakeet-decoder", "label": "the speech model", "kind": "file",
     "name": "parakeet-decoder_joint-model.int8.onnx",
     "url": PARAKEET_URL + "decoder_joint-model.int8.onnx",
     "sha256": "a449f49acd68979d418651dd2dcb737cc0f1bf0225e009e29ee326354edbf7d3", "size": 8998286,
     "dest": PARAKEET_REL + r"\decoder_joint-model.int8.onnx"},
    {"id": "parakeet-encoder", "label": "the speech model", "kind": "file", "name": "parakeet-encoder-model.int8.onnx",
     "url": PARAKEET_URL + "encoder-model.int8.onnx",
     "sha256": "3e0581fda6ab843888b51e56d7ee78b6d5bc3237ec113af1f732d1d5286aa155", "size": 652184014,
     "dest": PARAKEET_REL + r"\encoder-model.int8.onnx"},
    {"id": "llama-vulkan", "label": "the tidy-up engine", "kind": "zip",
     "name": f"llama-{LLAMA_TAG}-bin-win-vulkan-x64.zip",
     "url": LLAMA_URL + f"llama-{LLAMA_TAG}-bin-win-vulkan-x64.zip",
     "sha256": "0cc41db2e39901a3361ff8a1354e93a21cb1f4196768c261fbcbba2c33231458", "size": 33282695,
     "dest": r"runtime\llama-vulkan", "marker": "llama-server.exe"},
    {"id": "llama-cpu", "label": "the tidy-up engine (backup copy)", "kind": "zip",
     "name": f"llama-{LLAMA_TAG}-bin-win-cpu-x64.zip",
     "url": LLAMA_URL + f"llama-{LLAMA_TAG}-bin-win-cpu-x64.zip",
     "sha256": "679d167cd9d59d539a721fbc34305a1fea365ced1f4d0f87f4ccde3d4a403a7e", "size": 19363391,
     "dest": r"runtime\llama-cpu", "marker": "llama-server.exe"},
    {"id": "qwen", "label": "the tidy-up model", "kind": "file", "name": "Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
     "url": QWEN_URL,
     "sha256": "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597", "size": 2497281120,
     "dest": QWEN_REL},
]

# Settings written into config.json for this computer (paths are relative to the VoiceTool folder).
MACHINE_KEYS = {
    "offline_server": r"runtime\llama-vulkan\llama-server.exe",
    "offline_server_cpu": r"runtime\llama-cpu\llama-server.exe",
    "offline_model": QWEN_REL,
    "speech_model_path": PARAKEET_REL,
}
BASE_ARGS = ["-c", "16384", "--parallel", "1", "--no-webui"]
SOFTWARE_GPU_WORDS = ("llvmpipe", "swiftshader", "basic render", "warp", "software")
MIN_GPU_MIB = 4000  # the model plus its working memory need about 3.5 GB; less than this runs better on the processor


class FetchError(Exception):
    """Something went wrong; the message is plain English and safe to show to anyone."""


class Cancelled(Exception):
    pass


def say(text):
    try:
        if sys.stdout:
            sys.stdout.write(text)
            sys.stdout.flush()
    except Exception:
        pass


def fmt_size(n):
    return f"{n / 1e9:.1f} GB" if n >= 1e8 else f"{n / 1e6:.0f} MB"


def fmt_eta(seconds):
    if seconds is None:
        return "working out the time left..."
    if seconds < 60:
        return "less than a minute left"
    minutes = round(seconds / 60)
    if minutes < 90:
        return f"about {minutes} minute{'s' if minutes != 1 else ''} left"
    return f"about {seconds / 3600:.1f} hours left"


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while True:
            block = f.read(1024 * 1024)
            if not block:
                break
            h.update(block)
    return h.hexdigest()


_context = None


def ssl_context():
    """The TLS settings for every download: the operating system's own verifier through truststore (a fresh Windows only
    has a small root store and Python's ssl doesn't ask Windows for the rest, but Windows' verifier does), else certifi's
    bundle, else Python's default. Certificate and host-name checking are always on. (Plain http:// downloads, used by the
    tests on this computer, ignore it.)"""
    global _context
    if _context is None:
        try:
            import truststore
            _context = truststore.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        except Exception:
            try:
                import certifi
                _context = ssl.create_default_context(cafile=certifi.where())
            except Exception:
                _context = ssl.create_default_context()
    return _context


def plain_error(e):
    """A short, plain reason for a failed download."""
    if isinstance(e, FetchError):
        return str(e)
    if isinstance(getattr(e, "reason", None), ssl.SSLError) or isinstance(e, ssl.SSLError):
        return "This computer doesn't trust the download site's security certificate (check the date and time, and any antivirus that inspects web traffic)"
    if isinstance(e, urllib.error.HTTPError):
        if e.code in (401, 403, 404, 410):
            return f"The download site no longer has this file (error {e.code}). Please tell whoever sent you VoiceTool"
        return f"The download site had a problem (error {e.code})"
    reason = getattr(e, "reason", e)
    if isinstance(reason, socket.gaierror) or "getaddrinfo" in str(reason) or "11001" in str(reason):
        return "VoiceTool couldn't reach the internet"
    if isinstance(reason, (socket.timeout, TimeoutError)) or isinstance(e, (socket.timeout, TimeoutError)):
        return "The internet connection is very slow or stopped"
    if isinstance(e, OSError) and getattr(e, "errno", None) == 28:
        return "The disk is full"
    if isinstance(e, PermissionError):
        return "Windows wouldn't let VoiceTool save a file there"
    if isinstance(reason, ConnectionError) or isinstance(e, (ConnectionError, http.client.HTTPException)):
        return "The internet connection dropped"
    return "The download was interrupted"


class State:
    """What the window or console shows. Written by the worker, read by the screen."""

    def __init__(self):
        self.lock = threading.Lock()
        self.status = "Getting ready..."
        self.total = 0
        self.done = 0
        self.finished = False
        self.ok = False
        self.final = ""
        self.samples = collections.deque()
        self.cancel = threading.Event()

    def set(self, status):
        with self.lock:
            self.status = status

    def set_done(self, n):
        with self.lock:
            self.done = n
            now = time.monotonic()
            self.samples.append((now, n))
            while self.samples and now - self.samples[0][0] > 20:
                self.samples.popleft()

    def eta(self):
        with self.lock:
            if len(self.samples) < 2:
                return None
            (t0, d0), (t1, d1) = self.samples[0], self.samples[-1]
            if t1 - t0 < 3 or d1 <= d0:
                return None
            return max(0, (self.total - self.done) / ((d1 - d0) / (t1 - t0)))

    def snapshot(self):
        with self.lock:
            return self.status, self.done, self.total, self.finished, self.ok, self.final


def log_line(root, text):
    try:
        with open(Path(root) / "fetch_models.log", "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + text + "\n")
    except Exception:
        pass


class Fetcher:
    def __init__(self, root, cache=None, manifest=None, state=None, max_attempts=8, delay_scale=1.0):
        self.root = Path(root)
        self.keep_cache = cache is not None
        self.cache = Path(cache) if cache else self.root / "_downloads"
        self.manifest = manifest or MANIFEST
        self.state = state or State()
        self.max_attempts = max_attempts
        self.delay_scale = delay_scale
        self.failed = []
        self.base = 0  # bytes of the earlier files in this run, for the overall bar

    # ----- what is already here -----

    def stamp(self, item):
        return Path(self.root, item["dest"], f".voicetool-{item['sha256'][:12]}")

    def present(self, item):
        dest = Path(self.root, item["dest"])
        if item["kind"] == "zip":
            return (dest / item["marker"]).is_file() and self.stamp(item).is_file()
        if not dest.is_file() or dest.stat().st_size != item["size"]:
            return False
        return sha256_of(dest) == item["sha256"]

    # ----- one item -----

    def _wait(self, seconds, why, attempt):
        end = time.monotonic() + seconds * self.delay_scale
        while True:
            if self.state.cancel.is_set():
                raise Cancelled()
            left = end - time.monotonic()
            if left <= 0:
                return
            shown = round(left / self.delay_scale) if self.delay_scale else 0
            self.state.set(f"{why} Trying again in {shown} seconds (try {attempt + 1} of {self.max_attempts})...")
            time.sleep(min(0.25, left))

    def _stream(self, item, part, label):
        """Fetch the rest of the file into `part`, resuming from what is already there. Returns when the size is complete
        (the caller checks the hash). Raises on any trouble."""
        size = item["size"]
        have = part.stat().st_size if part.exists() else 0
        if have > size:
            part.unlink()
            have = 0
        if have == size:
            return
        headers = {"User-Agent": "VoiceTool-setup", "Accept-Encoding": "identity"}
        if have:
            headers["Range"] = f"bytes={have}-"
        try:
            reply = urllib.request.urlopen(urllib.request.Request(item["url"], headers=headers), timeout=30,
                                           context=ssl_context())
        except urllib.error.HTTPError as e:
            if e.code == 416:  # the server says our partial file doesn't fit: start over and let the hash decide
                part.unlink(missing_ok=True)
                raise ConnectionError("restarting the download")
            raise
        with reply:
            if have and reply.status == 200:  # the server ignored the resume request: take the whole file again
                have = 0
            elif have:
                m = re.match(r"bytes (\d+)-", reply.headers.get("Content-Range", ""))
                if reply.status != 206 or not m or int(m.group(1)) != have:
                    raise ConnectionError("the server answered the resume request oddly")
            with open(part, "ab" if have else "wb") as out:
                while True:
                    if self.state.cancel.is_set():
                        raise Cancelled()
                    block = reply.read(CHUNK)
                    if not block:
                        break
                    out.write(block)
                    have += len(block)
                    self.state.set_done(self.base + have)
                    self.state.set(label(have))
        if have != size:
            raise ConnectionError(f"the connection closed after {have} of {size} bytes")

    def download(self, item):
        """Make sure the verified file for this item is in the cache folder. Returns its path."""
        self.cache.mkdir(parents=True, exist_ok=True)
        target = self.cache / item["name"]
        part = target.with_name(target.name + ".part")
        size = item["size"]

        def label(have):
            if size >= 1e8:
                return f"Downloading {item['label']}, {have / 1e9:.1f} of {size / 1e9:.1f} GB..."
            return f"Downloading {item['label']}, {have / 1e6:.0f} of {size / 1e6:.0f} MB..."

        if target.is_file():
            if target.stat().st_size == size and sha256_of(target) == item["sha256"]:
                self.state.set_done(self.base + size)
                return target
            target.unlink()
        failures = 0
        bad_hashes = 0
        while True:
            if self.state.cancel.is_set():
                raise Cancelled()
            before = part.stat().st_size if part.exists() else 0
            self.state.set_done(self.base + before)
            self.state.set(label(before))
            try:
                self._stream(item, part, label)
            except Cancelled:
                raise
            except urllib.error.HTTPError as e:
                if e.code in (401, 403, 404, 410):
                    raise FetchError(plain_error(e))
                err = e
            except (OSError, http.client.HTTPException, ValueError, EOFError) as e:
                if isinstance(e, OSError) and getattr(e, "errno", None) in (28, 13):  # disk full / not allowed
                    raise FetchError(plain_error(e))
                err = e
            else:
                self.state.set(f"Checking {item['label']}...")
                if sha256_of(part) == item["sha256"]:
                    part.replace(target)
                    return target
                bad_hashes += 1
                part.unlink()
                self.state.set_done(self.base)
                log_line(self.root, f"{item['id']}: checksum didn't match (time {bad_hashes}); downloading again")
                if bad_hashes >= 3:
                    raise FetchError(f"The downloaded file for {item['label']} didn't pass its safety check three times")
                continue
            log_line(self.root, f"{item['id']}: {type(err).__name__}: {err}")
            after = part.stat().st_size if part.exists() else 0
            failures = 1 if after > before else failures + 1  # moving forward means it is not a dead end
            if failures >= self.max_attempts:
                raise FetchError(plain_error(err))
            self._wait(min(2 ** failures, 30), plain_error(err) + ".", failures)

    def install(self, item, cached):
        dest = Path(self.root, item["dest"])
        if item["kind"] == "zip":
            self.state.set(f"Unpacking {item['label']}...")
            dest.mkdir(parents=True, exist_ok=True)
            base = dest.resolve()
            with zipfile.ZipFile(cached) as z:
                for member in z.infolist():
                    out = (dest / member.filename).resolve()
                    if base != out and base not in out.parents:
                        raise FetchError("The download had an unsafe file name in it and was not unpacked")
                z.extractall(dest)
            if not (dest / item["marker"]).is_file():
                raise FetchError(f"{item['label'].capitalize()} unpacked without the file VoiceTool needs")
            self.stamp(item).write_text(item["sha256"], encoding="utf-8")
            if not self.keep_cache:
                Path(cached).unlink()
        else:
            dest.parent.mkdir(parents=True, exist_ok=True)
            tmp = dest.with_name(dest.name + ".new")
            if self.keep_cache:
                shutil.copyfile(cached, tmp)
            else:
                shutil.move(str(cached), tmp)
            os.replace(tmp, dest)

    # ----- everything -----

    def run(self):
        """Download and install whatever is missing. Returns True when everything is here."""
        s = self.state
        s.set("Checking what is already on this computer...")
        todo = [item for item in self.manifest if not self.present(item)]
        s.total = sum(i["size"] for i in todo)
        if not todo:
            s.set("Everything is already here.")
            return True
        self.check_space(todo)
        self.base = 0
        for item in todo:
            try:
                cached = self.download(item)
                self.install(item, cached)
                self.base += item["size"]
                s.set_done(self.base)
            except Cancelled:
                raise
            except FetchError as e:
                self.failed.append((item, str(e)))
            except zipfile.BadZipFile:
                self.failed.append((item, "A downloaded file was damaged"))
            except OSError as e:
                self.failed.append((item, plain_error(e)))
            if self.failed:
                s.set(self.failed[0][1])
                log_line(self.root, f"{item['id']}: gave up: {self.failed[0][1]}")
                break  # whatever stopped this one (no internet, no disk space) would stop the next one too
        if not self.keep_cache:
            try:
                self.cache.rmdir()
            except OSError:
                pass
        return not self.failed and all(self.present(i) for i in self.manifest)

    def check_space(self, todo):
        need = sum(i["size"] for i in todo) * (2 if self.keep_cache else 1) + 400_000_000
        try:
            probe = self.root if self.root.exists() else self.root.parent
            free = shutil.disk_usage(probe).free
        except OSError:
            return
        if free < need:
            raise FetchError(f"There isn't enough free space on this drive. VoiceTool needs about {need / 1e9:.1f} GB "
                             f"free and only {free / 1e9:.1f} GB is available")


# ----- the computer's graphics card, and config.json -----

def parse_devices(text):
    """[(index, name, MiB)] for the real graphics cards in `llama-server --list-devices` output."""
    found = []
    for m in re.finditer(r"^\s*Vulkan(\d+):\s*(.+)$", text, re.M):
        name = m.group(2)
        mib = re.search(r"\((\d+)\s*MiB", name)
        if any(w in name.lower() for w in SOFTWARE_GPU_WORDS):
            continue
        found.append((int(m.group(1)), name.strip(), int(mib.group(1)) if mib else 0))
    return found


def detect_gpu(server):
    """(device index, name) of the first usable graphics card, or None. Never raises."""
    try:
        out = subprocess.run([str(server), "--list-devices"], capture_output=True, text=True, timeout=90,
                             cwd=str(Path(server).parent), creationflags=CREATE_NO_WINDOW if os.name == "nt" else 0)
        found = [d for d in parse_devices((out.stdout or "") + "\n" + (out.stderr or "")) if d[2] >= MIN_GPU_MIB]
        if found:
            # llama.cpp lists a separate graphics card ahead of the one built into the processor (which reports the
            # computer's shared memory as its own, so "most memory" would pick the wrong one). Take the first that fits.
            first = min(found, key=lambda d: d[0])
            return first[0], first[1]
    except Exception:
        pass
    return None


def configure(root, defaults=None, detect=True):
    """Write this computer's settings into config.json without touching anything the user chose. The defaults file (the
    shipped config.json) supplies any setting the installed one doesn't have yet. Returns the backend written, or None."""
    root = Path(root)
    path = root / "config.json"
    config = {}
    if defaults and Path(defaults).is_file():
        config.update(json.loads(Path(defaults).read_text(encoding="utf-8-sig")))
    if path.is_file():
        try:
            config.update(json.loads(path.read_text(encoding="utf-8-sig")))  # (Notepad and PowerShell add a BOM)
        except ValueError:
            # Not readable as JSON. Keep the user's file next to the new one rather than losing their settings.
            try:
                shutil.copyfile(path, path.with_name("config.json.unreadable-" + time.strftime("%Y%m%d-%H%M%S")))
            except OSError:
                pass
    config.update(MACHINE_KEYS)
    backend = config.get("offline_backend")
    device = 0
    vulkan = root / MACHINE_KEYS["offline_server"]
    if detect and vulkan.is_file():
        gpu = detect_gpu(vulkan)
        backend = "vulkan" if gpu else "cpu"
        device = gpu[0] if gpu else 0
    elif backend not in ("vulkan", "cpu"):
        backend = None
    if backend:
        config["offline_backend"] = backend
    else:
        config.pop("offline_backend", None)
    config["offline_args"] = BASE_ARGS + ["-ngl", "99", "--device", f"Vulkan{device}"]
    tmp = path.with_name("config.json.new")
    tmp.write_text(json.dumps(config, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(tmp, path)
    return backend


# ----- the screens -----

def _work(fetcher):
    st = fetcher.state
    ok = False
    try:
        ok = fetcher.run()
        if ok:
            final = "All set."
        else:
            reason = fetcher.failed[0][1] if fetcher.failed else "Something is still missing"
            final = (f"Couldn't finish: {reason}. Your dictation still works. "
                     "Open VoiceTool and click Finish setup to try again.")
    except Cancelled:
        final = "Stopped. Open VoiceTool and click Finish setup to pick up where it left off."
    except FetchError as e:
        final = f"Couldn't finish: {e}. Your dictation still works. Open VoiceTool and click Finish setup to try again."
    except Exception as e:  # never leave the window hanging
        log_line(fetcher.root, f"unexpected: {type(e).__name__}: {e}")
        final = (f"Couldn't finish: {plain_error(e)}. Your dictation still works. "
                 "Open VoiceTool and click Finish setup to try again.")
    try:
        configure(fetcher.root)
    except Exception as e:
        log_line(fetcher.root, f"configure failed: {e}")
    log_line(fetcher.root, final)
    with st.lock:
        st.ok, st.final, st.finished = ok, final, True
        st.status = final


def run_console(fetcher):
    st = fetcher.state
    worker = threading.Thread(target=_work, args=(fetcher,), daemon=True)
    worker.start()
    tty = bool(sys.stdout and sys.stdout.isatty())
    last = ""
    while worker.is_alive():
        status, done, total, _, _, _ = st.snapshot()
        pct = f"{100 * done / total:3.0f}%" if total else "  --"
        line = f"  {pct}  {status}  {fmt_eta(st.eta()) if total else ''}"
        if tty:
            say("\r" + line[:118].ljust(118))
        elif line != last:
            say(line + "\n")
            last = line
        try:
            worker.join(1.0 if tty else 5.0)
        except KeyboardInterrupt:
            st.cancel.set()
    status, _, _, _, ok, final = st.snapshot()
    say(("\r" if tty else "") + (final or status).ljust(118 if tty else 0) + "\n")
    return 0 if ok else 1


def run_window(fetcher):
    try:
        import tkinter as tk
        from tkinter import ttk
        root = tk.Tk()
    except Exception:
        say("(No window available; showing progress here instead.)\n")
        return run_console(fetcher)
    hidden = os.environ.get("VOICETOOL_FETCH_HIDDEN") == "1"  # tests: never shown on screen
    if hidden:
        root.withdraw()
    st = fetcher.state
    root.title("VoiceTool setup")
    root.resizable(False, False)
    frame = ttk.Frame(root, padding=22)
    frame.grid()
    ttk.Label(frame, text="Getting VoiceTool ready", font=("Segoe UI", 14, "bold")).grid(sticky="w")
    status = ttk.Label(frame, text="", wraplength=470, justify="left", font=("Segoe UI", 10))
    status.grid(sticky="w", pady=(12, 8))
    bar = ttk.Progressbar(frame, length=470, mode="determinate", maximum=1000)
    bar.grid(sticky="we")
    detail = ttk.Label(frame, text="", font=("Segoe UI", 10))
    detail.grid(sticky="w", pady=(8, 12))
    note = ttk.Label(frame, text=("Please keep this window open. This can take 5 to 30 minutes, depending on your "
                                  "internet. If you close it, open VoiceTool later and click Finish setup to continue "
                                  "where it left off."), wraplength=470, justify="left", foreground="#555555")
    note.grid(sticky="w")
    button = ttk.Button(frame, text="Close", command=root.destroy)
    button.grid(sticky="e", pady=(14, 0))
    button.state(["disabled"])
    result = {"code": 1}

    def on_close():
        if not st.snapshot()[3]:
            st.cancel.set()  # stopped on purpose; the partial file is kept so it can resume
        root.destroy()

    root.protocol("WM_DELETE_WINDOW", on_close)

    def tick():
        s, done, total, finished, ok, final = st.snapshot()
        status.config(text=s)
        bar["value"] = 1000 * done / total if total else 0
        if total and not finished:
            detail.config(text=f"{fmt_size(done)} of {fmt_size(total)}   -   {fmt_eta(st.eta())}")
        if finished:
            result["code"] = 0 if ok else 1
            detail.config(text="")
            if ok:
                bar["value"] = 1000
                note.config(text="")
            else:
                note.config(text="You can close this window.")
            if ok or hidden:
                root.after(1200 if not hidden else 50, root.destroy)
                return
            button.state(["!disabled"])
            return
        root.after(250, tick)

    threading.Thread(target=_work, args=(fetcher,), daemon=True).start()
    tick()
    root.mainloop()
    return result["code"]


def main(argv=None):
    ap = argparse.ArgumentParser(description="Downloads and checks the models VoiceTool needs.")
    ap.add_argument("--window", action="store_true", help="show a progress window")
    ap.add_argument("--check", action="store_true", help="only check; download nothing")
    ap.add_argument("--configure", action="store_true", help="only write this computer's settings into config.json")
    ap.add_argument("--defaults", help="the shipped config.json, for settings the installed one lacks")
    ap.add_argument("--root", default=str(HERE))
    ap.add_argument("--cache", help="keep downloaded files here")
    ap.add_argument("--manifest", help="a JSON list of files, instead of the built-in one")
    args = ap.parse_args(argv)
    root = Path(args.root)
    if args.configure:
        say(f"backend: {configure(root, args.defaults, detect=(root / MACHINE_KEYS['offline_server']).is_file())}\n")
        return 0
    manifest = json.loads(Path(args.manifest).read_text(encoding="utf-8")) if args.manifest else None
    scale = float(os.environ.get("VOICETOOL_FETCH_DELAY_SCALE", "1"))
    fetcher = Fetcher(root, cache=args.cache, manifest=manifest, delay_scale=scale)
    if args.check:
        missing = [i for i in fetcher.manifest if not fetcher.present(i)]
        for i in missing:
            say(f"missing: {i['id']}\n")
        return 0 if not missing else 1
    root.mkdir(parents=True, exist_ok=True)
    lock = None
    try:
        import msvcrt
        lock = open(root / ".fetch_models.lock", "a+")
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
    except ImportError:
        pass
    except OSError:
        say("VoiceTool is already downloading in another window.\n")
        return 2
    try:
        return run_window(fetcher) if args.window else run_console(fetcher)
    finally:
        if lock:
            lock.close()


if __name__ == "__main__":
    sys.exit(main())
