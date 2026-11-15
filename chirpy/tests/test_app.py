"""Tests that run INSIDE a built Light app folder (test_light_edition.py builds one in a temporary folder, copies this file
in as test_light_inside.py and runs it there). Fakes only: no microphone, no speech model, no keys, no paste, no
registry, no real window on screen. Every file the app writes stays in that temporary folder."""
import json
import os
import sys
import time
from pathlib import Path
from types import SimpleNamespace

import numpy as np
import pytest

HERE = Path(__file__).resolve().parent
os.environ["VOICETOOL_TEST"] = "1"  # (settings_window keeps "start when I sign in" in memory)

import app_window_win  # noqa: E402
import corrections  # noqa: E402
import light_keys  # noqa: E402
import settings_window  # noqa: E402
import ui_api  # noqa: E402
import voicetool as vt  # noqa: E402

ALT, CTRL = 0xA4, 0xA2
PASTED = []


class FakeRecorder:
    device, level = None, 0.0

    def __init__(self):
        self.running, self.starts = False, 0

    def start(self):
        self.running, self.starts = True, self.starts + 1

    def stop(self):
        was, self.running = self.running, False
        return np.full(int(vt.RATE * 2), 0.3, np.float32) if was else np.zeros(0, np.float32)


class FakeLight:
    def __init__(self):
        self.state, self.level = None, 0.0

    def set(self, state):
        self.state = state

    def draw(self):
        pass


@pytest.fixture(autouse=True)
def isolated(tmp_path, monkeypatch):
    for module in (vt, ui_api, settings_window, corrections):
        monkeypatch.setattr(module, "HERE", tmp_path)
    monkeypatch.setattr(ui_api, "HISTORY", tmp_path / "history.jsonl")
    monkeypatch.setattr(ui_api, "WORDS", tmp_path / "words.txt")
    monkeypatch.setattr(corrections, "FILE", tmp_path / "corrections.json")
    monkeypatch.setattr(corrections, "PENDING", tmp_path / "corrections_pending.json")
    monkeypatch.setattr(corrections, "_cache", {"mtime": None, "pairs": []})
    monkeypatch.setattr(vt, "beep", lambda *a: None)
    monkeypatch.setattr(vt, "paste", lambda text: PASTED.append(text))
    monkeypatch.setattr(vt.win32api, "keybd_event", lambda *a: None)
    monkeypatch.setattr(vt.win32api, "GetAsyncKeyState", lambda vk: 0)
    monkeypatch.setattr(vt.log, "disabled", True)
    monkeypatch.setattr(light_keys, "READ_WAIT_SECONDS", 0)
    monkeypatch.setattr(light_keys, "on_problem", None)
    monkeypatch.setattr(light_keys, "_STATE", {"settings": None, "no_save": False})
    monkeypatch.setattr(light_keys, "_RUN_ONLY", {})
    monkeypatch.setattr(light_keys, "FORCED", {})
    PASTED.clear()
    config = dict(vt.CONFIG)
    yield tmp_path
    vt.CONFIG.clear()  # (a save in a test changes the app's own settings in memory: put them back)
    vt.CONFIG.update(config)


@pytest.fixture
def tool(monkeypatch):
    t = vt.VoiceTool(recorder=FakeRecorder(), transcriber=lambda audio: "", with_ui=False)
    t.light = FakeLight()
    t.taught, t.cards = [], []
    monkeypatch.setattr(t, "_teach", lambda trig: t.taught.append(trig.name))
    monkeypatch.setattr(t, "_front_window", lambda: {"app": "notepad.exe", "title": "Untitled", "hwnd": 2, "focus": 20})
    monkeypatch.setattr(t, "_card", lambda title, body, seconds=60, speak=False, buttons=None: t.cards.append((title, body)))
    return t


def key(tool, vk, down, at):
    tool._handle("trigger", tool.triggers[vk], down, at)


def run_jobs(tool):
    while not tool.jobs.empty():
        job, audio = tool.jobs.get()
        tool._dictate(job, audio)


# ---------------------------------------------------------------------------------------------- what is left out

def test_the_left_out_modules_are_not_in_the_folder():
    for name in ("meetings", "meetings_windows", "styles", "usage", "local_cleanup_win", "read_aloud", "transcribe_link",
                 "spending_report", "system_actions", "system_windows", "voice_actions", "actions_windows",
                 "speaker_windows", "make_start_menu_app"):
        assert not (HERE / f"{name}.py").exists(), name
    assert not hasattr(vt, "Cleaner") and not hasattr(vt, "MEETING_AUDIO") and not hasattr(vt, "clipboard_text")
    assert not hasattr(vt.VoiceTool, "_command") and not hasattr(vt.VoiceTool, "_claude")
    assert not hasattr(vt.VoiceTool, "_start_meeting") and not hasattr(vt.VoiceTool, "_read")
    import commands
    assert not hasattr(commands, "route") and not hasattr(commands, "ClaudeSession") and not hasattr(commands, "USER_SAYS")
    assert hasattr(commands, "capture_context") and hasattr(commands, "NO_COPY_APPS")


# ---------------------------------------------------------------------------------------------- dictation

def test_holding_left_alt_types_the_words_as_heard_with_fixes_and_word_spellings(tool, isolated):
    (isolated / "words.txt").write_text("Kubernetes\nGitHub\n", encoding="utf-8")
    corrections.add("cube ernest", "Kubernetes", "test")
    tool.transcribe = lambda audio: "I deployed it to cube ernest from github, colour me happy."
    now = time.monotonic()
    key(tool, ALT, True, now)
    assert tool.recorder.running
    key(tool, ALT, False, now + 1.0)
    run_jobs(tool)
    assert PASTED == ["I deployed it to Kubernetes from GitHub, color me happy."]
    row = json.loads((isolated / "history.jsonl").read_text(encoding="utf-8").splitlines()[-1])
    assert row["text"] == PASTED[0].strip() and row["raw"].startswith("I deployed") and row["fixes"] == 1
    assert "audio" not in row  # (Light never saves recordings)


def test_double_tap_left_alt_is_hands_free_and_a_tap_stops_it(tool):
    tool.transcribe = lambda audio: "Hands free works."
    now = time.monotonic()
    key(tool, ALT, True, now)
    key(tool, ALT, False, now + 0.1)
    key(tool, ALT, True, now + 0.2)
    assert tool.active is not None and tool.active.state == vt.LOCKED
    key(tool, ALT, False, now + 0.25)
    key(tool, ALT, True, now + 3.0)
    key(tool, ALT, False, now + 3.1)
    run_jobs(tool)
    assert PASTED == ["Hands free works."]


def test_a_quiet_mic_types_nothing_and_says_so(tool):
    tool.recorder.stop = lambda: np.full(int(vt.RATE * 1.0), 0.0001, np.float32)
    now = time.monotonic()
    key(tool, ALT, True, now)
    key(tool, ALT, False, now + 1.0)
    assert tool.jobs.empty() and tool.cards and tool.cards[0][0] == "Didn't hear you"


def test_the_teach_box_fix_is_learned_and_used_next_time(tool, isolated, monkeypatch):
    monkeypatch.setattr(vt, "bring_to_front", lambda hwnd, tries=10: False)  # (never touches a real window)
    tool._learn_typed("cube ernest", "Kubernetes", 0, "cube ernest")
    assert corrections.load() == [("cube ernest", "Kubernetes")]
    assert "Kubernetes" in (isolated / "words.txt").read_text(encoding="utf-8")
    tool._learn_typed("", "Zyxel", 0, "")
    assert "Zyxel" in (isolated / "words.txt").read_text(encoding="utf-8")
    assert [c[0] for c in tool.cards] == ["Learned", "Learned"]


class FakeTkWidget:
    """A tkinter widget that remembers how it was made and where it was gridded, and ignores everything else."""

    def __init__(self, parent=None, **kw):
        self.parent, self.kw, self.grid_kw, self.children = parent, kw, None, []
        if parent is not None:
            parent.children.append(self)

    def __getattr__(self, name):
        return lambda *a, **k: None

    def grid(self, **kw):
        self.grid_kw = dict(kw)
        return self

    def grid_configure(self, **kw):
        self.grid_kw.update(kw)

    def grid_slaves(self, row=None):
        return [c for c in self.children if c.grid_kw and (row is None or c.grid_kw.get("row") == row)]

    def winfo_reqwidth(self):
        return 300

    def winfo_reqheight(self):
        return 160

    def winfo_id(self):
        return 5


def open_teach_box(monkeypatch, selection):
    """The real TeachWindow code on fake tkinter widgets (no window is made, no real window handle is touched)."""
    import win32gui
    fake = SimpleNamespace(Toplevel=FakeTkWidget, Label=FakeTkWidget, Entry=FakeTkWidget,
                           StringVar=lambda value="": SimpleNamespace(get=lambda: value))
    monkeypatch.setattr(vt, "tk", fake)
    monkeypatch.setattr(vt.screen_layout, "work_area_and_dpi", lambda: ((0, 0, 1920, 1040), 96))
    monkeypatch.setattr(win32gui, "GetParent", lambda h: 0)
    monkeypatch.setattr(win32gui, "SetForegroundWindow", lambda h: None)
    box = vt.TeachWindow(FakeTkWidget(), selection, 99, lambda *a: None)
    return box, {c.kw.get("text"): c for c in box.win.children if "text" in c.kw}


def test_teach_with_nothing_selected_says_to_highlight_a_word_first_naming_the_chosen_key(monkeypatch):
    box, labels = open_teach_box(monkeypatch, "")
    hint = labels["Highlight a word first, then press Shift + Left Alt."]  # (the default Teach key)
    footer = [text for text in labels if text.startswith("Leave")][0]
    assert hint.grid_kw["row"] == 3 and labels[footer].grid_kw["row"] == 4  # the hint sits under the boxes, the gray line below it
    assert hint.kw["fg"] != labels[footer].kw["fg"] and hint.kw["font"][1] >= 10
    for ident, name in (("alt_r", "Right Alt"), ("caps_lock", "Caps Lock"), ("f13", "F13")):  # whichever key Settings chose
        monkeypatch.setitem(vt.CONFIG, "teach_key", ident)
        _, labels = open_teach_box(monkeypatch, "  ")
        assert f"Highlight a word first, then press Shift + {name}." in labels
    monkeypatch.setitem(vt.CONFIG, "teach_key", "no such key")  # (an unknown key falls back to the default, as everywhere else)
    assert "Highlight a word first, then press Shift + Left Alt." in open_teach_box(monkeypatch, "")[1]


def test_teach_with_a_word_selected_is_just_as_before_no_hint(monkeypatch):
    box, labels = open_teach_box(monkeypatch, "pier slight")
    assert not [text for text in labels if text.startswith("Highlight")]
    assert box.heard.get() == "pier slight" and [c.grid_kw["row"] for c in labels.values()] == [0, 1, 2, 3]
    assert labels[[t for t in labels if t.startswith("Leave")][0]].grid_kw["row"] == 3


# ---------------------------------------------------------------------------------------------- the window's API

class Host:
    def __init__(self):
        self.calls = []

    def computer_name(self): return "TEST-PC"
    def paused(self, p): self.calls.append(("paused", p))
    def startup_enabled(self): return False
    def set_startup(self, on): self.calls.append(("startup", on))
    def open_path(self, path): self.calls.append(("open", Path(path).name))
    def copy_text(self, text): self.calls.append(("copy", text))
    def quit(self): self.calls.append(("quit",))


def test_the_window_api_offers_only_light_settings(isolated):
    saved = []
    api = ui_api.API(Host(), {"enabled": True}, fake_save(saved))
    s = api.call("settings")
    assert [f["key"] for f in s["features"]] == ["dictate_enabled", "show_light", "beeps", "typed_fix"]
    assert set(s) == {"features", "startup", "enabled", "keys", "theme"}
    for gone in ("cleanup", "offline_only", "command_enabled", "claude_actions", "meeting_notes", "use_style",
                 "save_recordings", "speak_answers", "auto_learn"):
        with pytest.raises(ValueError):
            api.call("set_setting", {"key": gone, "on": True})
    for gone in ("spending", "meetings", "transcripts", "shortcuts", "styles", "style", "set_translate", "try_voice",
                 "set_notes", "start_meeting"):
        with pytest.raises(ValueError):
            api.call(gone)
    with pytest.raises(ValueError):
        api.call("open", {"what": "journal"})
    api.call("set_setting", {"key": "beeps", "on": False})
    assert saved[-1]["beeps"] is False


def test_the_overview_counts_todays_dictation(isolated):
    today = time.strftime("%Y-%m-%d")
    (isolated / "history.jsonl").write_text("\n".join(json.dumps(r) for r in [
        {"at": f"{today} 09:00:00", "raw": "hello there", "text": "Hello there.", "app": "notepad.exe", "fixes": 2},
        {"at": "2020-01-01 09:00:00", "raw": "old", "text": "Old.", "app": "notepad.exe"}]) + "\n", encoding="utf-8")
    o = ui_api.API(Host(), {"enabled": True}, fake_save()).call("overview")
    assert o["today"] == {"dictations": 1, "words": 2, "fixes": 2}
    assert "month_cost" not in o and "claude_actions" not in o and len(o["recent"]) == 2


def fake_save(saved=None):
    """A stand-in for settings_window.save (config, changes): applies the change and says it was saved."""
    def save(config, changes):
        config.update(changes)
        if saved is not None:
            saved.append(dict(config))
        return {"saved": True, "toast": "Saved"}
    return save


class QuietLog:
    def info(self, *a, **k): pass
    warning = error = exception = debug = info


class Sink:
    """The window's stdin as the tool writes to it: every reply, in the order sent."""
    def __init__(self):
        self.lines = []

    def write(self, line):
        self.lines.append(json.loads(line))

    def flush(self):
        pass


class FakeWindowProc:
    pid = 1

    def __init__(self, requests):
        self.stdout = iter(json.dumps(r) + "\n" for r in requests)
        self.stdin = Sink()

    def poll(self):
        return None


def _write_config(path, **changes):
    path.write_text(json.dumps(dict(light_keys.DEFAULT_CONFIG, **changes), indent=2) + "\n", encoding="utf-8")


def test_a_settings_reset_is_said_once_on_home_until_the_person_says_got_it(isolated):
    class NoticeHost(Host):
        text = "Your settings file couldn't be read, so Chirpy started with its usual settings."

        def settings_notice(self):
            return self.text or ""

        def notice_seen(self):
            self.text = None
    host = NoticeHost()
    api = ui_api.API(host, {"enabled": True}, fake_save())
    assert api.call("overview")["notice"] == NoticeHost.text
    assert api.call("overview")["notice"] == NoticeHost.text  # (Home refreshes itself: still there until "Got it")
    api.call("dismiss_notice")
    assert api.call("overview")["notice"] == "" and api.call("overview")["notice"] == ""
    assert ui_api.API(Host(), {"enabled": True}, fake_save()).call("overview")["notice"] == ""  # (a good start says nothing)
    page = (Path(ui_api.__file__).parent / "ui" / "index.html").read_text(encoding="utf-8")
    assert 'call("dismiss_notice")' in page and "Got it" in page


def test_the_real_host_gives_the_startup_notice_once(monkeypatch):
    monkeypatch.setattr(vt, "_NOTICE", {"text": "settings were reset"})
    host = vt.WinHost(object())
    assert host.settings_notice() == "settings were reset"
    host.notice_seen()
    assert host.settings_notice() == ""


def test_BAR2_two_quick_clicks_both_stick_in_the_order_they_were_made(isolated, monkeypatch):
    """Through the real way in: the tool reads the window's requests (app_window_win) and the real setters save through
    the real save path. The first save is held just before its swap. Every later settings click waits its turn (at
    4a030f81 each one started at once on a thread of its own and raced for the lock, so the order wasn't kept); a
    non-settings request is not held up. The file ends with every change, and the last click on a switch wins."""
    import threading
    path = isolated / "config.json"
    _write_config(path, theme="light")
    config = light_keys.load_config_and_notice(path)[0]
    api = ui_api.API(Host(), config, settings_window.save)
    entered = []
    real_call = api.call
    api.call = lambda name, args=None: (entered.append(name), real_call(name, args))[1]
    window = app_window_win.AppWindow(api, QuietLog())
    staged, go = threading.Event(), threading.Event()
    real = light_keys._replace_atomic

    def hold_first(src, dst):
        if dst.name == "config.json" and not staged.is_set():
            staged.set()
            go.wait(5)
        return real(src, dst)
    monkeypatch.setattr(light_keys, "_replace_atomic", hold_first)
    proc = FakeWindowProc([{"id": 1, "name": "set_theme", "args": {"theme": "dark"}},
                           {"id": 2, "name": "set_setting", "args": {"key": "beeps", "on": False}},
                           {"id": 3, "name": "set_setting", "args": {"key": "show_light", "on": False}},
                           {"id": 4, "name": "set_setting", "args": {"key": "show_light", "on": True}},
                           {"id": 5, "name": "overview"}])
    window.proc = proc
    window._read_stdout(proc)
    assert staged.wait(5)
    deadline = time.monotonic() + 5
    while not any(m.get("id") == 5 for m in proc.stdin.lines) and time.monotonic() < deadline:
        time.sleep(0.02)
    time.sleep(0.2)
    assert [m["id"] for m in proc.stdin.lines] == [5]  # (Home's refresh answered while the first save is held)
    assert entered.count("set_setting") == 0  # (the later clicks wait their turn; none has started)
    go.set()
    deadline = time.monotonic() + 10
    while len(proc.stdin.lines) < 5 and time.monotonic() < deadline:
        time.sleep(0.02)
    replies = [m for m in proc.stdin.lines if m["id"] != 5]
    assert [m["id"] for m in replies] == [1, 2, 3, 4] and all(m["ok"] and m["data"]["saved"] for m in replies)
    saved = json.loads(path.read_text(encoding="utf-8"))
    assert (saved["theme"], saved["beeps"], saved["show_light"]) == ("dark", False, True) and config == saved


def test_P1_each_key_list_sends_and_saves_only_its_own_key(isolated):
    path = isolated / "config.json"
    _write_config(path)
    config = light_keys.load_config_and_notice(path)[0]
    api = ui_api.API(Host(), config, settings_window.save)
    _write_config(path, teach_key="f14")  # (the file holds a Teach key this window hasn't shown yet)
    r = api.call("set_keys", {"dictate": "f13"})
    assert r["saved"] and (r["keys"]["dictate_key"], r["keys"]["teach_key"]) == ("f13", "f14")
    saved = json.loads(path.read_text(encoding="utf-8"))
    assert (saved["dictate_key"], saved["teach_key"]) == ("f13", "f14")
    assert api.call("set_keys", {})["saved"] is False
    page = (Path(ui_api.__file__).parent / "ui" / "index.html").read_text(encoding="utf-8")
    assert 'call("set_keys", { [which]: box.value })' in page and 'saveKey("dictate", e.target)' in page and 'saveKey("teach", e.target)' in page


def test_P3_after_a_start_that_couldnt_open_the_file_pause_still_pauses_and_says_it_wasnt_saved(tool, isolated, monkeypatch):
    path = isolated / "config.json"
    _write_config(path, dictate_key="f13")
    before = path.read_bytes()
    real = Path.read_bytes
    busy = {"on": True}
    monkeypatch.setattr(Path, "read_bytes", lambda self: (_ for _ in ()).throw(PermissionError(13, "in use"))
                        if self.name == "config.json" and busy["on"] else real(self))
    monkeypatch.setattr(vt, "_NOTICE", {"text": None})
    monkeypatch.setattr(light_keys, "on_problem", lambda text, detail: vt._NOTICE.update(text=text))
    problem, notice = light_keys.start(path, vt.CONFIG)
    assert notice == light_keys.START_BUSY
    busy["on"] = False
    tool._pause(True)  # (the tray's Pause)
    assert vt.CONFIG["enabled"] is False and path.read_bytes() == before
    api = ui_api.API(vt.WinHost(tool), vt.CONFIG, settings_window.save)
    assert api.call("overview")["notice"] == light_keys.NOT_SAVED
    r = api.call("set_setting", {"key": "beeps", "on": False})
    assert r == {"saved": False, "toast": light_keys.NOT_SAVED, "key": "beeps", "on": False} and path.read_bytes() == before


def test_P5_the_page_says_saved_only_when_the_reply_says_so(isolated):
    page = (Path(ui_api.__file__).parent / "ui" / "index.html").read_text(encoding="utf-8")
    assert 'call("set_setting", { key: i.dataset.key, on: i.checked }).then(r => toast(r.toast))' in page
    assert "if (!r.saved) toast(r.toast)" in page and "toast(r.reason || r.toast)" in page
    api = ui_api.API(Host(), {"enabled": True}, lambda c, ch: {"saved": False, "toast": light_keys.NOT_SAVED})
    assert api.call("set_setting", {"key": "beeps", "on": False}) == {"saved": False, "toast": light_keys.NOT_SAVED, "key": "beeps", "on": True}
    assert api.call("set_theme", {"theme": "dark"})["toast"] == light_keys.NOT_SAVED
    assert api.call("set_paused", {"paused": True})["toast"] == light_keys.NOT_SAVED


def test_start_at_sign_in_in_test_mode_never_touches_the_registry():
    assert settings_window.startup_enabled() is False
    settings_window.set_startup(True)
    assert settings_window.startup_enabled() is True
    settings_window.set_startup(False)
    assert settings_window.startup_enabled() is False
    assert settings_window.RUN_VALUE == "Chirpy"
    assert settings_window.startup_command().endswith(f'"{settings_window.HERE / "voicetool.py"}"')


# ---------------------------------------------------------------------------------------------- Teach: Shift + tap a key

ALT_R, CAPS, F13, F14 = 0xA5, 0x14, 0x7C, 0x7D
SHIFT = vt.win32con.VK_SHIFT


def shift_held(monkeypatch, held=True):
    monkeypatch.setattr(vt.win32api, "GetAsyncKeyState", lambda vk: 0x8000 if held and vk == SHIFT else 0)


def with_keys(monkeypatch, dictate=None, teach=None, **more):
    """A Light tool whose Settings choose these keys (the settings are put back after the test)."""
    for name, value in (("dictate_key", dictate), ("teach_key", teach)):
        if value is None:
            monkeypatch.delitem(vt.CONFIG, name, raising=False)
        else:
            monkeypatch.setitem(vt.CONFIG, name, value)
    for name, value in more.items():
        monkeypatch.setitem(vt.CONFIG, name, value)
    t = vt.VoiceTool(recorder=FakeRecorder(), transcriber=lambda audio: "", with_ui=False)
    t.light = FakeLight()
    t.taught = []
    t.cards = []
    monkeypatch.setattr(t, "_teach", lambda trig: t.taught.append(trig.name))
    monkeypatch.setattr(t, "_front_window", lambda: {"app": "notepad.exe", "title": "Untitled", "hwnd": 2, "focus": 20})
    monkeypatch.setattr(t, "_card", lambda title, body, seconds=60, speak=False, buttons=None: t.cards.append((title, body)))
    monkeypatch.setattr(t, "_watch_mouse", lambda: None)  # (a Right Ctrl key would start the mouse hook)
    return t


class FakeListener:
    def suppress_event(self):
        raise vt._SUPPRESS()


def hook(tool, vk, down, scan=0):
    """One key through the real keyboard-hook handler (what Windows would hand it). 'suppressed' if Windows would
    never see the key."""
    tool.listener = tool.listener or FakeListener()
    data = vt._KBDLLHOOKSTRUCT(vk, scan, 0, 0, 0)
    sys_key = vk in (0xA4, 0xA5)
    msg = (vt.win32con.WM_SYSKEYDOWN if down else vt.win32con.WM_SYSKEYUP) if sys_key else (
        vt.win32con.WM_KEYDOWN if down else vt.win32con.WM_KEYUP)
    try:
        tool._filter(msg, data)
    except vt._SUPPRESS:
        return "suppressed"
    return "passed"


def drain(tool):
    """Handle what the hook queued (what the window loop does 30 times a second)."""
    while not tool.events.empty():
        kind, trigger, value, at = tool.events.get()
        tool._handle(kind, trigger, value, at)


def test_light_has_one_handler_for_the_dictate_key_and_teach_shares_it_by_default(tool):
    assert list(tool.triggers) == [ALT] and tool.triggers[ALT].name == "dictate"
    assert tool.triggers[ALT].teach_with.name == "teach" and CTRL not in tool.triggers


def test_left_ctrl_is_not_a_light_key_at_all(tool):
    now = time.monotonic()
    for hold in (0.05, 0.5, 2.0):
        tool._handle("other", None, vt.VK_LCONTROL, now)
        now += 3
    assert tool.recorder.starts == 0 and tool.active is None and tool.jobs.empty() and tool.taught == []
    assert hook(tool, CTRL, True) == "passed" and hook(tool, CTRL, False) == "passed"
    assert [e[0] for e in list(tool.events.queue)] == ["other"]  # (just a key pressed: never a Light key)
    drain(tool)
    assert tool.active is None and tool.recorder.starts == 0 and tool.taught == []


def test_shift_and_tap_left_alt_opens_the_teach_box_and_never_starts_dictation(tool, monkeypatch):
    shift_held(monkeypatch)
    now = time.monotonic()
    key(tool, ALT, True, now)
    assert tool.active is tool.triggers[ALT].teach_with and tool.active.state == vt.TEACH
    assert tool.recorder.starts == 0 and not tool.recorder.running
    key(tool, ALT, False, now + 0.1)
    time.sleep(0.1)
    assert tool.taught == ["teach"] and tool.active is None and tool.recorder.starts == 0 and tool.jobs.empty()
    for at in (0.15, 0.3):  # again and again, quickly: never the double-tap that turns hands-free on
        key(tool, ALT, True, now + at)
        key(tool, ALT, False, now + at + 0.05)
    time.sleep(0.1)
    assert tool.taught == ["teach"] * 3 and tool.recorder.starts == 0 and tool.active is None


def test_a_plain_tap_then_shift_and_tap_left_alt_is_a_teach_not_a_double_tap(tool, monkeypatch):
    now = time.monotonic()
    key(tool, ALT, True, now)
    key(tool, ALT, False, now + 0.1)  # (a plain tap: waiting to see whether a second one follows)
    assert tool.active is tool.triggers[ALT] and tool.active.state == vt.TAP_WAIT
    shift_held(monkeypatch)
    key(tool, ALT, True, now + 0.2)  # Shift + Alt inside the double-tap window
    key(tool, ALT, False, now + 0.25)
    time.sleep(0.1)
    assert tool.taught == ["teach"] and tool.active is None and not tool.recorder.running
    assert tool.triggers[ALT].state == vt.IDLE and tool.jobs.empty()


def test_alt_pressed_first_and_shift_second_is_a_shortcut_not_a_teach(tool, monkeypatch):
    now = time.monotonic()
    key(tool, ALT, True, now)  # (a dictation begins: no Shift yet)
    assert tool.recorder.running
    shift_held(monkeypatch)
    tool._handle("other", None, vt.win32con.VK_LSHIFT, now + 0.05)  # Alt + Shift: Windows' own shortcut
    assert tool.active is None and not tool.recorder.running
    key(tool, ALT, True, now + 0.1)  # (the held key repeating)
    key(tool, ALT, False, now + 0.5)
    time.sleep(0.05)
    assert tool.taught == [] and tool.jobs.empty()


def test_shift_alt_with_teach_switched_off_does_nothing_at_all(tool, monkeypatch):
    monkeypatch.setitem(vt.CONFIG, "typed_fix", False)
    shift_held(monkeypatch)
    now = time.monotonic()
    key(tool, ALT, True, now)
    assert tool.active is None and tool.recorder.starts == 0
    key(tool, ALT, False, now + 0.2)
    shift_held(monkeypatch, False)
    key(tool, ALT, True, now + 1)  # a plain Alt still dictates
    assert tool.recorder.running
    key(tool, ALT, False, now + 2)
    assert tool.taught == [] and tool.jobs.qsize() == 1


def test_teach_still_works_on_the_shared_key_when_dictation_is_switched_off(tool, monkeypatch):
    monkeypatch.setitem(vt.CONFIG, "dictate_enabled", False)
    shift_held(monkeypatch)
    assert hook(tool, ALT, True) == "suppressed"  # (Shift + Alt: Windows is handed Alt again by the hook, see below)
    drain(tool)
    assert tool.active is tool.triggers[ALT].teach_with
    hook(tool, ALT, False)
    drain(tool)
    time.sleep(0.1)
    assert tool.taught == ["teach"] and tool.recorder.starts == 0
    shift_held(monkeypatch, False)
    assert hook(tool, ALT, True) == "passed" and hook(tool, ALT, False) == "passed"  # a plain Alt keeps its normal job
    drain(tool)
    assert tool.recorder.starts == 0 and tool.taught == ["teach"]


def test_shift_alt_keeps_windows_from_switching_the_input_language(tool, monkeypatch):
    """Windows switches the keyboard language on Alt + Shift (with two languages installed) unless another key is pressed
    in between: the hook sends Alt again, followed by an unused key, and Windows never sees a bare Alt + Shift."""
    sent = []
    monkeypatch.setattr(vt.win32api, "keybd_event", lambda *a: sent.append(a))
    shift_held(monkeypatch)
    assert hook(tool, ALT, True) == "suppressed"
    assert sent == [(ALT, 0, 0, 0), (vt.MASK_VK, 0, 0, 0), (vt.MASK_VK, 0, vt.win32con.KEYEVENTF_KEYUP, 0)]
    drain(tool)
    hook(tool, ALT, False)
    drain(tool)
    time.sleep(0.05)
    assert tool.taught == ["teach"] and tool.recorder.starts == 0
    shift_held(monkeypatch, False)  # no Shift: a plain Alt press is left alone
    sent.clear()
    assert hook(tool, ALT, True) == "passed" and sent == []
    drain(tool)
    hook(tool, ALT, False)
    drain(tool)
    monkeypatch.setitem(vt.CONFIG, "typed_fix", False)  # Teach switched off: Shift + Alt is Windows' own again
    shift_held(monkeypatch)
    sent.clear()
    assert hook(tool, ALT, True) == "passed" and sent == []


def test_teach_is_dropped_by_another_key_or_when_switched_off(tool, monkeypatch):
    shift_held(monkeypatch)
    now = time.monotonic()
    key(tool, ALT, True, now)
    tool._handle("other", None, 0x54, now + 0.05)  # Alt + Shift + T: a shortcut, not a teach
    key(tool, ALT, False, now + 0.1)
    monkeypatch.setitem(vt.CONFIG, "typed_fix", False)
    key(tool, ALT, True, now + 1)
    key(tool, ALT, False, now + 1.1)
    time.sleep(0.1)
    assert tool.taught == [] and tool.active is None


# ---------------------------------------------------------------------------------------------- choosing the keys

ALL_KEYS = light_keys.ORDER


def vk_of(ident):
    from pynput import keyboard
    return getattr(keyboard.Key, ident).value.vk


@pytest.mark.parametrize("ident", ALL_KEYS)
def test_every_key_on_the_list_works_as_the_dictate_key(ident, monkeypatch):
    other = "f23" if ident == "f24" else "f24"
    t = with_keys(monkeypatch, dictate=ident, teach=other)
    vk = vk_of(ident)
    assert t.triggers[vk].name == "dictate" and (ALT in t.triggers) == (ident == "alt_l")
    t.transcribe = lambda audio: "Hello there."
    now = time.monotonic()
    if light_keys.KEYS[ident].ctrl:  # (a Ctrl key opens the microphone only once it has been down a moment)
        key(t, vk, True, now - 1.0)
        t._tick_once()
    else:
        key(t, vk, True, now)
    assert t.recorder.running, ident
    key(t, vk, False, now + 1.0)
    run_jobs(t)
    assert PASTED == ["Hello there."], ident


@pytest.mark.parametrize("ident", ALL_KEYS)
def test_every_key_on_the_list_works_as_shift_plus_the_teach_key(ident, monkeypatch):
    other = "f23" if ident == "f24" else "f24"
    t = with_keys(monkeypatch, dictate=other, teach=ident)
    vk = vk_of(ident)
    assert t.triggers[vk].name == "teach"
    shift_held(monkeypatch)
    now = time.monotonic()
    key(t, vk, True, now)
    assert t.active is t.triggers[vk] and t.active.state == vt.TEACH
    key(t, vk, False, now + 0.1)
    time.sleep(0.05)
    assert t.taught == ["teach"] and t.recorder.starts == 0, ident
    shift_held(monkeypatch, False)  # (the same key without Shift does nothing for Teach)
    key(t, vk, True, now + 1)
    key(t, vk, False, now + 1.1)
    time.sleep(0.05)
    assert t.taught == ["teach"] and t.recorder.starts == 0


@pytest.mark.parametrize("ident", ALL_KEYS)
def test_teach_and_dictate_can_share_any_key_on_the_list(ident, monkeypatch):
    t = with_keys(monkeypatch, dictate=ident, teach=ident)
    vk = vk_of(ident)
    assert list(t.triggers) == [vk] and t.triggers[vk].teach_with.name == "teach"
    shift_held(monkeypatch)
    now = time.monotonic()
    key(t, vk, True, now)
    key(t, vk, False, now + 0.1)
    time.sleep(0.05)
    assert t.taught == ["teach"] and t.recorder.starts == 0, ident
    shift_held(monkeypatch, False)
    t.transcribe = lambda audio: "Shared."
    if light_keys.KEYS[ident].ctrl:
        key(t, vk, True, now - 1.0)
        t._tick_once()
    else:
        key(t, vk, True, now + 4)
    assert t.recorder.running, ident
    key(t, vk, False, now + 5)
    run_jobs(t)
    assert PASTED == ["Shared."]


def test_teach_and_dictate_on_two_different_keys_do_not_mix_them_up(monkeypatch):
    t = with_keys(monkeypatch, dictate="alt_r", teach="alt_l")
    assert sorted(x.name for x in t.triggers.values()) == ["dictate", "teach"]
    shift_held(monkeypatch)
    now = time.monotonic()
    key(t, ALT, True, now)  # Shift + Left Alt: Teach
    key(t, ALT, False, now + 0.1)
    time.sleep(0.05)
    assert t.taught == ["teach"] and t.recorder.starts == 0
    shift_held(monkeypatch, False)
    key(t, ALT, True, now + 1)  # Left Alt on its own is no longer a Light key
    key(t, ALT, False, now + 2)
    assert t.recorder.starts == 0
    key(t, ALT_R, True, now + 3)
    assert t.recorder.running


def test_caps_lock_is_swallowed_so_it_never_switches_caps_on(monkeypatch):
    t = with_keys(monkeypatch, dictate="caps_lock", teach="caps_lock")
    assert hook(t, CAPS, True) == "suppressed" and hook(t, CAPS, False) == "suppressed"
    sent = []
    monkeypatch.setattr(vt.win32api, "keybd_event", lambda *a: sent.append(a))
    shift_held(monkeypatch)
    assert hook(t, CAPS, True) == "suppressed" and sent == []  # (no key is sent back to Windows for Caps Lock)


def test_the_old_settings_file_gets_the_new_teach_key(monkeypatch):
    for old in ({}, {"teach_key": "ctrl_l"}, {"dictate_key": "alt_l", "teach_key": "ctrl_l", "let_key_through": True},
                {"teach_key": "no_such_key", "dictate_key": 7}):
        assert light_keys.resolve(old) == ("alt_l", "alt_l")
    t = with_keys(monkeypatch, dictate="alt_l", teach="ctrl_l")  # (what the earlier install left in its settings)
    assert list(t.triggers) == [ALT] and t.triggers[ALT].teach_with is not None and CTRL not in t.triggers
    api = ui_api.API(Host(), {"dictate_enabled": True, "typed_fix": True, "teach_key": "ctrl_l"}, fake_save())
    badge = {f["key"]: f["badge"] for f in api.call("settings")["features"]}
    assert badge == {"dictate_enabled": "Hold or double-tap Left Alt", "show_light": None, "beeps": None,
                     "typed_fix": "Shift + tap Left Alt"}


def test_new_keys_work_at_once_without_a_restart(tool, isolated, monkeypatch):
    path = isolated / "config.json"
    _write_config(path)
    light_keys.start(path, vt.CONFIG)
    while not tool.ui.empty():
        tool.ui.get_nowait()
    api = ui_api.API(vt.WinHost(tool), vt.CONFIG, settings_window.save)
    now = time.monotonic()
    key(tool, ALT, True, now)  # (a dictation is under way when the keys are changed)
    assert tool.recorder.running
    old_handler = tool.triggers[ALT]
    assert api.call("set_keys", {"dictate": "f13"})["saved"]
    result = api.call("set_keys", {"teach": "f14"})
    assert result["saved"] and result["keys"]["hold"] == "Hold F13" and result["keys"]["teach"] == "Shift + tap F14"
    while not tool.ui.empty():  # (the window loop does this on its next turn)
        tool.ui.get_nowait()()
    saved = json.loads(path.read_text(encoding="utf-8"))
    assert (saved["dictate_key"], saved["teach_key"]) == ("f13", "f14") and not tool.recorder.running and tool.active is None
    assert sorted(tool.triggers) == [F13, F14] and ALT not in tool.triggers
    assert tool._tooltip() == "Chirpy: hold F13 to talk"
    tool._handle("trigger", old_handler, True, now + 1)  # a left-over event for the old Left Alt handler: ignored
    assert tool.active is None and tool.recorder.starts == 1
    key(tool, F13, True, now + 2)
    assert tool.recorder.running and tool.active.name == "dictate"
    key(tool, F13, False, now + 3)
    run_jobs(tool)
    shift_held(monkeypatch)
    key(tool, F14, True, now + 4)
    key(tool, F14, False, now + 4.1)
    time.sleep(0.05)
    assert tool.taught == ["teach"]


def test_settings_refuses_a_key_that_is_not_on_the_list_in_plain_words(isolated):
    saved = []
    config = {"dictate_key": "alt_l", "teach_key": "alt_l"}
    api = ui_api.API(Host(), config, fake_save(saved))
    for bad in (("ctrl_l", "alt_l"), ("alt_l", "ctrl_l"), ("", "alt_l"), ("alt_l", 5), ("a", "b"), ("shift", "alt_l"), (None, None)):
        r = api.call("set_keys", {"dictate": bad[0], "teach": bad[1]})
        assert r["saved"] is False and "isn't one of the choices" in r["reason"] and r["keys"]["dictate_key"] == "alt_l"
    assert saved == [] and config == {"dictate_key": "alt_l", "teach_key": "alt_l"}
    for pair in (("alt_r", "alt_r"), ("f13", "alt_l"), ("caps_lock", "f24"), ("ctrl_r", "ctrl_r"), ("alt_l", "alt_l")):
        r = api.call("set_keys", {"dictate": pair[0], "teach": pair[1]})
        assert r["saved"] is True and r["reason"] == "" and (config["dictate_key"], config["teach_key"]) == pair
    assert len(saved) == 5


def test_the_list_of_choices_is_short_and_safe():
    c = light_keys.choices()
    labels = [x["label"] for x in c["dictate"]]
    assert labels[:4] == ["Left Alt", "Right Alt", "Right Ctrl", "Caps Lock"] and labels[4:] == [f"F{n}" for n in range(13, 25)]
    assert [x["label"] for x in c["teach"]] == ["Shift + " + x for x in labels]
    assert not {"ctrl_l", "shift", "shift_l", "esc", "enter", "space", "cmd"} & set(light_keys.KEYS)
    assert light_keys.problem("alt_l", "alt_l") == "" and light_keys.problem("alt_l", "ctrl_l")


def test_every_place_that_names_a_key_shows_the_chosen_one(monkeypatch, isolated):
    monkeypatch.setitem(vt.CONFIG, "dictate_key", "caps_lock")
    monkeypatch.setitem(vt.CONFIG, "teach_key", "f14")
    config = {"dictate_key": "caps_lock", "teach_key": "f14", "dictate_enabled": True, "typed_fix": True}
    api = ui_api.API(Host(), config, fake_save())
    k = api.call("overview")["keys"]
    assert (k["hold"], k["double"], k["teach"], k["dictate"]) == ("Hold Caps Lock", "Double-tap Caps Lock", "Shift + tap F14", "Caps Lock")
    badges = {f["key"]: f["badge"] for f in api.call("settings")["features"]}
    assert badges["dictate_enabled"] == "Hold or double-tap Caps Lock" and badges["typed_fix"] == "Shift + tap F14"
    assert api.call("settings")["keys"] == k
    t = vt.VoiceTool(recorder=FakeRecorder(), transcriber=lambda audio: "", with_ui=False)
    assert t._tooltip() == "Chirpy: hold Caps Lock to talk"
    tk_rows = {r[1]: r[3] for r in light_keys.features(settings_window.FEATURES, config)}
    assert tk_rows["dictate_enabled"] == "Hold or double-tap Caps Lock" and tk_rows["typed_fix"] == "Shift + tap F14"
    for text in (vt.DAILY_RUNNING_TEXT, vt.DAILY_STARTED_TEXT):
        assert "Left Alt" not in text and "Alt" not in text and "Ctrl" not in text
    html = app_window_win.build_page("home")
    assert html.count("Left Alt") == 3 and "Left Ctrl" not in html  # (only the three cards' starting words, set again on load)
    for ident in ('id="k-hold"', 'id="k-double"', 'id="k-teach"', "o.keys.hold", "drawKeyNames(o.keys)", 'id="dictatekey"',
                  'id="teachkey"'):
        assert ident in html, ident
    assert "Nothing yet. Hold Left Alt" not in html


def test_the_dark_light_switch_is_one_setting_on_home_and_in_settings(isolated):
    saved, host = [], Host()
    host.theme = lambda: "dark"
    config = {"enabled": True}
    api = ui_api.API(host, config, fake_save(saved))
    assert api.call("overview")["theme"] == "dark" and api.call("settings")["theme"] == "dark"  # (no choice yet: Windows' own)
    assert api.call("set_theme", {"theme": "light"}) == {"saved": True, "toast": "Saved", "theme": "light"}
    assert saved[-1]["theme"] == "light"  # (the look itself changes through _apply_settings: test_W6_...)
    assert api.call("overview")["theme"] == "light" and api.call("settings")["theme"] == "light"
    api.call("set_theme", {"theme": "dark"})
    assert config["theme"] == "dark" and api.call("overview")["theme"] == "dark"
    for bad in ("", "blue", None, "Dark"):
        with pytest.raises(ValueError):
            api.call("set_theme", {"theme": bad})
    assert config["theme"] == "dark"
    html = app_window_win.build_page("home")
    for ident in ('id="darkhome"', 'id="darkset"', "function applyTheme", 'call("set_theme"', "applyTheme(o.theme)",
                  "applyTheme(s.theme)", "Dark mode"):
        assert ident in html, ident


def test_the_window_opens_in_the_look_chosen_and_changes_look_at_once(monkeypatch):
    sent, started = [], []

    class Log:
        def info(self, *a): pass
        warning = error = exception = info

    class Proc:
        pid = 1
        stdin = stdout = stderr = None

        def poll(self): return None
    monkeypatch.setattr(app_window_win.subprocess, "Popen", lambda cmd, **kw: started.append(kw["env"]) or Proc())
    monkeypatch.setattr(app_window_win.threading, "Thread", lambda **kw: SimpleNamespace(start=lambda: None))
    monkeypatch.delenv("VOICETOOL_THEME", raising=False)
    window = app_window_win.AppWindow(SimpleNamespace(config={"theme": "light"}), Log())
    window._spawn("home")
    assert started[-1]["VOICETOOL_THEME"] == "light"
    window.api = SimpleNamespace(config={})
    window._spawn("home")
    assert "VOICETOOL_THEME" not in started[-1]
    window._send = lambda message, proc=None: sent.append(message) or True
    window.set_theme("dark")
    assert sent == [{"cmd": "theme", "theme": "dark"}]


# ---------------------------------------------------------------------------------------------- the first-try fixes

def test_the_word_list_header_does_not_name_a_left_out_feature(isolated):
    api = ui_api.API(Host(), {}, fake_save())
    (isolated / "words.txt").write_text("# One word or name per line. Chirpy spells these exactly like this.\nKubernetes\n", encoding="utf-8")
    api.call("add_word", {"word": "kubernetes"})
    api.call("add_word", {"word": " Zephyrion "})
    first = (isolated / "words.txt").read_text(encoding="utf-8").splitlines()
    assert first[0] == "# One word or name per line. Chirpy spells these exactly like this."
    assert first[1:] == ["Kubernetes", "Zephyrion"]
    assert api.call("words")["words"] == ["Kubernetes", "Zephyrion"]


def test_the_beeps_have_a_folder_of_their_own_that_the_everyday_tool_never_uses():
    assert vt._BEEP_DIR.name == "voicetool-light-sounds"


def test_the_page_says_what_is_needed_for_a_duplicate_or_empty_add_and_wraps_long_words():
    html = app_window_win.build_page("home")
    for text in ("Already in your list", "Type a word first, then press Add", "Type what it wrote and what you meant, then press Add fix",
                 "Type what it wrote too", "Type what you meant too", ".wordchip .edit { min-width: 0; overflow-wrap: anywhere",
                 "#fixlist td { overflow-wrap: anywhere; }"):
        assert text in html, text


def test_closing_the_window_while_it_is_still_opening_is_not_logged_as_a_failure():
    lines, failed = [], []

    class Log:
        def info(self, msg, *a): lines.append(("info", msg % a if a else msg))
        def error(self, msg, *a): lines.append(("error", msg % a if a else msg))
        warning = exception = info

    class Proc:
        pid = 7

        def poll(self): return 1  # (the window process has just been ended)

        def wait(self, timeout=None): return 1
    api = SimpleNamespace(config={})
    window = app_window_win.AppWindow(api, Log(), on_failed=lambda reason: failed.append(reason))
    proc = Proc()
    window.proc, window._closing = proc, proc  # (what close() does first)
    window._watch(proc)
    assert failed == [] and not any(level == "error" for level, _ in lines)
    other = Proc()
    window.proc, window._closing = other, None  # (a window that died by itself is still reported)
    window._watch(other)
    assert failed and any(level == "error" for level, _ in lines)


# ---------------------------------------------------------------------------------------------- the page

def test_the_page_has_four_pages_and_no_left_out_feature():
    html = app_window_win.build_page("home")
    for gone in ('data-page="style"', 'data-page="styles"', 'data-page="commands"', 'data-page="meetings"',
                 'data-page="transcribe"', 'data-page="spending"', "page-meetings", "Command key", "Hold Left Ctrl",
                 "Spending", "Meeting notes", "Transcribe", "Read it to me", "Voice notes", "Translate", "tidy-up",
                 "Claude", "Azure", "Journal", "Roadmap"):
        assert gone not in html, gone
    for kept in ('data-page="home"', 'data-page="history"', 'data-page="words"', 'data-page="settings"',
                 "Three ways to use it", "Shift + tap Left Alt", "no online service", "Chirpy"):
        assert kept in html, kept
    assert f'data-theme="{app_window_win.THEME}"' in html


def test_the_theme_follows_the_override(monkeypatch):
    monkeypatch.setenv("VOICETOOL_THEME", "light")
    assert app_window_win.windows_theme() == "light"
    monkeypatch.setenv("VOICETOOL_THEME", "dark")
    assert app_window_win.windows_theme() == "dark"


# ---------------------------------------------------------------------------------------------- never beside the daily tool

def test_light_refuses_to_start_while_the_daily_tool_runs(monkeypatch):
    told, made = [], []
    monkeypatch.delenv("VOICETOOL_TEST", raising=False)
    monkeypatch.setattr(vt, "daily_running", lambda: True)
    monkeypatch.setattr(vt, "tell", lambda text: told.append(text))
    monkeypatch.setattr(vt.win32event, "CreateMutex", lambda *a: made.append(a))
    monkeypatch.setattr(vt.screen_layout, "make_dpi_aware", lambda: None)
    monkeypatch.setattr(sys, "argv", ["voicetool.py"])
    vt.main()
    assert told and "everyday VoiceTool is running" in told[0] and made == []
    assert vt.MUTEX_NAME == "VoiceToolLight-single-instance" and vt.DAILY_MUTEX == "VoiceTool-single-instance"


def test_light_steps_aside_when_the_daily_tool_starts_while_it_runs(tool, monkeypatch):
    answers = iter([False, False, True])
    shown, quit_ = [], []
    monkeypatch.setattr(vt, "notice", lambda text: shown.append(text))
    monkeypatch.setattr(tool, "_quit", lambda: quit_.append(1))
    now = time.monotonic()
    key(tool, ALT, True, now)  # (dictating when the everyday tool appears)
    assert tool.recorder.running
    started = time.monotonic()
    tool._watch_daily(check=lambda: next(answers), every=0.01)
    assert time.monotonic() - started < 1
    tool.ui.get_nowait()()
    assert quit_ == [1] and shown and "stepped aside" in shown[0]
    assert tool.active is None and not tool.recorder.running and tool.jobs.empty()  # nothing is typed afterwards


def test_a_dictation_already_being_written_is_kept_in_history_but_never_pasted_after_stepping_aside(tool, isolated,
                                                                                                    monkeypatch):
    monkeypatch.setattr(vt, "notice", lambda text: None)
    monkeypatch.setattr(tool, "_quit", lambda: None)
    tool.transcribe = lambda audio: "This was said just before."
    now = time.monotonic()
    key(tool, ALT, True, now)
    key(tool, ALT, False, now + 1.0)  # the recording is finished and waiting to be written
    assert tool.jobs.qsize() == 1
    tool._watch_daily(check=lambda: True, every=0.01)
    tool.ui.get_nowait()()  # the everyday VoiceTool appeared: Light steps aside
    run_jobs(tool)  # the worker gets to the waiting dictation only now
    assert PASTED == []  # nothing pasted, so the clipboard is never left holding it
    row = json.loads((isolated / "history.jsonl").read_text(encoding="utf-8").splitlines()[-1])
    assert row["text"] == "This was said just before."


def test_stepping_aside_waits_for_a_paste_already_under_way(tool, monkeypatch):
    monkeypatch.setattr(vt, "notice", lambda text: None)
    quit_at = []
    monkeypatch.setattr(tool, "_quit", lambda: quit_at.append(time.monotonic()))
    tool._pasting = True
    threading = __import__("threading")
    done = []
    threading.Timer(0.3, lambda: (setattr(tool, "_pasting", False), done.append(time.monotonic()))).start()
    tool._step_aside()
    assert done and quit_at and quit_at[0] >= done[0]


def test_the_daily_watch_checks_every_one_and_a_half_seconds_and_stops_on_quit(tool):
    assert tool.DAILY_CHECK_SECONDS == 1.5
    tool._quitting = True
    tool._watch_daily(check=lambda: True, every=0.01)
    assert tool.ui.empty()


def test_daily_running_asks_for_the_daily_mutex(monkeypatch):
    asked = []

    def open_mutex(access, inherit, name):
        asked.append(name)
        raise vt.win32api.error(2, "OpenMutex", "not found") if hasattr(vt.win32api, "error") else OSError()
    monkeypatch.setattr(vt.win32event, "OpenMutex", open_mutex)
    assert vt.daily_running() is False and asked == ["VoiceTool-single-instance"]
    monkeypatch.setattr(vt.win32event, "OpenMutex", lambda *a: 99)
    monkeypatch.setattr(vt.win32api, "CloseHandle", lambda h: None)
    assert vt.daily_running() is True


def test_nothing_the_person_sees_still_says_voicetool_light():
    """The edition is called Chirpy everywhere in the app folder (the everyday VoiceTool keeps its own name, which stays where
    the messages talk about it)."""
    old_name = "VoiceTool" + " Light"
    seen = 0
    for path in HERE.rglob("*"):
        if (path.is_file() and path.suffix in (".py", ".html", ".js", ".cmd", ".json", ".txt", ".md") and "python" not in path.parts
                and "models" not in path.parts and not path.name.startswith("test_")):
            assert old_name not in path.read_text(encoding="utf-8", errors="ignore"), path.name
            seen += 1
    assert seen >= 10
    assert (HERE / "Start Chirpy.cmd").exists() and (HERE / "Stop Chirpy.cmd").exists()
    assert vt.DAILY_RUNNING_TEXT.count("Chirpy") >= 1 and "everyday VoiceTool" in vt.DAILY_RUNNING_TEXT
    assert "Chirpy" in vt.DAILY_STARTED_TEXT and app_window_win.TITLE == "Chirpy"


# ---------------------------------------------------------------------------------------------- outside review: first use, wording

def test_no_microphone_gives_a_plain_card_that_says_what_to_do_not_only_a_beep(tool):
    def no_device():
        raise OSError("Error querying device -1")
    tool.recorder.start = no_device
    now = time.monotonic()
    key(tool, ALT, True, now)
    key(tool, ALT, False, now + 1.0)
    assert not tool.recorder.running and PASTED == [] and tool.jobs.empty()
    assert tool.cards and tool.cards[0][0] == "No microphone"
    body = tool.cards[0][1]
    assert "Plug in a microphone" in body and "Privacy & security" in body and "Microphone" in body and "desktop apps" in body
    assert "VoiceTool" not in body


def test_the_privacy_wording_promises_only_what_chirpy_can_prove():
    html = app_window_win.build_page("home")
    assert "Your voice is turned into text on this PC." in html and "Windows clipboard" in html
    # (Codex re-review of cfb9bbd1: the paste still goes ahead when the clipboard-history marks fail, so no promise about the
    # internet as a whole; the promise covers the speech processing and what is kept on this PC, and clipboard sync is named)
    for promise in ("Nothing you say leaves this computer", "Nothing is sent to the internet", "Nothing goes online",
                    "nothing is sent", "never leaves"):
        assert promise.lower() not in html.lower(), promise
    assert "clipboard sync across your devices" in html and "Microsoft account" in html
    # (re-review of 2c7d889e: the marks are a request Windows may ignore, so the page says "asks", never "marks"/"skip it")
    assert "asks Windows to keep that text out of clipboard history" in html and "doesn't always work" in html
    assert "It marks that text" not in html and "clipboard history" in html


# ---------------------------------------------------------------------------------------------- punctuation: dictating again

def say(tool, monkeypatch, words, hwnd=2):
    monkeypatch.setattr(tool, "_front_window", lambda: {"app": "notepad.exe", "title": "Untitled", "hwnd": hwnd, "focus": 20})
    tool.transcribe = lambda audio: words
    now = time.monotonic()
    key(tool, ALT, True, now)
    key(tool, ALT, False, now + 1.0)
    run_jobs(tool)


def test_a_second_dictation_is_joined_with_a_space_and_a_capital_after_sentence_punctuation(tool, monkeypatch):
    say(tool, monkeypatch, "I will call you today.")
    say(tool, monkeypatch, "next we talk about the plan.")
    say(tool, monkeypatch, "Is that ok?")
    say(tool, monkeypatch, "yes it is!")
    assert PASTED == ["I will call you today.", " Next we talk about the plan.", " Is that ok?", " Yes it is!"]


def test_a_second_dictation_after_a_comma_colon_or_semicolon_gets_a_space_only(tool, monkeypatch):
    for mark in (",", ":", ";"):
        PASTED.clear()
        tool._last_insert = None
        say(tool, monkeypatch, f"first part{mark}")
        say(tool, monkeypatch, "then the second part")
        assert PASTED == [f"first part{mark}", " then the second part"], mark


def test_the_join_never_doubles_a_space_and_the_first_dictation_is_typed_as_it_is(tool, monkeypatch):
    say(tool, monkeypatch, "Hello.")
    assert PASTED == ["Hello."]  # (nothing before it: no space in front, nothing added behind)
    tool.transcribe = lambda audio: " already spaced."
    say(tool, monkeypatch, " already spaced.")
    assert PASTED[-1] == " already spaced."  # (one space, not two)
    # a closing quote or bracket after the full stop still counts as the end of a sentence
    PASTED.clear()
    tool._last_insert = None
    say(tool, monkeypatch, 'He said "yes."')
    say(tool, monkeypatch, "then he left.")
    assert PASTED[1] == " Then he left."
    # a words-only ending still gets its space (the earlier trailing space is gone)
    PASTED.clear()
    tool._last_insert = None
    say(tool, monkeypatch, "and so on")
    say(tool, monkeypatch, "and on")
    assert PASTED == ["and so on", " and on"]


def test_the_join_only_continues_chirpys_own_last_insert_in_the_same_window_soon_after(tool, monkeypatch):
    say(tool, monkeypatch, "One.", hwnd=2)
    say(tool, monkeypatch, "another window.", hwnd=3)  # (a different window: it is a first dictation there)
    assert PASTED == ["One.", "another window."]
    tool._last_insert["at"] -= light_keys.CONTINUE_SECONDS + 5  # (a long pause: the person may have moved on)
    say(tool, monkeypatch, "much later.", hwnd=3)
    assert PASTED[-1] == "much later."
    # a dictation that was not typed (wrong window, nothing heard) is not remembered as an insert
    PASTED.clear()
    tool._last_insert = None
    tool.transcribe = lambda audio: ""
    say(tool, monkeypatch, "")
    assert PASTED == [] and getattr(tool, "_last_insert", None) is None
    # and the history keeps the words themselves, with no space in front
    assert not json.loads((Path(vt.HERE) / "history.jsonl").read_text(encoding="utf-8").splitlines()[-1])["text"].startswith(" ")


def test_continue_dictation_on_its_own():
    f = light_keys.continue_dictation
    assert f("hi there.", "") == "hi there." and f("hi", None) == "hi" and f("", "Done.") == ""
    assert f("next", "Done.") == " Next" and f("next", "Done?  ") == " Next" and f("next", "Done!") == " Next"
    assert f("then", "wait,") == " then" and f("then", "note:") == " then" and f("then", "one;") == " then"
    assert f(" next", "Done.") == " next" and f("\u00e9clair", "Done.") == " \u00c9clair"


# ---------------------------------------------------------------------------------------------- Send feedback (the public GitHub page)

def test_send_feedback_opens_the_public_issue_page_and_is_always_shown(isolated):
    host = Host()
    sent = []
    host.send_feedback = lambda: sent.append(1)
    api = ui_api.API(host, {"enabled": True}, fake_save())
    assert api.call("send_feedback") == {"opened": True} and sent == [1]
    assert "feedback" not in api.call("overview")  # (no hidden-until-set switch any more)
    assert light_keys.feedback_url({}) == light_keys.FEEDBACK_URL == "https://github.com/petercarter19-hue/Apps/issues/new"
    assert light_keys.feedback_url({"feedback_url": "https://example.org/issues/new"}) == "https://example.org/issues/new"
    for bad in ("", "http://example.org/x", "javascript:alert(1)", "mailto:a@b.c", "https://a b", None, 5):
        assert light_keys.feedback_url({"feedback_url": bad}) == light_keys.FEEDBACK_URL, bad
    assert "@" not in light_keys.FEEDBACK_URL


def test_the_feedback_link_has_the_title_chirpy_feedback_and_the_version_in_the_body(tmp_path):
    from urllib.parse import parse_qs, urlsplit
    (tmp_path / ".voicetool-light-from-daily").write_text("Chirpy (from daily) 3e17f4e6" + chr(10) + "Version: 2026.11.9" + chr(10), encoding="utf-8")
    assert light_keys.app_version(tmp_path) == "2026.11.9 (3e17f4e6)" and light_keys.app_version(tmp_path / "none") == "unknown"
    link = light_keys.feedback_link(light_keys.FEEDBACK_URL, light_keys.app_version(tmp_path))
    parts = urlsplit(link)
    assert (parts.scheme, parts.netloc, parts.path) == ("https", "github.com", "/petercarter19-hue/Apps/issues/new")
    query = parse_qs(parts.query)
    assert query["title"] == ["Chirpy feedback: "] and "Chirpy version: 2026.11.9 (3e17f4e6)" in query["body"][0]


def test_the_feedback_button_and_tray_entry_are_always_there():
    html = app_window_win.build_page("home")
    assert 'id="feedback">Send feedback' in html and 'call("send_feedback")' in html and 'style="display:none">Send feedback' not in html
    source = Path(vt.__file__).read_text(encoding="utf-8")
    assert 'pystray.MenuItem("Send feedback", lambda: ask(self._send_feedback))' in source and "visible=lambda item: bool(light_keys.feedback" not in source


# ---------------------------------------------------------------------------------------------- the five gaps (the window and the Teach box)

import light_files  # noqa: E402

NOT_SAVED = "Couldn't save this change. Your words and fixes were left as they were."


def window_api():
    return ui_api.API(Host(), {"enabled": True}, fake_save())


def swap_refused(monkeypatch):
    """Every save fails at the swap. Returns the real function, to put back."""
    real = light_keys._replace_atomic

    def refused(src, dst):
        raise PermissionError(5, "Access is denied")
    monkeypatch.setattr(light_keys, "_replace_atomic", refused)
    return real


def test_G2_clear_history_in_the_window_deletes_the_file_and_the_page_shows_it_empty(tool, isolated):
    api = window_api()
    tool._history({"raw": "my secret", "text": "My secret.", "app": "notepad.exe", "fixes": 0})
    tool._history({"raw": "another one", "text": "Another one.", "app": "notepad.exe", "fixes": 0})
    path = isolated / "history.jsonl"
    assert len(path.read_text(encoding="utf-8").splitlines()) == 2 and len(api.call("history")["items"]) == 2
    assert api.call("overview")["today"]["dictations"] == 2
    assert api.call("clear_history") == {"cleared": True, "toast": "History cleared"}
    assert not path.exists() and sorted(p.name for p in isolated.iterdir()) == []
    assert api.call("history")["items"] == [] and api.call("overview")["recent"] == [] and api.call("overview")["today"]["dictations"] == 0
    assert api.call("clear_history") == {"cleared": True, "toast": "History is already empty"}
    tool._history({"raw": "new", "text": "New.", "app": "", "fixes": 0})  # (History starts again from nothing)
    assert [r["text"] for r in api.call("history")["items"]] == ["New."]


def test_G3_the_window_saves_words_and_fixes_the_safe_way_and_says_only_what_happened(isolated, monkeypatch):
    api = window_api()
    (isolated / "words.txt").write_text(light_files.WORDS_HEADER + "Kubernetes\n", encoding="utf-8")
    assert api.call("add_word", {"word": "GitHub"}) == {"words": ["Kubernetes", "GitHub"], "saved": True, "kept": None, "reason": "", "toast": "Saved"}
    assert (isolated / "words.txt").read_text(encoding="utf-8") == light_files.WORDS_HEADER + "Kubernetes\nGitHub\n"
    added = api.call("add_fix", {"heard": "cube ernest", "meant": "Kubernetes"})
    assert added["added"] is True and added["toast"] == "Saved" and [f["heard"] for f in added["fixes"]] == ["cube ernest"]
    before = {name: (isolated / name).read_bytes() for name in ("words.txt", "corrections.json")}
    real_swap = swap_refused(monkeypatch)  # (every save below fails before the swap)
    r = api.call("add_word", {"word": "x"})
    assert r == {"words": ["Kubernetes", "GitHub"], "saved": False, "kept": None, "reason": NOT_SAVED, "toast": NOT_SAVED}  # (shows what the file still holds)
    r = api.call("remove_word", {"word": "GitHub"})
    assert r["saved"] is False and r["toast"] == NOT_SAVED and r["words"] == ["Kubernetes", "GitHub"]
    r = api.call("edit_word", {"old": "GitHub", "new": "Git Hub"})
    assert r["saved"] is False and r["reason"] == NOT_SAVED and r["words"] == ["Kubernetes", "GitHub"]
    r = api.call("add_fix", {"heard": "quill pad", "meant": "Quillpad"})
    assert r["added"] is False and r["failed"] is True and r["reason"] == NOT_SAVED and [f["heard"] for f in r["fixes"]] == ["cube ernest"]
    r = api.call("edit_fix", {"old_heard": "cube ernest", "heard": "cube ernest", "meant": "Kube"})
    assert r["saved"] is False and r["failed"] is True and r["reason"] == NOT_SAVED and r["fixes"][0]["meant"] == "Kubernetes"
    r = api.call("delete_fix", {"heard": "cube ernest"})
    assert r["saved"] is False and r["toast"] == NOT_SAVED and [f["heard"] for f in r["fixes"]] == ["cube ernest"]
    assert {name: (isolated / name).read_bytes() for name in before} == before and not list(isolated.glob("*.tmp"))
    # a problem with what was typed is still its own plain reason, not a save failure
    r = api.call("add_fix", {"heard": "same", "meant": "Same"})
    assert r["added"] is False and r["failed"] is False and r["reason"].startswith("The two sides must be different")
    monkeypatch.setattr(light_keys, "_replace_atomic", real_swap)
    r = api.call("edit_word", {"old": "GitHub", "new": "Git Hub"})
    assert r["saved"] is True and r["words"] == ["Kubernetes", "Git Hub"] and r["toast"] == "Saved"
    r = api.call("delete_fix", {"heard": "cube ernest"})
    assert r["saved"] is True and r["fixes"] == [] and r["toast"] == "Saved"


def test_G3_a_damaged_fixes_file_does_not_stop_the_window_and_the_next_fix_keeps_it_first(isolated):
    api = window_api()
    (isolated / "corrections.json").write_bytes(b'[{"heard": "cube ernest", "meant": "Kub')
    (isolated / "words.txt").write_bytes(b"\xff\xfe\x00 not text")
    assert api.call("fixes")["fixes"] == [] and api.call("words")["words"] == [] and api.call("overview")["fixes"] == 0  # (no crash)
    r = api.call("add_fix", {"heard": "quill pad", "meant": "Quillpad"})
    kept = next(isolated.glob("corrections.damaged-*.json"))
    assert r["added"] is True and r["kept"] == kept.name and r["toast"].startswith("Saved. The earlier file couldn't be read, so Chirpy kept it as corrections.damaged-")
    assert kept.read_bytes() == b'[{"heard": "cube ernest", "meant": "Kub' and [f["heard"] for f in r["fixes"]] == ["quill pad"]
    r = api.call("add_word", {"word": "Quillpad"})
    assert r["saved"] is True and r["kept"] and next(isolated.glob("words.damaged-*.txt")).read_bytes() == b"\xff\xfe\x00 not text"


def test_G3_the_teach_box_says_when_a_fix_or_a_word_could_not_be_saved_and_never_says_learned(tool, isolated, monkeypatch):
    monkeypatch.setattr(vt, "bring_to_front", lambda hwnd, tries=10: False)  # (never touches a real window)
    real_swap = swap_refused(monkeypatch)
    tool._learn_typed("cube ernest", "Kubernetes", 0, "cube ernest")
    tool._learn_typed("", "Zyxel", 0, "")
    assert tool.cards == [("Couldn't save", NOT_SAVED)] * 2
    assert corrections.load() == [] and not (isolated / "words.txt").exists() and not list(isolated.glob("*.tmp"))
    monkeypatch.setattr(light_keys, "_replace_atomic", real_swap)
    tool.cards.clear()
    tool._learn_typed("cube ernest", "Kubernetes", 0, "cube ernest")
    assert tool.cards == [("Learned", "From now on, “cube ernest” becomes “Kubernetes”.")]
    assert corrections.load() == [("cube ernest", "Kubernetes")] and (isolated / "words.txt").read_text(encoding="utf-8").endswith("Kubernetes\n")
    # a fix file that could not be read: learned, and the card says where the earlier file went
    tool.cards.clear()
    (isolated / "corrections.json").write_bytes(b"{ cut")
    tool._learn_typed("quill pad", "Quillpad", 0, "quill pad")
    kept = next(isolated.glob("corrections.damaged-*.json")).name
    assert tool.cards == [("Learned", "From now on, “quill pad” becomes “Quillpad”. "
                                      f"The earlier file couldn't be read, so Chirpy kept it as {kept} in its folder.")]


def test_G3_the_copy_card_after_a_fix_that_could_not_be_pasted_no_longer_claims_it_was_learned():
    source = Path(vt.__file__).read_text(encoding="utf-8")
    assert 'kept="Copy the corrected text here.")' in source and "The fix is still learned" not in source


def test_G3_history_lines_are_added_through_the_one_lock_that_clearing_uses(tool, isolated, monkeypatch):
    lines = []
    monkeypatch.setattr(light_files, "append_history", lambda path, line: lines.append((Path(path).name, line)))
    tool._history({"raw": "a", "text": "A."})
    assert [name for name, _ in lines] == ["history.jsonl"] and json.loads(lines[0][1])["text"] == "A." and lines[0][1].endswith("\n")


def test_G1_the_start_failure_hook_is_installed_before_anything_else_is_imported():
    import light_start
    assert sys.excepthook is light_start._hook or vt.light_start is light_start
    source = Path(vt.__file__).read_text(encoding="utf-8")
    assert source.index("import light_start") < source.index("import numpy as np")


# ---------------------------------------------------------------------------------------------- round 2, item 3: per-word window operations

def test_R2_3_window_add_remove_and_edit_apply_to_the_current_file_so_a_word_taught_meanwhile_is_never_lost(tool, isolated, monkeypatch):
    """Codex 675427c7, finding 1: the window sent the whole list it had loaded, so an Add, a Remove or an Edit erased a word
    the Teach box saved after that list was loaded, and both saves said "Saved". Now each is one word, applied to the file
    as it is, under the same lock Teach uses."""
    monkeypatch.setattr(vt, "bring_to_front", lambda hwnd, tries=10: False)
    api = window_api()
    (isolated / "words.txt").write_text(light_files.WORDS_HEADER + "Initial\n", encoding="utf-8")
    loaded = api.call("words")["words"]  # (the window's list)
    tool._learn_typed("", "Background", 0, "")  # (the Teach box saves a word after that)
    r = api.call("add_word", {"word": "Window"})
    assert loaded == ["Initial"] and r["saved"] is True and r["words"] == ["Initial", "Background", "Window"] and r["toast"] == "Saved"
    tool._learn_typed("", "Taught2", 0, "")
    r = api.call("remove_word", {"word": "Initial"})
    assert r["saved"] is True and r["words"] == ["Background", "Window", "Taught2"]
    tool._learn_typed("", "Taught3", 0, "")
    r = api.call("edit_word", {"old": "Window", "new": "Renamed"})
    assert r["saved"] is True and r["words"] == ["Background", "Renamed", "Taught2", "Taught3"] and r["reason"] == ""
    assert (isolated / "words.txt").read_text(encoding="utf-8").startswith(light_files.WORDS_HEADER)  # (the header and order stay)
    # plain reasons, nothing written
    assert api.call("add_word", {"word": "background"}) == {"words": ["Background", "Renamed", "Taught2", "Taught3"], "saved": False, "kept": None,
                                                           "reason": "Already in your list", "toast": "Already in your list"}
    assert api.call("edit_word", {"old": "Renamed", "new": "Taught2"})["reason"] == "That word is already on your list."
    assert api.call("edit_word", {"old": "Renamed", "new": "  "})["reason"] == "Type a word, or use \u2715 to remove it."
    assert api.call("add_word", {"word": "   "})["saved"] is False
    assert api.call("remove_word", {"word": "NotThere"})["saved"] is True  # (already gone: nothing to do)
    # the word a window action names is gone from the file only if it was there; Edit of a word that was removed meanwhile adds the new one
    assert api.call("edit_word", {"old": "Gone", "new": "Fresh"})["words"][-1] == "Fresh"


def test_R2_3_many_window_actions_and_many_teach_saves_at_once_lose_nothing(tool, isolated, monkeypatch):
    import threading
    monkeypatch.setattr(vt, "bring_to_front", lambda hwnd, tries=10: False)
    api = window_api()
    (isolated / "words.txt").write_text(light_files.WORDS_HEADER + "Keep\nDrop\nOld\n", encoding="utf-8")
    jobs = [lambda n=n: tool._learn_typed("", f"Taught{n}", 0, "") for n in range(15)]
    jobs += [lambda n=n: api.call("add_word", {"word": f"Window{n}"}) for n in range(15)]
    jobs += [lambda: api.call("remove_word", {"word": "Drop"}), lambda: api.call("edit_word", {"old": "Old", "new": "New"})]
    threads = [threading.Thread(target=j) for j in jobs]
    for t in threads:
        t.start()
    for t in threads:
        t.join(60)
    words = light_files.read_words(isolated / "words.txt")
    assert {f"Taught{n}" for n in range(15)} | {f"Window{n}" for n in range(15)} | {"Keep", "New"} == set(words)
    assert "Drop" not in words and "Old" not in words and len(words) == len(set(words)) and not list(isolated.glob("*.tmp"))


def test_R2_3_the_window_has_no_whole_list_replacement_left_in_light(built_page=None):
    source = Path(ui_api.__file__).read_text(encoding="utf-8")
    assert "def api_save_words" not in source and "light_files.save_words" not in source
    page = app_window_win.build_page("home")
    assert 'call("save_words"' not in page and 'call("add_word"' in page and 'call("remove_word"' in page and 'call("edit_word"' in page


# ---------------------------------------------------------------------------------------------- round 3, item 1: the displayed row, and a duplicate decided under the lock

def test_R3_1_a_word_with_two_spaces_inside_can_be_removed_and_edited_as_displayed(tool, isolated, monkeypatch):
    """Codex 00bdc9e1: the window named the row "Old  Phrase" as displayed, but the API collapsed the two spaces before looking
    for it, so Remove and Edit reported success and changed nothing (Edit added the new word beside it). Teach can write such a row."""
    monkeypatch.setattr(vt, "bring_to_front", lambda hwnd, tries=10: False)
    api = window_api()
    (isolated / "words.txt").write_text(light_files.WORDS_HEADER + "Keep\nOld  Phrase\nAnother  One\n", encoding="utf-8")
    shown = api.call("words")["words"]
    assert shown == ["Keep", "Old  Phrase", "Another  One"]  # (what the window lists, and sends back)
    r = api.call("remove_word", {"word": shown[1]})
    assert r["saved"] is True and r["words"] == ["Keep", "Another  One"]
    (isolated / "words.txt").write_text(light_files.WORDS_HEADER + "Keep\nOld  Phrase\nAnother  One\n", encoding="utf-8")
    r = api.call("edit_word", {"old": shown[1], "new": "Replacement"})
    assert r["saved"] is True and r["words"] == ["Keep", "Replacement", "Another  One"]
    # Teach itself writes a row like this, and the window can then change it
    tool._learn_typed("", "Two  Words", 0, "")
    assert "Two  Words" in api.call("words")["words"]
    assert api.call("remove_word", {"word": "Two  Words"})["words"] == ["Keep", "Replacement", "Another  One"]


def test_R3_1_add_reports_already_in_your_list_when_teach_saved_that_word_first(tool, isolated, monkeypatch):
    """The window's list was loaded before Teach saved "Zyxel"; Add then decides, under the lock, from the file as it is."""
    monkeypatch.setattr(vt, "bring_to_front", lambda hwnd, tries=10: False)
    api = window_api()
    (isolated / "words.txt").write_text(light_files.WORDS_HEADER + "Keep\n", encoding="utf-8")
    loaded = api.call("words")["words"]
    tool._learn_typed("", "Zyxel", 0, "")  # (saved by Teach after the window loaded its list)
    before = (isolated / "words.txt").read_bytes()
    r = api.call("add_word", {"word": "zyxel"})
    assert loaded == ["Keep"] and r["saved"] is False and r["toast"] == "Already in your list" and r["reason"] == "Already in your list"
    assert r["words"] == ["Keep", "Zyxel"] and (isolated / "words.txt").read_bytes() == before
    # the decision is made inside the lock, not from a read before it: a Teach save landing between the window's own read and
    # its write is still seen
    real = light_files._rewrite
    once = []

    def teach_first(path, make):
        if not once:  # (Teach saves "Racer" just before the window's own save takes the lock)
            once.append(1)
            real(path, lambda raw: ((light_files._decode(raw)[0] + "Racer" + chr(10)).encode("utf-8"), False))
        return real(path, make)
    monkeypatch.setattr(light_files, "_rewrite", teach_first)
    r = api.call("add_word", {"word": "Racer"})
    assert r["saved"] is False and r["toast"] == "Already in your list" and r["words"].count("Racer") == 1
