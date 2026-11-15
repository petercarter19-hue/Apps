

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
TITLE = "Chirpy"
APP_ID = "VoiceTool.Light"

def windows_theme():

    chosen = os.environ.get("VOICETOOL_THEME", "")
    if chosen in ("light", "dark"):
        return chosen
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as key:
            return "light" if winreg.QueryValueEx(key, "AppsUseLightTheme")[0] == 1 else "dark"
    except OSError:
        return "dark"

THEME = windows_theme()
BACKGROUND = "#f4f6f6" if THEME == "light" else "#111416"

def window_python():

    exe = Path(sys.executable)
    if exe.name.lower() == "pythonw.exe" and exe.with_name("python.exe").exists():
        return str(exe.with_name("python.exe"))
    return str(exe)

class AppWindow:
    START_TIMEOUT = 25

    def __init__(self, api, log, on_failed=None, python=None, script=None, test_mode=False):
        self.api, self.log, self.on_failed = api, log, on_failed
        self.python = python or window_python()
        self.script = script or str(Path(__file__).resolve())
        self.test_mode = test_mode
        self.proc = None
        self.ready = False
        self.failed = False
        self.child_pid = self.child_hwnd = 0
        self.child_editing = self.child_corners = False
        self._lock = threading.RLock()
        self._write_lock = threading.Lock()
        self._stderr = collections.deque(maxlen=40)
        self._evals = {}
        self._eval_id = 0
        self._folders = {}

    def is_open(self):
        return self.proc is not None and self.proc.poll() is None

    def show(self, page="home"):

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
            self._closing = proc
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

    def set_theme(self, theme):

        self._send({"cmd": "theme", "theme": theme})

    def _dispatch(self, proc, msg):

        if not str(msg.get("name", "")).startswith("set_"):
            threading.Thread(target=self._answer, args=(proc, msg), daemon=True).start()
            return
        with self._lock:
            if getattr(self, "_settings_q", None) is None:
                import queue
                self._settings_q = queue.Queue()
                threading.Thread(target=self._settings_worker, daemon=True, name="settings requests").start()
        self._settings_q.put((proc, msg))

    def _settings_worker(self):
        while True:
            proc, msg = self._settings_q.get()
            try:
                self._answer(proc, msg)
            finally:
                self._settings_q.task_done()

    def evaluate(self, js, timeout=10):

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

        with self._lock:
            self._eval_id += 1
            n = self._eval_id
            done = self._folders[n] = {"event": threading.Event(), "path": ""}
        if not self._send({"cmd": "folder", "id": n, "start": str(start or "")}):
            return ""
        done["event"].wait(timeout)
        self._folders.pop(n, None)
        return done["path"]

    @staticmethod
    def _allow_foreground():
        try:
            ctypes.windll.user32.AllowSetForegroundWindow(-1)
        except Exception:
            pass

    def _spawn(self, page):
        env = dict(os.environ, PYTHONIOENCODING="utf-8", PYTHONUTF8="1")
        chosen = (getattr(self.api, "config", None) or {}).get("theme")
        if chosen in ("light", "dark"):
            env["VOICETOOL_THEME"] = chosen
        if self.test_mode:
            env["VOICETOOL_TEST_WINDOW"] = "hidden" if self.test_mode == "hidden" else "1"
        self.ready, self.failed = False, False
        self._stderr.clear()
        self.proc = subprocess.Popen([self.python, "-s", "-E", self.script, "--child", "--page", page],
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
                self._stderr.append(line.rstrip())
                continue
            if "name" in msg and "id" in msg:
                self._dispatch(proc, msg)
            elif msg.get("event") == "ready":
                self.child_pid, self.child_hwnd = msg.get("pid") or 0, msg.get("hwnd") or 0
                self.child_editing, self.child_corners = bool(msg.get("editing")), bool(msg.get("corners"))
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

        deadline = time.monotonic() + self.START_TIMEOUT
        while proc.poll() is None and not self.ready and time.monotonic() < deadline:
            time.sleep(0.1)
        if self.ready:
            proc.wait()
            self.log.info("app window closed")
            return
        if getattr(self, "_closing", None) is proc:
            self.log.info("app window closed before it finished opening")
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

def build_page(start_page="home", ui_dir=UI):

    html = (Path(ui_dir) / "index.html").read_text(encoding="utf-8")
    try:
        extra = (Path(ui_dir) / "pc_overrides.js").read_text(encoding="utf-8")
    except FileNotFoundError:
        extra = ""
    start = "<script>try{history.replaceState(null,'','#" + "".join(c for c in start_page if c.isalnum()) + "')}catch(e){}</script>\n"
    tail = start + "<script>\n" + extra + "\n</script>\n"
    html = html.replace('<html lang="en">', f'<html lang="en" data-theme="{THEME}">', 1)
    return html.replace("</body>", tail + "</body>") if "</body>" in html else html + tail

EDIT_MENU = ("undo", "redo", "cut", "copy", "paste", "selectAll")

def edit_menu_items(items):

    keep, separator = [], None
    for i, (name, is_separator) in enumerate(items):
        if is_separator:
            if keep:
                separator = i
        elif name in EDIT_MENU:
            if separator is not None:
                keep.append(separator)
                separator = None
            keep.append(i)
    return keep

HT_LEFT, HT_RIGHT, HT_TOP, HT_TOPLEFT, HT_TOPRIGHT, HT_BOTTOM, HT_BOTTOMLEFT, HT_BOTTOMRIGHT = 10, 11, 12, 13, 14, 15, 16, 17
CORNER = 24

def corner_hit(hit, x, y, rect, dpi=96):

    if hit not in (HT_LEFT, HT_RIGHT, HT_TOP, HT_BOTTOM):
        return hit
    left, top, right, bottom = rect
    reach = CORNER * dpi // 96
    if hit in (HT_LEFT, HT_RIGHT):
        if y - top < reach:
            return HT_TOPLEFT if hit == HT_LEFT else HT_TOPRIGHT
        if bottom - y <= reach:
            return HT_BOTTOMLEFT if hit == HT_LEFT else HT_BOTTOMRIGHT
    else:
        if x - left < reach:
            return HT_TOPLEFT if hit == HT_TOP else HT_BOTTOMLEFT
        if right - x <= reach:
            return HT_TOPRIGHT if hit == HT_TOP else HT_BOTTOMRIGHT
    return hit

def add_corner_resize(hwnd, keep):

    from ctypes import wintypes
    user32, comctl32 = ctypes.windll.user32, ctypes.windll.comctl32
    LRESULT = ctypes.c_ssize_t
    SUBCLASSPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM,
                                      ctypes.c_size_t, ctypes.c_size_t)
    comctl32.DefSubclassProc.restype = LRESULT
    comctl32.DefSubclassProc.argtypes = (wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
    comctl32.SetWindowSubclass.argtypes = (wintypes.HWND, SUBCLASSPROC, ctypes.c_size_t, ctypes.c_size_t)
    user32.GetDpiForWindow.argtypes = (wintypes.HWND,)

    def proc(window, msg, wparam, lparam, uid, data):
        result = comctl32.DefSubclassProc(window, msg, wparam, lparam)
        if msg == 0x0084:
            try:
                x, y = ctypes.c_short(lparam & 0xFFFF).value, ctypes.c_short((lparam >> 16) & 0xFFFF).value
                r = wintypes.RECT()
                user32.GetWindowRect(window, ctypes.byref(r))
                result = corner_hit(result, x, y, (r.left, r.top, r.right, r.bottom), user32.GetDpiForWindow(window) or 96)
            except Exception:
                pass
        return result

    keep.append(SUBCLASSPROC(proc))
    return bool(comctl32.SetWindowSubclass(hwnd, keep[-1], 1, 0))

def redraw_page_control(control):

    try:
        control.Visible = False
    finally:
        control.Visible = True
        control.Focus()
        control.Invalidate()
        control.Update()

def restore_minimized(hwnd, user32, refresh_page=None, wait=time.sleep):

    steps = []
    if not hwnd or not user32.IsIconic(hwnd):
        return steps
    result = ctypes.c_size_t(0)
    try:
        sent = user32.SendMessageTimeoutW(hwnd, 0x0112, 0xF120, 0, 0x0002, 2000, ctypes.byref(result))
    except Exception:
        sent = 0
    if sent:
        steps.append("restored like the taskbar (SC_RESTORE, the window's own thread)")
    else:
        user32.ShowWindow(hwnd, 9)
        steps.append("restored by ShowWindow")
    for _ in range(40):
        if not user32.IsIconic(hwnd):
            break
        wait(0.05)
    else:
        user32.ShowWindow(hwnd, 9)
        steps.append("still minimized: restored by ShowWindow")
    if refresh_page is not None:
        try:
            refresh_page()
            steps.append("page told it is visible again")
        except Exception as error:
            steps.append(f"page refresh failed: {error}")
    return steps

def child_main(argv):
    page = argv[argv.index("--page") + 1] if "--page" in argv else "home"
    proto = sys.stdout
    sys.stdout = sys.stderr
    lock = threading.Lock()

    def send(message):
        with lock:
            proto.write(json.dumps(message, default=str) + "\n")
            proto.flush()

    try:
        import webview
    except Exception as e:
        send({"event": "error", "message": f"pywebview is not available: {e}"})
        return 2
    try:
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID(APP_ID)
    except Exception:
        pass

    state = {"page": page, "loaded": False, "window": None}
    pending, counter = {}, [0]
    test_mode = os.environ.get("VOICETOOL_TEST_WINDOW") in ("1", "hidden")
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

        hwnd = native_handle()
        if not hwnd:
            return
        try:
            dark = ctypes.c_int(0 if THEME == "light" else 1)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 20, ctypes.byref(dark), 4)
            rgb = int(BACKGROUND[1:], 16)
            caption = ctypes.c_int(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | (rgb >> 16))
            ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 35, ctypes.byref(caption), 4)
        except Exception:
            pass

    def set_theme(theme):
        global THEME, BACKGROUND
        if theme in ("light", "dark"):
            THEME = theme
            BACKGROUND = "#f4f6f6" if theme == "light" else "#111416"
            style_window()

    def refresh_page():

        control = state.get("webview")
        if control is None:
            return
        from System import Func, Type
        state["window"].native.Invoke(Func[Type](lambda: redraw_page_control(control)))

    def restore_window():
        for step in restore_minimized(native_handle(), ctypes.windll.user32, refresh_page):
            sys.stderr.write(f"raise: {step}\n")

    def raise_window():

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
            restore_window()
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
                flags = 0x0001 | 0x0002 | 0x0010 | 0x0040
                user32.SetWindowPos(hwnd, -1, 0, 0, 0, 0, flags)
                user32.SetWindowPos(hwnd, -2, 0, 0, 0, 0, flags)
        except Exception as e:
            sys.stderr.write(f"could not raise the window: {e}\n")

    def go(page_name):
        if state["loaded"]:
            if not hidden:
                try:
                    restore_window()
                except Exception as e:
                    sys.stderr.write(f"restore failed: {e}\n")
            state["window"].evaluate_js(f"window.showPage && window.showPage({json.dumps(page_name)})")
        else:
            state["page"] = page_name
        raise_window()

    def pick_folder(msg):

        path = ""
        try:
            if os.environ.get("VOICETOOL_TEST_FOLDER"):
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
            elif msg.get("cmd") == "theme":
                try:
                    set_theme(str(msg.get("theme")))
                except Exception as e:
                    sys.stderr.write(f"theme change failed: {e}\n")
            elif msg.get("cmd") == "close":
                break
            elif msg.get("cmd") == "eval" and test_mode:
                try:
                    result = state["window"].evaluate_js(msg["js"])
                except Exception as e:
                    result = f"ERROR: {e}"
                send({"event": "eval", "id": msg.get("id"), "result": result})
        for waiter in list(pending.values()):
            waiter["event"].set()
        try:
            state["window"].destroy()
        except Exception:
            pass

    def on_loaded():
        state["loaded"] = True
        style_window()
        for _ in range(100):
            try:
                if state["window"].evaluate_js("(window.pywebview && window.pywebview.api && "
                                               "typeof window.pywebview.api.call === 'function') ? 'ok' : 'no'") == "ok":
                    if state["page"] != page:
                        go(state["page"])
                    else:
                        raise_window()
                    send({"event": "ready", "pid": os.getpid(), "hwnd": native_handle(), "editing": state["editing"],
                          "corners": state["corners"]})
                    return
            except Exception:
                pass
            time.sleep(0.1)
        send({"event": "error", "message": "the page loaded but its link to VoiceTool did not start"})

    def tune_webview():

        try:
            from webview.platforms import edgechromium
        except Exception as e:
            sys.stderr.write(f"WebView2 settings not changed: {e}\n")
            return
        original = edgechromium.EdgeChrome.on_webview_ready

        def trim_menu(sender, args):
            try:
                items = list(args.MenuItems)
                keep = set(edit_menu_items([(str(i.Name), str(i.Kind) == "Separator") for i in items]))
                for n in range(len(items) - 1, -1, -1):
                    if n not in keep:
                        args.MenuItems.RemoveAt(n)
                if not keep:
                    args.Handled = True
            except Exception as e:
                sys.stderr.write(f"right-click menu not trimmed: {e}\n")

        def on_webview_ready(self, sender, args):
            original(self, sender, args)
            try:
                state["webview"] = sender
                core = sender.CoreWebView2
                core.Settings.AreDefaultContextMenusEnabled = True
                core.Settings.AreBrowserAcceleratorKeysEnabled = True
                core.ContextMenuRequested += trim_menu
                state["editing"] = True
            except Exception as e:
                sys.stderr.write(f"WebView2 settings not changed: {e}\n")
            try:
                state["corners"] = add_corner_resize(int(self.form.Handle.ToInt64()), state["keep"])
            except Exception as e:
                sys.stderr.write(f"corner resize not added: {e}\n")

        edgechromium.EdgeChrome.on_webview_ready = on_webview_ready

    state.update(editing=False, corners=False, keep=[])
    tune_webview()
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
