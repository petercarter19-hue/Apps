"""The VoiceTool app window on Windows: ui/index.html (the same page the Mac shows) in Edge WebView2, through pywebview.

Why a second process: pywebview wants the main thread, and VoiceTool's main thread already belongs to the tkinter loop
that draws the pill and cards. So the window runs in its own small process, started by the tool:

    VoiceTool (keys, tray, pill, ui_api.API)  <-- JSON lines over the child's stdin/stdout -->  this file, --child

The channel is the child's own stdin/stdout pipes. No port is opened, no named pipe or file is shared, and only the
two processes at either end of those pipes can use it, so nothing else on this PC or network can reach VoiceTool's
settings through it. The page asks `window.pywebview.api.call(name, args)`; the child forwards that line to the tool,
the tool runs `API.call(name, args)` (on a thread, so a slow answer never freezes the window) and sends the answer back.
The window closes by itself when the tool goes away (its stdin ends).

Parent side: AppWindow (used by voicetool.py). Child side: child_main() (run as `python app_window_win.py --child`).
If the window can't start (WebView2 missing, pywebview broken, no answer in 25 s) AppWindow calls on_failed(reason) so the
tool can open the old settings window instead.
"""
import collections
import ctypes
import json
import os
import subprocess
import sys
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
UI = HERE / "ui"
CREATE_NO_WINDOW = 0x08000000
TITLE = "VoiceTool"
APP_ID = "VoiceTool.App"
BACKGROUND = "#121216"


def window_python():
    """python.exe next to the running interpreter (pythonw.exe has no usable pipes)."""
    exe = Path(sys.executable)
    if exe.name.lower() == "pythonw.exe" and exe.with_name("python.exe").exists():
        return str(exe.with_name("python.exe"))
    return str(exe)


# ======================================================================================================== the tool

class AppWindow:
    START_TIMEOUT = 25  # seconds to wait for the window to be ready before giving up on it

    def __init__(self, api, log, on_failed=None, python=None, script=None, test_mode=False):
        self.api, self.log, self.on_failed = api, log, on_failed
        self.python = python or window_python()
        self.script = script or str(Path(__file__).resolve())
        self.test_mode = test_mode
        self.proc = None
        self.ready = False
        self.failed = False
        self.child_pid = self.child_hwnd = 0  # the window's own process and window handle, once it is ready
        self._lock = threading.RLock()
        self._write_lock = threading.Lock()
        self._stderr = collections.deque(maxlen=40)
        self._evals = {}
        self._eval_id = 0
        self._folders = {}

    # ----- what the tool does -----

    def is_open(self):
        return self.proc is not None and self.proc.poll() is None

    def show(self, page="home"):
        """Open the window at `page`, or bring it forward (and switch page) if it's already open. Never blocks."""
        with self._lock:
            if self.is_open():
                self._allow_foreground()
                self.log.info("app window: showing %s", page)
                if self._send({"cmd": "show", "page": page}):
                    return
                self._stop_process()
            self._spawn(page)

    def close(self):
        with self._lock:
            proc = self.proc
            if proc is None:
                return
            self._send({"cmd": "close"})
            try:
                proc.stdin.close()
            except Exception:
                pass
            try:
                proc.wait(timeout=3)
            except Exception:
                self._kill(proc)
            self.proc = None

    def evaluate(self, js, timeout=10):
        """Test hook (only in test mode): run JavaScript in the page and return its result."""
        if not self.test_mode:
            raise RuntimeError("only available in test mode")
        with self._lock:
            self._eval_id += 1
            n = self._eval_id
            done = self._evals[n] = {"event": threading.Event(), "result": None}
        if not self._send({"cmd": "eval", "id": n, "js": js}):
            raise RuntimeError("the window isn't open")
        if not done["event"].wait(timeout):
            raise TimeoutError("the page didn't answer")
        return done["result"]

    def choose_folder(self, start="", timeout=600):
        """Ask the window to show its folder-picker dialog and wait for the answer: the chosen path, or '' if the dialog
        was canceled (or the window has gone). Called from a request thread, never the main one."""
        with self._lock:
            self._eval_id += 1
            n = self._eval_id
            done = self._folders[n] = {"event": threading.Event(), "path": ""}
        if not self._send({"cmd": "folder", "id": n, "start": str(start or "")}):
            return ""
        done["event"].wait(timeout)
        self._folders.pop(n, None)
        return done["path"]

    # ----- the process -----

    @staticmethod
    def _allow_foreground():
        try:  # we have just been clicked (tray, Start menu), so the window we open may come to the front (ASFW_ANY)
            ctypes.windll.user32.AllowSetForegroundWindow(-1)
        except Exception:
            pass

    def _spawn(self, page):
        env = dict(os.environ, PYTHONIOENCODING="utf-8", PYTHONUTF8="1")
        if self.test_mode:
            env["VOICETOOL_TEST_WINDOW"] = "hidden" if self.test_mode == "hidden" else "1"
        self.ready, self.failed = False, False
        self._stderr.clear()
        self.proc = subprocess.Popen([self.python, self.script, "--child", "--page", page],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                     encoding="utf-8", env=env, cwd=str(HERE), creationflags=CREATE_NO_WINDOW)
        proc = self.proc
        self._allow_foreground()
        self.log.info("app window starting (pid %s, page %s)", proc.pid, page)
        threading.Thread(target=self._read_stdout, args=(proc,), daemon=True).start()
        threading.Thread(target=self._read_stderr, args=(proc,), daemon=True).start()
        threading.Thread(target=self._watch, args=(proc,), daemon=True).start()

    @staticmethod
    def _kill(proc):
        """End the window's process AND its child: inside a virtual environment python.exe is a small launcher that
        starts the real interpreter, so killing only the launcher would leave the window running."""
        try:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True, timeout=10,
                           creationflags=CREATE_NO_WINDOW)
        except Exception:
            pass
        try:
            proc.kill()
        except Exception:
            pass

    def _stop_process(self):
        proc, self.proc = self.proc, None
        if proc is not None:
            self._kill(proc)

    def _send(self, message, proc=None):
        """One JSON line to the window. False if it can't be delivered (the window is gone)."""
        proc = proc or self.proc
        if proc is None:
            return False
        try:
            line = json.dumps(message, default=str) + "\n"
            with self._write_lock:
                proc.stdin.write(line)
                proc.stdin.flush()
            return True
        except Exception:
            return False

    def _read_stderr(self, proc):
        for line in proc.stderr:
            self._stderr.append(line.rstrip())

    def _read_stdout(self, proc):
        for line in proc.stdout:
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                self._stderr.append(line.rstrip())  # a stray print from a library, not part of the conversation
                continue
            if "name" in msg and "id" in msg:
                threading.Thread(target=self._answer, args=(proc, msg), daemon=True).start()
            elif msg.get("event") == "ready":
                self.child_pid, self.child_hwnd = msg.get("pid") or 0, msg.get("hwnd") or 0
                self.ready = True
                self.log.info("app window ready")
            elif msg.get("event") == "error":
                self._stderr.append(str(msg.get("message")))
            elif msg.get("event") == "folder":
                waiting = self._folders.get(msg.get("id"))
                if waiting:
                    waiting["path"] = msg.get("path") or ""
                    waiting["event"].set()
            elif msg.get("event") == "eval":
                waiting = self._evals.pop(msg.get("id"), None)
                if waiting:
                    waiting["result"] = msg.get("result")
                    waiting["event"].set()

    def _answer(self, proc, req):
        """The page asked for something: do it with ui_api.API and send the answer back."""
        started = time.perf_counter()
        try:
            data, ok = self.api.call(req["name"], req.get("args") or {}), True
        except Exception as e:
            self.log.warning("app window request %s failed: %s", req.get("name"), e)
            data, ok = str(e)[:300], False
        self._send({"id": req["id"], "ok": ok, "data": data}, proc)
        if time.perf_counter() - started > 2:
            self.log.info("app window request %s took %.1fs", req.get("name"), time.perf_counter() - started)

    def _watch(self, proc):
        """Give the window START_TIMEOUT seconds to be ready. If it dies or hangs first, report it once."""
        deadline = time.monotonic() + self.START_TIMEOUT
        while proc.poll() is None and not self.ready and time.monotonic() < deadline:
            time.sleep(0.1)
        if self.ready:
            proc.wait()
            self.log.info("app window closed")
            return
        reason = " | ".join(list(self._stderr)[-6:]) or ("it took too long to start" if proc.poll() is None
                                                          else f"it stopped at once (exit code {proc.poll()})")
        if proc.poll() is None:
            self._kill(proc)
        if self.proc is proc:
            self.proc = None
        self.failed = True
        self.log.error("app window failed to start: %s", reason[:600])
        if self.on_failed:
            try:
                self.on_failed(reason)
            except Exception:
                self.log.exception("app window fallback failed")


# ======================================================================================================== the window

def build_page(start_page="home", ui_dir=UI):
    """ui/index.html with the PC additions (ui/pc_overrides.js) added to the end, opening at `start_page`."""
    html = (Path(ui_dir) / "index.html").read_text(encoding="utf-8")
    try:
        extra = (Path(ui_dir) / "pc_overrides.js").read_text(encoding="utf-8")
    except FileNotFoundError:
        extra = ""
    start = "<script>try{history.replaceState(null,'','#" + "".join(c for c in start_page if c.isalnum()) + "')}catch(e){}</script>\n"
    tail = start + "<script>\n" + extra + "\n</script>\n"
    return html.replace("</body>", tail + "</body>") if "</body>" in html else html + tail


def child_main(argv):
    page = argv[argv.index("--page") + 1] if "--page" in argv else "home"
    proto = sys.stdout            # the conversation with the tool; anything else printed goes to stderr
    sys.stdout = sys.stderr
    lock = threading.Lock()

    def send(message):
        with lock:
            proto.write(json.dumps(message, default=str) + "\n")
            proto.flush()

    try:
        import webview
    except Exception as e:  # pywebview isn't installed
        send({"event": "error", "message": f"pywebview is not available: {e}"})
        return 2
    try:
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(APP_ID)  # its own taskbar button and icon
    except Exception:
        pass

    state = {"page": page, "loaded": False, "window": None}
    pending, counter = {}, [0]
    test_mode = os.environ.get("VOICETOOL_TEST_WINDOW") in ("1", "hidden")  # tests only: lets them run JavaScript in the page
    hidden = os.environ.get("VOICETOOL_TEST_WINDOW") == "hidden"

    class Bridge:
        def call(self, name, args=None):
            counter[0] += 1
            n = counter[0]
            waiter = pending[n] = {"event": threading.Event(), "reply": None}
            send({"id": n, "name": name, "args": args or {}})
            waiter["event"].wait()
            pending.pop(n, None)
            reply = waiter["reply"]
            if reply is None:
                raise RuntimeError("VoiceTool isn't answering")
            if not reply.get("ok"):
                raise ValueError(reply.get("data"))
            return reply.get("data")

    def native_handle():
        try:
            return int(state["window"].native.Handle.ToInt64())
        except Exception:
            return 0

    def style_window():
        """Dark title bar on Windows 10/11, so the window doesn't have a white strip across the top."""
        hwnd = native_handle()
        if not hwnd:
            return
        try:
            dark = ctypes.c_int(1)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(dark), 4)  # immersive dark mode
            rgb = int(BACKGROUND[1:], 16)
            caption = ctypes.c_int(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | (rgb >> 16))  # BGR
            ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 35, ctypes.byref(caption), 4)  # caption color (Windows 11)
        except Exception:
            pass

    def raise_window():
        """Bring the window to the front. Windows refuses this to a program that didn't just get a click, so there are
        three tries: ask politely (the tool allowed it when it was clicked), borrow the front window's input for a
        moment, and finally flick "always on top" so the window is at least in front (and its taskbar button flashes)."""
        if hidden:
            return
        window = state["window"]
        hwnd = native_handle()
        if not hwnd:
            try:
                window.restore()
                window.on_top = True
                window.on_top = False
            except Exception:
                pass
            return
        user32 = ctypes.windll.user32
        try:
            if user32.IsIconic(hwnd):
                user32.ShowWindow(hwnd, 9)  # restore a minimized window
            user32.SetForegroundWindow(hwnd)
            if user32.GetForegroundWindow() != hwnd:
                front = user32.GetForegroundWindow()
                theirs = user32.GetWindowThreadProcessId(front, None) if front else 0
                mine = ctypes.windll.kernel32.GetCurrentThreadId()
                if theirs and theirs != mine:
                    user32.AttachThreadInput(mine, theirs, True)
                    try:
                        user32.SetForegroundWindow(hwnd)
                    finally:
                        user32.AttachThreadInput(mine, theirs, False)
            if user32.GetForegroundWindow() != hwnd:
                flags = 0x0001 | 0x0002 | 0x0010 | 0x0040  # no move, no resize, no activate, show
                user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, flags)  # topmost...
                user32.SetWindowPos(hwnd, -2, 0, 0, 0, 0, flags)  # ...and straight back to normal: now in front of the rest
        except Exception as e:
            sys.stderr.write(f"could not raise the window: {e}\n")

    def go(page_name):
        if state["loaded"]:
            state["window"].evaluate_js(f"window.showPage && window.showPage({json.dumps(page_name)})")
        else:
            state["page"] = page_name
        raise_window()

    def pick_folder(msg):
        """The window's own folder dialog (it sits on top of this window, not behind it)."""
        path = ""
        try:
            if os.environ.get("VOICETOOL_TEST_FOLDER"):  # tests: answer without showing a dialog
                path = os.environ["VOICETOOL_TEST_FOLDER"]
            else:
                kind = webview.FileDialog.FOLDER if hasattr(webview, "FileDialog") else webview.FOLDER_DIALOG
                chosen = state["window"].create_file_dialog(kind, directory=msg.get("start") or "")
                path = chosen[0] if chosen else ""
        except Exception as e:
            sys.stderr.write(f"folder dialog failed: {e}\n")
        send({"event": "folder", "id": msg.get("id"), "path": path})

    def listen():
        for line in sys.stdin:
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue
            if "ok" in msg and "id" in msg:
                waiter = pending.get(msg["id"])
                if waiter:
                    waiter["reply"] = msg
                    waiter["event"].set()
            elif msg.get("cmd") == "show":
                try:
                    go(str(msg.get("page") or "home"))
                except Exception as e:
                    sys.stderr.write(f"show failed: {e}\n")
            elif msg.get("cmd") == "folder":
                threading.Thread(target=pick_folder, args=(msg,), daemon=True).start()
            elif msg.get("cmd") == "close":
                break
            elif msg.get("cmd") == "eval" and test_mode:
                try:
                    result = state["window"].evaluate_js(msg["js"])
                except Exception as e:
                    result = f"ERROR: {e}"
                send({"event": "eval", "id": msg.get("id"), "result": result})
        for waiter in list(pending.values()):  # the tool is gone: free any request waiting for it
            waiter["event"].set()
        try:
            state["window"].destroy()
        except Exception:
            pass

    def on_loaded():
        state["loaded"] = True
        style_window()
        for _ in range(100):  # tell the tool when the page and its bridge are really there
            try:
                if state["window"].evaluate_js("(window.pywebview && window.pywebview.api && "
                                               "typeof window.pywebview.api.call === 'function') ? 'ok' : 'no'") == "ok":
                    if state["page"] != page:
                        go(state["page"])
                    else:
                        raise_window()
                    send({"event": "ready", "pid": os.getpid(), "hwnd": native_handle()})
                    return
            except Exception:
                pass
            time.sleep(0.1)
        send({"event": "error", "message": "the page loaded but its link to VoiceTool did not start"})

    icon = HERE / "voicetool.ico"
    window = state["window"] = webview.create_window(
        TITLE, html=build_page(page), js_api=Bridge(), width=1060, height=720, min_size=(820, 560),
        background_color=BACKGROUND, text_select=True, hidden=hidden)
    window.events.loaded += on_loaded
    threading.Thread(target=listen, daemon=True).start()
    try:
        webview.start(gui="edgechromium", icon=str(icon) if icon.exists() else None)
    except Exception as e:
        send({"event": "error", "message": f"the window could not start: {e}"})
        return 3
    send({"event": "closed"})
    return 0


if __name__ == "__main__":
    if "--child" in sys.argv:
        sys.exit(child_main(sys.argv))
    print("This file is started by VoiceTool; it isn't run by hand.")
