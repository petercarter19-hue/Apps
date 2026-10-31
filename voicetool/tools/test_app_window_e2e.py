"""End-to-end tests of the app window on this PC: the real window process (Edge WebView2 through pywebview) talking to the
real ui_api.API, and the real VoiceTool started in test mode so it can't collide with the running tool.

Part 1 opens the page hidden and clicks through it with JavaScript (nothing is shown on screen).
Part 2 starts voicetool.py itself from a temporary copy of the code with VOICETOOL_TEST=1: its own single-instance names,
no keyboard hook, no microphone, no speech model, no tray icon, no call watcher, no model server, a Task Scheduler name that
doesn't exist, and every file it writes inside the temporary copy. Nothing touches the real VoiceTool folder or its
Task Scheduler task. The one fallback test briefly shows the old settings window.
Run:  .venv\\Scripts\\python.exe -m pytest -q test_app_window_e2e.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path
from types import SimpleNamespace

import pytest

import app_window_win
import commands
import corrections
import meetings
import meetings_windows
import settings_window
import styles
import ui_api

HERE = Path(__file__).resolve().parent.parent / "src"  # the app code (the tests live in tools\)
PYTHON = sys.executable


def until(check, timeout=15, step=0.1):
    deadline = time.time() + timeout
    while time.time() < deadline:
        value = check()
        if value:
            return value
        time.sleep(step)
    raise AssertionError("timed out waiting for the page")


class Log:
    def __init__(self):
        self.lines = []

    def info(self, msg, *a):
        self.lines.append(msg % a if a else msg)

    warning = error = info

    def exception(self, *a):
        self.lines.append("EXC " + str(a))


class FakeHost:
    """Records what the page asked the PC to do; the real WinHost is tested in test_voicetool.py."""

    def __init__(self):
        self.calls = []
        self.recording = False
        self.writing = False
        self.signed_in = True
        self.picked = "C:/Picked/Notes"
        self.model_missing = True
        self.speech_missing = False
        self.offline_ready = False  # (the tidy-up model is downloaded)

    def _rec(self, name, *args):
        self.calls.append((name, *args))

    def computer_name(self): return "E2E-PC"
    def claude_signed_in(self): return self.signed_in
    def paused(self, p): self._rec("paused", p)
    def startup_enabled(self): return True
    def set_startup(self, on): self._rec("set_startup", on)
    def stop_claude(self): self._rec("stop_claude")
    def refresh_style(self): self._rec("refresh_style")
    def open_path(self, path): self._rec("open_path", Path(path).name)
    def copy_text(self, text): self._rec("copy_text", text)
    def quit(self): self._rec("quit")
    def start_meeting(self): self._rec("start_meeting"); self.recording = True
    def stop_meeting(self): self._rec("stop_meeting"); self.recording, self.writing = False, True
    def open_meeting_folder(self): self._rec("open_meeting_folder")
    def offline_available(self): return self.offline_ready
    def list_voices(self): return [{"name": "Microsoft Zira Desktop", "lang": ""}, {"name": "Microsoft David Desktop", "lang": ""}]
    def note_destinations(self): return ["folder"]
    def choose_folder(self, start=""): self._rec("choose_folder", start); return self.picked
    def read_aloud(self, text): self._rec("read_aloud", text)

    def setup_status(self): return {"missing": self.model_missing or self.speech_missing, "speech_missing": self.speech_missing,
                                    "running": False, "failed": False}

    def finish_setup(self):
        self._rec("finish_setup")
        return {"started": True}

    def meeting_status(self):
        return {"recording": self.recording, "seconds": 75 if self.recording else 0,
                "app": "Zoom" if self.recording else "", "writing": self.writing}

    def called(self, name):
        return [c for c in self.calls if c[0] == name]


@pytest.fixture(scope="module")
def window():
    tmp = Path(tempfile.mkdtemp(prefix="vt_e2e_window_"))
    mp = pytest.MonkeyPatch()
    for module in (meetings, ui_api, styles, commands):
        if hasattr(module, "HERE"):
            mp.setattr(module, "HERE", tmp)
    mp.setattr(ui_api, "HISTORY", tmp / "history.jsonl")
    mp.setattr(ui_api, "WORDS", tmp / "words.txt")
    mp.setattr(corrections, "FILE", tmp / "corrections.json")  # never the real learned fixes
    mp.setattr(corrections, "PENDING", tmp / "corrections_pending.json")
    mp.setattr(corrections, "_cache", {"mtime": None, "pairs": []})
    mp.setattr(styles, "FILE", tmp / "styles.json")
    mp.setattr(styles, "_cache", {"mtime": None, "data": None})
    mp.setattr(commands, "STYLE_FILE", tmp / "style.md")
    mp.setenv("OneDrive", str(tmp / "OneDrive"))  # (a temp "OneDrive" that meeting notes must never use)
    mp.setattr(meetings_windows, "notes_base", lambda: tmp / "Documents")  # the Word documents go to a temp Documents, never the real one
    today = time.strftime("%Y-%m-%d")
    (tmp / "words.txt").write_text("Zephyr\nClaude\n")
    (tmp / "history.jsonl").write_text("\n".join(json.dumps(r) for r in [
        {"at": f"{today} 09:00:00", "raw": "hello um test", "text": "Hello, test.", "app": "OUTLOOK.EXE"},
        {"at": f"{today} 09:05:00", "command": "shorter", "route": "rewrite", "app": "chrome.exe", "result": "Hi"}]) + "\n")
    notes = {"title": "Budget review", "summary": "We agreed.", "decisions": ["Keep Q4 flat"],
             "action_items": [{"owner": "Me", "task": "Send numbers", "due": "Friday"}], "open_questions": [],
             "follow_up_email": "Hi all,\nThanks."}
    doc = meetings.write_document(notes, [(0, "Me", "hello"), (9, "Others", "hi")], time.time() - 3600, time.time() - 1800)
    with open(tmp / "history.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps({"at": f"{today} 10:00:00", "meeting": "Budget review", "notes": str(doc), "minutes": 30}) + "\n")
        f.write(json.dumps({"at": f"{today} 08:00:00", "meeting": "Moved away", "notes": str(doc.parent / "gone.docx"), "minutes": 5}) + "\n")
    saved = []
    config = {"enabled": True, "use_style": True, "meeting_followup": True, "translate_to": "", "claude_keep_running": True,
              "read_voice": "", "read_rate": "normal", "notes_destination": "folder", "notes_folder": "", "first_run": True,
              "meeting_notes": False, "claude_actions": False, "offline_only": True, "azure_openai_base_url": ""}
    host = FakeHost()
    api = ui_api.API(host, config, lambda c: saved.append(dict(c)))
    log = Log()
    failures = []
    w = app_window_win.AppWindow(api, log, on_failed=failures.append, test_mode="hidden")
    w.show("home")
    until(lambda: w.ready or failures, 40)
    assert w.ready, failures
    w.t = SimpleNamespace(tmp=tmp, host=host, config=config, saved=saved, doc=doc, log=log, failures=failures)
    yield w
    w.close()
    mp.undo()
    shutil.rmtree(tmp, ignore_errors=True)


def js(w, expr):
    return w.evaluate(expr)


def go(w, page):
    w.show(page)
    until(lambda: js(w, f"document.querySelector('#page-{page}').classList.contains('on')"))
    time.sleep(0.3)


def test_the_window_starts_on_the_requested_page_and_the_pc_bridge_works(window):
    assert js(window, "location.hash") == "#home"
    until(lambda: js(window, "document.querySelector('#navstatus').textContent") == "On and listening for keys")
    assert js(window, "document.querySelector('#computer').textContent") == "E2E-PC"
    assert js(window, "typeof window.pywebview.api.call") == "function"
    assert js(window, "document.querySelector('#s-dict').textContent") == "1"
    assert js(window, "document.querySelector('#s-cmd').textContent") == "1"
    assert "Hello, test." in js(window, "document.querySelector('#recent').textContent")


def test_the_first_start_shows_the_welcome_card_and_closing_it_clears_first_run(window):
    go(window, "home")
    until(lambda: js(window, "!!document.querySelector('#welcome .welcome')"))
    text = js(window, "document.querySelector('#welcome').textContent")
    for phrase in ("Hold Left Alt", "Let go", "Double-tap Left Alt", "hands-free", "Hold Left Ctrl", "make this shorter",
                   "open Notepad", "note: buy milk", "Shift + tap Left Ctrl", "Ctrl works as usual", "Everything stays on this computer",
                   "first start takes about 10 seconds"):
        assert phrase in text, phrase
    assert window.t.config["first_run"] is True
    js(window, "document.querySelector('#welcome [data-welcome-close].btn').click()")
    until(lambda: window.t.config["first_run"] is False)
    assert window.t.saved[-1]["first_run"] is False
    until(lambda: js(window, "document.querySelector('#welcome').textContent") == "")
    go(window, "history")
    go(window, "home")  # and it stays gone
    time.sleep(0.5)
    assert js(window, "document.querySelector('#welcome').textContent") == ""


def test_finish_setup_button_shows_while_the_model_is_missing_and_clicking_asks_the_tool_to_download(window):
    host = window.t.host
    host.model_missing = True
    go(window, "home")
    until(lambda: js(window, "!!document.querySelector('#setup [data-finish-setup]')"))
    assert js(window, "document.querySelector('#setup [data-finish-setup]').textContent") == "Finish setup: download the tidy-up model"
    assert "typed as you said them" in js(window, "document.querySelector('#setup').textContent")
    js(window, "document.querySelector('#setup [data-finish-setup]').click()")
    until(lambda: host.called("finish_setup"))
    host.model_missing = False  # (the download finished)
    window.show("home")
    until(lambda: js(window, "document.querySelector('#setup').textContent") == "")
    host.model_missing = True


def test_a_missing_speech_model_says_so_on_home_and_offers_the_same_finish_setup_button(window):
    host = window.t.host
    host.model_missing, host.speech_missing = False, True
    go(window, "home")
    until(lambda: js(window, "!!document.querySelector('#setup [data-finish-setup]')"))
    text = js(window, "document.querySelector('#setup').textContent")
    assert "The speech model is missing" in text and "can't turn your voice into text" in text and "typed as you said them" not in text
    assert js(window, "document.querySelector('#setup [data-finish-setup]').textContent") == "Finish setup: download what's missing"
    host.model_missing, host.speech_missing = True, False  # (back to the module's usual state)
    go(window, "home")


def test_every_page_opens_and_loads_its_data(window):
    for page in ("history", "words", "style", "styles", "commands", "meetings", "settings", "home"):
        go(window, page)
        assert js(window, "document.querySelector('.page.on').id") == f"page-{page}"
    assert window.t.failures == []
    # the Transcribe and Spending pages are not part of this version: no page, no menu entry
    assert js(window, "document.querySelector('#page-transcribe') === null && document.querySelector('#page-spending') === null")
    assert js(window, "[...document.querySelectorAll('nav button[data-page]')].map(b => b.dataset.page)") == [
        "home", "history", "words", "style", "styles", "commands", "meetings", "settings"]


def test_history_search_and_filters(window):
    go(window, "history")
    until(lambda: "Hello, test." in js(window, "document.querySelector('#historylist').textContent"))
    js(window, "(() => { const q = document.querySelector('#q'); q.value = 'shorter'; q.dispatchEvent(new Event('input')); })()")
    until(lambda: "Hello, test." not in js(window, "document.querySelector('#historylist').textContent"))
    assert "Hi" in js(window, "document.querySelector('#historylist').textContent")


def test_settings_page_lists_every_feature_with_the_pc_meetings_rows(window):
    go(window, "settings")
    until(lambda: js(window, "document.querySelectorAll('#featurelist .feature').length") == len(ui_api.FEATURES))
    text = js(window, "document.querySelector('#featurelist').textContent")
    for title in ("Meeting notes", "Offer when a call starts", "Follow-up email draft", "Spoken answers", "Keep everything on this computer"):
        assert title in text
    assert "Icon menu › Start meeting notes" in text and "isn't downloaded yet" in text  # (the fake host has no model)
    assert "Claude Code installed and signed in on your own Claude plan" in text and "Off until you turn it on" in text
    assert "Offline tidy-up" not in text and "Left Option" not in text and "Left Command" not in text
    assert "Hold or double-tap Left Alt" in text and "Hold or double-tap Left Ctrl" in text and "Shift + tap Left Ctrl" in text
    assert "Left Windows" not in text and "Windows key" not in text
    assert js(window, "document.querySelector('#startup').checked") is True
    assert "Windows" not in js(window, "document.querySelector('#translate').textContent")
    assert js(window, "document.querySelectorAll('#translate option').length") == len(ui_api.LANGUAGES) + 1


def test_read_it_to_me_lists_the_windows_voices_and_saves_the_choice(window):
    go(window, "settings")
    until(lambda: js(window, "document.querySelectorAll('#readvoice option').length") == 3)
    options = js(window, "[...document.querySelectorAll('#readvoice option')].map(o => o.textContent)")
    assert options == ["The Windows default voice", "Microsoft Zira Desktop", "Microsoft David Desktop"]  # not "The Mac's"
    text = js(window, "document.querySelector('#readvoice').closest('.feature').textContent")
    assert "built into Windows" in text and "System Settings" not in text and "Mac" not in text
    js(window, "(() => { const s = document.querySelector('#readvoice'); s.value = 'Microsoft Zira Desktop'; s.dispatchEvent(new Event('change')); })()")
    until(lambda: window.t.config["read_voice"] == "Microsoft Zira Desktop")
    js(window, "(() => { const s = document.querySelector('#readrate'); s.value = 'fast'; s.dispatchEvent(new Event('change')); })()")
    until(lambda: window.t.config["read_rate"] == "fast")
    js(window, "document.querySelector('#tryvoice').click()")
    until(lambda: window.t.host.called("read_aloud"))
    assert "This is how VoiceTool will read to you" in window.t.host.called("read_aloud")[-1][1]
    go(window, "home")
    go(window, "settings")  # drawn again on every visit, with the saved choices selected
    until(lambda: js(window, "document.querySelector('#readvoice').value") == "Microsoft Zira Desktop")
    assert js(window, "document.querySelector('#readrate').value") == "fast"
    assert js(window, "[...document.querySelectorAll('#readvoice option')].map(o => o.textContent)[0]") == "The Windows default voice"


def test_voice_notes_go_to_a_folder_picked_with_the_windows_dialog(window):
    go(window, "settings")
    until(lambda: js(window, "document.querySelectorAll('#notedest option').length") == 1)
    assert js(window, "[...document.querySelectorAll('#notedest option')].map(o => o.textContent)") == [
        "A folder (Markdown, Obsidian-ready)"]  # (no Big Brain, no Apple Notes, no OneNote)
    assert js(window, "getComputedStyle(document.querySelector('#notefolderrow')).display") != "none"
    until(lambda: js(window, "document.querySelector('#notefolder').value").endswith("Voice notes"))  # the default: Documents \u203a Voice notes
    js(window, "document.querySelector('#choosefolder').click()")
    until(lambda: window.t.host.called("choose_folder"))
    until(lambda: window.t.config["notes_folder"] == "C:/Picked/Notes")
    # (the setting is saved before the page has drawn the answer: wait for the box to show it)
    until(lambda: js(window, "document.querySelector('#notefolder').value") == "C:/Picked/Notes")
    before = len(window.t.host.called("open_path"))
    js(window, "document.querySelector('#opennotes').click()")
    until(lambda: len(window.t.host.called("open_path")) > before)
    assert window.t.host.called("open_path")[-1] == ("open_path", "Notes")


def test_commands_page_lists_what_this_version_does_and_nothing_personal(window):
    go(window, "commands")
    text = js(window, "document.querySelector('#cmdhelp').textContent")
    assert "Apple Notes" not in text and "Big Brain" not in text and "Safari" not in text and "Handoffs" not in text
    assert "Control the computer" in text and "Read it to me" in text and "open Notepad" in text.replace("Open Notepad", "open Notepad")
    for gone in ("Transcribe a link", "About your screen", "Tell your AI team"):
        assert gone not in text
    assert "needs Claude Code" in text and "it always asks first" in text
    assert "Left Option" not in text and "Left Command" not in text and "Left Ctrl" in js(window, "document.querySelector('#page-commands').textContent")
    assert "Left Windows" not in js(window, "document.querySelector('#page-commands').textContent")


def test_the_window_can_ask_for_a_folder_dialog_and_get_the_answer(monkeypatch):
    """The real round trip: the tool asks, the real window process answers (in test mode without showing a dialog)."""
    monkeypatch.setenv("VOICETOOL_TEST_FOLDER", "C:/Test/Chosen folder")
    failures = []
    w = app_window_win.AppWindow(SimpleNamespace(call=lambda name, args: {}), Log(), on_failed=failures.append, test_mode="hidden")
    try:
        w.show("settings")
        until(lambda: w.ready or failures, 40)
        assert w.ready, failures
        assert w.choose_folder("C:/Start") == "C:/Test/Chosen folder"
        assert w.choose_folder("") == "C:/Test/Chosen folder"  # (and again: each ask has its own id)
    finally:
        w.close()
    assert w.choose_folder("C:/Start") == ""  # the window is gone: an empty answer, no hang


def test_clicking_a_switch_saves_it(window):
    go(window, "settings")
    until(lambda: js(window, "!!document.querySelector('[data-key=meeting_followup]')"))
    js(window, "document.querySelector('[data-key=meeting_followup]').click()")
    until(lambda: window.t.config["meeting_followup"] is False)
    assert window.t.saved[-1]["meeting_followup"] is False
    js(window, "document.querySelector('[data-key=meeting_followup]').click()")
    until(lambda: window.t.config["meeting_followup"] is True)
    js(window, "document.querySelector('[data-key=claude_keep_running]').click()")
    until(lambda: window.t.host.called("stop_claude"))
    js(window, "document.querySelector('[data-key=claude_keep_running]').click()")


def test_turning_meeting_notes_on_shows_the_reminder_and_cancel_keeps_it_off(window):
    go(window, "settings")
    until(lambda: js(window, "!!document.querySelector('[data-key=meeting_notes]')"))
    assert window.t.config["meeting_notes"] is False
    js(window, "document.querySelector('[data-key=meeting_notes]').click()")
    until(lambda: js(window, "!!document.querySelector('.modal')"))
    assert "Before recording, make sure everyone on the call agrees to be recorded." in js(window, "document.querySelector('.modal').textContent")
    assert window.t.config["meeting_notes"] is False  # nothing saved while the question is open
    js(window, "document.querySelector('.modal [data-no]').click()")
    until(lambda: not js(window, "!!document.querySelector('.modal')"))
    assert js(window, "document.querySelector('[data-key=meeting_notes]').checked") is False and window.t.config["meeting_notes"] is False
    js(window, "document.querySelector('[data-key=meeting_notes]').click()")
    until(lambda: js(window, "!!document.querySelector('.modal')"))
    js(window, "document.querySelector('.modal [data-yes]').click()")
    until(lambda: window.t.config["meeting_notes"] is True)
    assert window.t.saved[-1]["meeting_notes"] is True
    js(window, "document.querySelector('[data-key=meeting_notes]').click()")  # turning it off asks nothing
    until(lambda: window.t.config["meeting_notes"] is False)
    assert not js(window, "!!document.querySelector('.modal')")


def test_the_offline_switch_cannot_be_turned_off_without_an_azure_endpoint(window):
    go(window, "settings")
    until(lambda: js(window, "!!document.querySelector('[data-key=offline_only]')"))
    assert js(window, "document.querySelector('[data-key=offline_only]').checked") is True
    js(window, "document.querySelector('[data-key=offline_only]').click()")
    until(lambda: "Azure isn't set up" in js(window, "document.querySelector('#toast').textContent"))
    until(lambda: js(window, "document.querySelector('[data-key=offline_only]').checked") is True)  # put back
    assert window.t.config["offline_only"] is True


def test_translate_dropdown_and_startup_switch(window):
    go(window, "settings")
    until(lambda: js(window, "!!document.querySelector('#translate option[value=Spanish]')"))
    js(window, "(() => { const s = document.querySelector('#translate'); s.value = 'Spanish'; s.dispatchEvent(new Event('change')); })()")
    until(lambda: window.t.config["translate_to"] == "Spanish")
    js(window, "(() => { const s = document.querySelector('#translate'); s.value = ''; s.dispatchEvent(new Event('change')); })()")
    until(lambda: window.t.config["translate_to"] == "")
    js(window, "document.querySelector('#startup').click()")
    until(lambda: window.t.host.called("set_startup"))
    assert window.t.host.called("set_startup")[-1] == ("set_startup", False)


def test_pause_switch_on_home(window):
    go(window, "home")
    js(window, "document.querySelector('#pause').click()")
    until(lambda: window.t.host.called("paused"))
    assert window.t.host.called("paused")[-1] == ("paused", True) and window.t.config["enabled"] is False
    until(lambda: js(window, "document.querySelector('#herotitle').textContent") == "VoiceTool is paused")
    js(window, "document.querySelector('#pause').click()")
    until(lambda: window.t.config["enabled"] is True)


def test_meetings_page_says_both_sides_are_recorded_and_lists_the_meetings(window):
    go(window, "meetings")
    text = js(window, "document.querySelector('#page-meetings').textContent")
    assert "Records you and the call" in text and "Meeting notes folder" in text and "OneDrive" not in text
    assert "VoiceTool icon by the clock" in text and "menu bar" not in text and "agrees to be recorded" in text
    until(lambda: "Meeting notes are off" in js(window, "document.querySelector('#meetoff').textContent"))
    until(lambda: "Budget review" in js(window, "document.querySelector('#meetinglist').textContent"))
    listing = js(window, "document.querySelector('#meetinglist').textContent")
    assert "Moved away" in listing and "File moved" in listing and "Ready" in listing and "Open in Word" in listing
    assert "Draft follow-up email" not in listing  # (the follow-up is drafted automatically)
    assert "Meeting" in js(window, "document.querySelector('nav').textContent")


def test_history_shows_meetings_with_a_meeting_badge(window):
    js(window, "(() => { const q = document.querySelector('#q'); q.value = ''; q.dispatchEvent(new Event('input')); })()")
    go(window, "history")
    until(lambda: "Budget review" in js(window, "document.querySelector('#historylist').textContent"))
    assert js(window, "document.querySelectorAll('#historylist .b-meeting').length") == 2


def test_start_stop_the_live_clock_and_the_writing_state(window):
    go(window, "meetings")
    js(window, "document.querySelector('#mstart').click()")
    until(lambda: window.t.host.called("start_meeting"))
    until(lambda: "card at the bottom right" in js(window, "document.querySelector('#toast').textContent"))
    until(lambda: js(window, "getComputedStyle(document.querySelector('#mstop')).display") != "none")
    assert js(window, "document.querySelector('#mclock').textContent") == "1:15"
    assert js(window, "document.querySelector('#mname').textContent") == "Taking notes · Zoom"
    js(window, "document.querySelector('#mstop').click()")
    until(lambda: window.t.host.called("stop_meeting"))
    window.show("meetings")  # (the page checks again when it is shown)
    until(lambda: js(window, "document.querySelector('#liveidle b').textContent") == "Writing your meeting notes…")
    assert js(window, "getComputedStyle(document.querySelector('#mstart')).display") == "none"
    window.t.host.writing = False
    window.show("meetings")
    until(lambda: js(window, "document.querySelector('#liveidle b').textContent") == "Not recording")
    until(lambda: js(window, "getComputedStyle(document.querySelector('#mstart')).display") != "none")


def test_notes_buttons_show_the_notes_from_the_word_document_and_open_word_and_the_folder(window):
    go(window, "meetings")
    until(lambda: js(window, "!!document.querySelector('[data-m=toggle]')"))
    js(window, "document.querySelector('[data-m=toggle]').click()")
    until(lambda: "Keep Q4 flat" in js(window, "document.querySelector('.meeting.open .notes').textContent"))
    notes = js(window, "document.querySelector('.meeting.open .notes').textContent")
    assert "Send numbers" in notes and "Summary" in notes and "Transcript" in notes and "Others: hi" in notes
    before = len(window.t.host.called("open_path"))
    js(window, "document.querySelector('[data-m=open]').click()")
    until(lambda: len(window.t.host.called("open_path")) > before)
    assert window.t.host.called("open_path")[-1] == ("open_path", window.t.doc.name)
    js(window, "document.querySelector('#mfolder').click()")
    until(lambda: window.t.host.called("open_meeting_folder"))


def test_an_error_from_the_tool_shows_as_a_toast_not_a_frozen_page(window):
    go(window, "settings")
    js(window, "call('open', { what: 'nonsense' }).catch(() => {})")
    until(lambda: "Couldn't do that" in js(window, "document.querySelector('#toast').textContent"))
    assert "nonsense" in js(window, "document.querySelector('#toast').textContent")
    go(window, "home")  # and the window still works afterwards
    until(lambda: js(window, "document.querySelector('#navstatus').textContent") != "Loading…")


def test_commands_page_has_pc_wording_and_windows_shortcut_kind(window):
    go(window, "commands")
    until(lambda: js(window, "document.querySelectorAll('#shortcutlist tr').length") >= 1)
    text = js(window, "document.querySelector('#page-commands').textContent")
    assert "open Edge" in text and "Safari" not in text and "Apple Shortcut" not in text and "Windows shortcut (.lnk)" in text
    kinds = js(window, "[...document.querySelectorAll('#shortcutlist select[data-f=kind] option')].map(o => o.textContent).slice(0, 4)")
    assert kinds == ["Website", "App", "File", "Windows shortcut"]
    js(window, "document.querySelector('#addshortcut').click()")
    until(lambda: js(window, "document.querySelectorAll('#shortcutlist option[value=shortcut]').length") >= 2)
    assert set(js(window, "[...document.querySelectorAll('#shortcutlist option[value=shortcut]')].map(o => o.textContent)")) == {"Windows shortcut"}


def test_styles_page_adds_a_rule_and_saves_it(window):
    go(window, "styles")
    until(lambda: js(window, "document.querySelectorAll('#rulelist tr').length") >= 4)
    rules = js(window, "[...document.querySelectorAll('#rulelist [data-f=apps]')].map(i => i.value).join('|')")
    assert "WindowsTerminal.exe" in rules and "OUTLOOK.EXE" in rules and "ms-teams.exe" in rules
    assert "chrome.exe" in js(window, "document.querySelector('#recentapps').textContent")  # recent apps come from the history
    js(window, "document.querySelector('#addrule').click()")
    js(window, "(() => { const rows = document.querySelectorAll('#rulelist tr'); rows[rows.length - 1].querySelector('[data-f=apps]').value = 'msedge.exe'; "
               "rows[rows.length - 1].querySelector('[data-f=mode]').value = 'raw'; })()")
    js(window, "document.querySelector('#savestyles').click()")
    until(lambda: (window.t.tmp / "styles.json").exists())
    saved = json.loads((window.t.tmp / "styles.json").read_text())
    assert {"apps": "msedge.exe", "mode": "raw"} in saved["app_rules"]


def test_words_page_adds_and_removes_a_word_and_a_fix(window):
    go(window, "words")
    js(window, "(() => { document.querySelector('#newword').value = 'Kubernetes'; document.querySelector('#addword').click(); })()")
    until(lambda: "Kubernetes" in (window.t.tmp / "words.txt").read_text())
    until(lambda: "Kubernetes" in js(window, "document.querySelector('#wordlist').textContent"))


def press(w, selector, key):
    js(w, f"document.querySelector('{selector}').dispatchEvent(new KeyboardEvent('keydown', {{key: '{key}', bubbles: true}}))")


def type_into(w, selector, text):
    js(w, f"document.querySelector('{selector}').value = {json.dumps(text)}")


def test_words_page_edits_a_fix_in_place_with_save_cancel_enter_and_esc(window):
    fixes = window.t.tmp / "corrections.json"
    go(window, "words")
    type_into(window, "#fixheard", "zef fir"); type_into(window, "#fixmeant", "Zephyr")
    js(window, "document.querySelector('#addfix').click()")
    until(lambda: "zef fir" in js(window, "document.querySelector('#fixlist').textContent"))
    def rows():
        try:
            return [(r["heard"], r["meant"]) for r in json.loads(fixes.read_text(encoding="utf-8"))]
        except json.JSONDecodeError:
            return None  # (read while the window was still writing the file: the next look sees it whole)

    js(window, "document.querySelector('#fixlist [data-edit-fix]').click()")  # clicking "It wrote" opens the row
    until(lambda: js(window, "!!document.querySelector('#editheard')"))
    assert js(window, "document.querySelector('#editheard').value") == "zef fir"
    assert js(window, "document.querySelector('#editmeant').value") == "Zephyr"
    type_into(window, "#editheard", "zef fur"); press(window, "#editheard", "Escape")  # Esc cancels
    until(lambda: not js(window, "!!document.querySelector('#editheard')"))
    assert rows() == [("zef fir", "Zephyr")]

    js(window, "document.querySelector('#fixlist button[title=Edit]').click()")  # the pencil opens it too
    until(lambda: js(window, "!!document.querySelector('#editmeant')"))
    type_into(window, "#editheard", "zef fur"); type_into(window, "#editmeant", "Zephyr Inc")
    press(window, "#editmeant", "Enter")  # Enter saves both sides
    until(lambda: rows() == [("zef fur", "Zephyr Inc")])
    until(lambda: not js(window, "!!document.querySelector('#editheard')"))
    assert "Zephyr Inc" in js(window, "document.querySelector('#fixlist').textContent")

    js(window, "document.querySelector('#fixlist [data-edit-fix]').click()")
    until(lambda: js(window, "!!document.querySelector('#editmeant')"))
    type_into(window, "#editheard", "one two three four five six")
    js(window, "document.querySelector('[data-save-fix]').click()")  # refused: it stays open and says why
    until(lambda: "5 words at most" in js(window, "document.querySelector('#toast').textContent"))
    assert js(window, "!!document.querySelector('#editheard')") and rows() == [("zef fur", "Zephyr Inc")]
    js(window, "document.querySelector('[data-cancel-fix]').click()")
    until(lambda: not js(window, "!!document.querySelector('#editheard')"))

    js(window, "document.querySelector('#fixlist [data-forget]').click()")  # forgetting still works
    until(lambda: rows() == [])


def test_words_page_edits_a_word_in_place_with_save_cancel_enter_and_esc(window):
    words = lambda: [w for w in (window.t.tmp / "words.txt").read_text(encoding="utf-8").splitlines() if not w.startswith("#")]
    go(window, "words")
    until(lambda: "Claude" in js(window, "document.querySelector('#wordlist').textContent"))
    open_claude = "[...document.querySelectorAll('#wordlist [data-edit-word]')].find(e => e.textContent === %s).click()"
    js(window, open_claude % "'Claude'")
    until(lambda: js(window, "!!document.querySelector('#editword')"))
    assert js(window, "document.querySelector('#editword').value") == "Claude"
    type_into(window, "#editword", "Claud"); press(window, "#editword", "Escape")
    until(lambda: not js(window, "!!document.querySelector('#editword')"))
    assert "Claude" in words() and "Claud" not in words()

    js(window, open_claude % "'Claude'")
    until(lambda: js(window, "!!document.querySelector('#editword')"))
    type_into(window, "#editword", "zephyr")  # already on the list: refused, says why
    js(window, "document.querySelector('[data-save-word]').click()")
    until(lambda: "already on your list" in js(window, "document.querySelector('#toast').textContent"))
    type_into(window, "#editword", "Claude Code"); press(window, "#editword", "Enter")
    until(lambda: "Claude Code" in words())
    assert "Claude" not in words() and words().index("Claude Code") == words().index("Zephyr") + 1  # same place
    until(lambda: not js(window, "!!document.querySelector('#editword')"))
    js(window, open_claude % "'Claude Code'")
    until(lambda: js(window, "!!document.querySelector('#editword')"))
    js(window, "document.querySelector('[data-cancel-word]').click()")
    until(lambda: not js(window, "!!document.querySelector('#editword')"))


def test_the_style_page_shows_the_neutral_description_and_hides_relearn_unless_claude_is_available(window):
    go(window, "style")
    until(lambda: "In my own words" in js(window, "document.querySelector('#styletext').textContent"))
    assert "Using your own description" in js(window, "document.querySelector('#stylestate').textContent")
    assert js(window, "getComputedStyle(document.querySelector('#relearn')).display") == "none"  # Claude is off
    window.t.config["claude_actions"] = True
    window.t.host.signed_in = True
    go(window, "home")
    window.t.host._rec("noop")
    go(window, "style")
    until(lambda: js(window, "getComputedStyle(document.querySelector('#relearn')).display") != "none")
    window.t.host.signed_in = False  # signed out: hidden again
    go(window, "home")
    go(window, "style")
    until(lambda: js(window, "getComputedStyle(document.querySelector('#relearn')).display") == "none")
    window.t.config["claude_actions"] = False
    window.t.host.signed_in = True


def test_the_home_page_says_claude_is_missing_only_when_it_was_turned_on(window):
    go(window, "home")
    until(lambda: js(window, "document.querySelector('#navstatus').textContent") != "Loading…")
    assert "Claude Code" not in js(window, "document.querySelector('#notices').textContent")  # off: nothing said
    window.t.config["claude_actions"] = True
    window.t.host.signed_in = False
    window.show("home")
    until(lambda: "Email and calendar need Claude Code" in js(window, "document.querySelector('#notices').textContent"))
    window.t.config["claude_actions"] = False
    window.t.host.signed_in = True
    window.show("home")
    until(lambda: "Claude Code" not in js(window, "document.querySelector('#notices').textContent"))


def test_home_key_labels_use_the_short_wording_and_each_fits_on_one_line(window):
    go(window, "home")
    labels = js(window, "[...document.querySelectorAll('#page-home .gesture .k')].map(k => k.textContent)")
    assert labels == ["Hold Left Alt", "Double-tap Left Alt", "Hold Left Ctrl", "Shift + tap Left Ctrl"]
    fit = js(window, """[...document.querySelectorAll('#page-home .gesture .k')].map(k => {
        const r = k.getBoundingClientRect(), card = k.closest('.card').getBoundingClientRect();
        return getComputedStyle(k).whiteSpace === 'nowrap' && r.height < 26 && r.right <= card.right - 8; })""")
    assert fit == [True, True, True, True]


def test_today_counts_the_fixes_made_for_you_and_shows_zero_without_any(window):
    history = window.t.tmp / "history.jsonl"
    before = history.read_text(encoding="utf-8")
    try:
        go(window, "home")
        assert js(window, "document.querySelector('#s-fixes').closest('.stat').querySelector('.l').textContent") == "fixes made for you"
        assert len(js(window, "[...document.querySelectorAll('#page-home .stat')].map(s => s.textContent)")) == 4
        until(lambda: js(window, "document.querySelector('#s-fixes').textContent") == "0")  # rows from before the counter: none
        today = time.strftime("%Y-%m-%d")
        with open(history, "a", encoding="utf-8") as f:
            f.write(json.dumps({"at": f"{today} 11:00:00", "raw": "zef fur", "text": "Zephyr", "fixes": 4}) + "\n")
            f.write(json.dumps({"at": "2020-01-01 11:00:00", "raw": "zef fur", "text": "Zephyr", "fixes": 9}) + "\n")  # not today
        window.show("home")
        until(lambda: js(window, "document.querySelector('#s-fixes').textContent") == "4")
    finally:
        history.write_text(before, encoding="utf-8")
    window.show("home")
    until(lambda: js(window, "document.querySelector('#s-fixes').textContent") == "0")


def pc_card(w):
    return js(w, "document.querySelector('#pccard').textContent.replace(/\\s+/g, ' ')")


def test_runs_on_your_pc_card_tells_the_truth_for_each_state(window):
    config, host = window.t.config, window.t.host
    go(window, "home")
    until(lambda: "Runs on your PC" in pc_card(window))
    # the default: everything on this computer, no online setup, the tidy-up model not downloaded yet
    text = pc_card(window)
    assert "Nothing you say leaves this computer." in text and "Stays on this PC" in text and "Goes online" in text
    assert "Voice to text" in text and "Dictation tidy-up" in text and "History, words and fixes" in text and "Nothing goes online" in text
    assert "isn't downloaded yet" in text and "Azure" not in text.replace("Azure endpoint", "")
    assert js(window, "document.querySelector('#pckeep')") is None  # no switch: there is no Azure endpoint to turn it off for
    # paused: the card is still there
    js(window, "document.querySelector('#pause').click()")
    until(lambda: js(window, "document.querySelector('#herotitle').textContent") == "VoiceTool is paused")
    assert "Runs on your PC" in pc_card(window)
    js(window, "document.querySelector('#pause').click()")
    until(lambda: config["enabled"] is True)
    # email and calendar on: the one thing that goes online while dictation stays here
    config["claude_actions"] = True
    window.show("home")
    until(lambda: "Email and calendar requests (Claude Code)" in pc_card(window))
    text = pc_card(window)
    assert "What you dictate never leaves this computer." in text and "Nothing goes online" not in text
    config["claude_actions"] = False
    # an Azure endpoint is set: now there is a switch, and it really turns the setting off and on
    config["azure_openai_base_url"] = "https://example.invalid/openai/v1/"
    host.offline_ready = True
    try:
        window.show("home")
        until(lambda: js(window, "!!document.querySelector('#pckeep')"))
        assert js(window, "document.querySelector('#pckeep').checked") is True and "run on this PC" in pc_card(window)
        js(window, "document.querySelector('#pckeep').click()")
        until(lambda: config["offline_only"] is False)
        until(lambda: "Dictation tidy-up (Azure OpenAI)" in pc_card(window))
        text = pc_card(window)
        assert "Commands (Azure OpenAI)" in text and "Meeting summaries (Azure OpenAI)" in text and "Nothing goes online" not in text
        assert "Your voice becomes text right here." in text and js(window, "document.querySelector('#pckeep').checked") is False
        js(window, "document.querySelector('#pckeep').click()")
        until(lambda: config["offline_only"] is True)
        until(lambda: "Nothing you say leaves this computer." in pc_card(window))
        assert "Nothing goes online" in pc_card(window)
    finally:
        config["offline_only"], config["azure_openai_base_url"] = True, ""
        host.offline_ready = False


def test_words_and_fixes_search_filters_both_lists_and_says_when_nothing_matches(window):
    fixes_file = window.t.tmp / "corrections.json"
    before = fixes_file.read_text(encoding="utf-8") if fixes_file.exists() else None
    corrections.add("zef fur", "Zephyr Inc", "test")
    corrections.add("cube", "Kubernetes", "test")
    try:
        go(window, "words")
        until(lambda: "zef fur" in js(window, "document.querySelector('#fixlist').textContent"))
        shown = lambda sel: js(window, f"document.querySelector('{sel}').textContent")
        assert "Claude" in shown("#wordlist") and "cube" in shown("#fixlist")
        type_into(window, "#wq", "zeph")
        js(window, "document.querySelector('#wq').dispatchEvent(new Event('input'))")
        assert "Zephyr" in shown("#wordlist") and "Claude" not in shown("#wordlist")
        assert "zef fur" in shown("#fixlist") and "cube" not in shown("#fixlist")  # matches on either side of a fix
        type_into(window, "#wq", "CUBE")
        js(window, "document.querySelector('#wq').dispatchEvent(new Event('input'))")
        assert "No words match." in shown("#wordlist") and "cube" in shown("#fixlist") and "zef fur" not in shown("#fixlist")
        type_into(window, "#wq", "nothing like this")
        js(window, "document.querySelector('#wq').dispatchEvent(new Event('input'))")
        assert "No words match." in shown("#wordlist") and "No fixes match." in shown("#fixlist")
        # a word added while a search is showing is not hidden by it
        type_into(window, "#newword", "Quokka")
        js(window, "document.querySelector('#addword').click()")
        until(lambda: "Quokka" in shown("#wordlist"))
        assert js(window, "document.querySelector('#wq').value") == "" and "cube" in shown("#fixlist")
        # no fixes at all and no words at all: the plain empty messages, not "match"
        fixes_file.write_text("[]", encoding="utf-8")
        words_file = window.t.tmp / "words.txt"
        words_before = words_file.read_text(encoding="utf-8")
        words_file.write_text("# none\n", encoding="utf-8")
        try:
            window.show("words")
            until(lambda: "No fixes learned yet." in shown("#fixlist"))
            assert "No words yet." in shown("#wordlist")
            type_into(window, "#wq", "abc")
            js(window, "document.querySelector('#wq').dispatchEvent(new Event('input'))")
            assert "No words yet." in shown("#wordlist") and "No fixes learned yet." in shown("#fixlist")
        finally:
            words_file.write_text(words_before.replace("Quokka\n", ""), encoding="utf-8")
    finally:
        if before is None:
            fixes_file.unlink(missing_ok=True)
        else:
            fixes_file.write_text(before, encoding="utf-8")
        js(window, "document.querySelector('#wq').value = ''")


def test_the_quit_button_asks_the_tool_to_quit(window):
    go(window, "settings")
    js(window, "document.querySelector('#quit').click()")
    until(lambda: window.t.host.called("quit"))


def test_closing_the_window_ends_its_process_and_it_opens_again(window):
    first_pid = window.child_pid
    assert first_pid and window.child_hwnd
    window.close()
    until(lambda: not window.is_open(), 10)

    def gone():
        out = subprocess.run(["tasklist", "/FI", f"PID eq {first_pid}", "/NH"], capture_output=True, text=True).stdout
        return str(first_pid) not in out
    until(gone, 10)  # the real window process (not just its launcher) is gone
    window.show("settings")
    until(lambda: window.ready and window.child_pid != first_pid, 40)
    until(lambda: js(window, "document.querySelector('#page-settings').classList.contains('on')"))
    assert js(window, "location.hash") == "#settings"


# ====================================================================================================== part 2: the tool itself

CODE = ["*.py", "ui", "config.json", "words.txt", "corrections.json", "style.md", "voicetool.ico"]


@pytest.fixture
def copy_of_the_tool(tmp_path):
    """The staging code in a temporary folder, so a test run writes nothing next to the real files."""
    dest = tmp_path / "tool"
    dest.mkdir()
    for pattern in CODE:
        for src in HERE.glob(pattern):
            if src.name.startswith("test_"):
                continue
            shutil.copytree(src, dest / src.name) if src.is_dir() else shutil.copy2(src, dest / src.name)
    return dest


class Tool:
    """voicetool.py running in test mode from a copy."""

    def __init__(self, folder, window_python=None, delay=0, visible=False):
        self.folder = folder
        self.instance = "e2e" + uuid.uuid4().hex[:8]
        self.env = dict(os.environ, VOICETOOL_TEST="1", VOICETOOL_INSTANCE=self.instance,
                        VOICETOOL_TEST_WINDOW="1" if visible else "hidden", VOICETOOL_TASK="VoiceTool-test-none",
                        VOICETOOL_TEST_START_DELAY=str(delay), PYTHONIOENCODING="utf-8")
        if window_python:
            self.env["VOICETOOL_TEST_WINDOW_PYTHON"] = window_python
        self.proc = None

    def start(self, *args):
        self.proc = subprocess.Popen([PYTHON, str(self.folder / "voicetool.py"), *args], cwd=self.folder, env=self.env,
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=0x08000000)
        return self.proc

    def second_launch(self, *args):
        return subprocess.run([PYTHON, str(self.folder / "voicetool.py"), *args], cwd=self.folder, env=self.env,
                              capture_output=True, timeout=60, creationflags=0x08000000)

    @property
    def log(self):
        try:
            return (self.folder / "voicetool.log").read_text(encoding="utf-8")
        except FileNotFoundError:
            return ""

    def processes(self, script):
        """PIDs of the programs started from THIS copy that are running `script` (inside a virtual environment each start
        is two processes: python.exe's small launcher and the real interpreter). Matched on the copy's full temporary
        path, so a VoiceTool installed anywhere else can never match."""
        assert str(self.folder).lower().startswith(str(Path(tempfile.gettempdir()).resolve()).lower()), "not a temporary copy"
        wanted = str(self.folder / script).replace("'", "''")
        out = subprocess.run(["powershell", "-NoProfile", "-Command",
                              "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.ToLower().Contains('"
                              + wanted.lower() + "') } | Select-Object -ExpandProperty ProcessId"],
                             capture_output=True, text=True, creationflags=0x08000000).stdout
        return [int(x) for x in out.split()]

    def window_processes(self):
        return self.processes("app_window_win.py")

    def tool_processes(self):
        return self.processes("voicetool.py")

    def stop(self):
        for pid in self.window_processes() + self.tool_processes():
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True, creationflags=0x08000000)
        if self.proc is not None and self.proc.poll() is None:
            self.proc.kill()


@pytest.fixture
def tool_process(copy_of_the_tool):
    made = []

    def make(**kw):
        t = Tool(copy_of_the_tool, **kw)
        made.append(t)
        return t
    yield make
    for t in made:
        t.stop()


def test_start_menu_launch_opens_the_window_then_a_second_launch_just_brings_it_forward(tool_process):
    tool = tool_process()
    tool.start("--settings")  # what the Start-menu entry runs when VoiceTool isn't running yet
    until(lambda: "app window ready" in tool.log, 60)
    assert "starting (pid" in tool.log and "already running" not in tool.log
    assert len(tool.window_processes()) >= 1
    time.sleep(4)  # long enough for the Home page to ask the real tool (WinHost + ui_api) for its status
    assert "request" not in tool.log or "failed" not in tool.log, tool.log  # every page request was answered
    started = tool.log.count("app window starting")
    second = tool.second_launch("--settings")  # the Start menu again: asks the running tool and exits
    assert second.returncode == 0
    until(lambda: "app window: showing home" in tool.log, 20)
    assert tool.log.count("app window starting") == started  # no second window
    assert tool.proc.poll() is None


def test_a_click_while_the_tool_is_still_starting_is_remembered(tool_process):
    tool = tool_process(delay=4)
    tool.start()  # no window asked for yet...
    time.sleep(1.5)
    second = tool.second_launch("--settings")  # ...then the Start menu is clicked while it's still loading
    assert second.returncode == 0
    until(lambda: "app window ready" in tool.log, 60)


def test_a_second_plain_launch_exits_without_a_second_tool(tool_process):
    tool = tool_process()
    tool.start()
    time.sleep(3)
    again = tool.second_launch()
    assert again.returncode == 0 and "already running" in tool.log and tool.proc.poll() is None


def test_the_test_copy_uses_its_own_names_so_it_cannot_collide_with_the_real_tool(tool_process):
    import win32api
    import win32event
    import winerror
    tool = tool_process()
    tool.start()
    time.sleep(3)
    live = win32event.CreateMutex(None, False, "VoiceTool-single-instance")
    already = win32api.GetLastError() == winerror.ERROR_ALREADY_EXISTS
    try:
        assert "already running" not in tool.log  # it started even if the real VoiceTool is running right now
        assert tool.proc.poll() is None
        mine = win32event.CreateMutex(None, False, "VoiceTool-single-instance-" + tool.instance)
        assert win32api.GetLastError() == winerror.ERROR_ALREADY_EXISTS  # its own name is held by the test copy
        mine.Close()
    finally:
        live.Close()
    assert already in (True, False)  # (whether the real tool is running is not this test's business)


def test_when_the_tool_goes_away_the_window_closes_with_it(tool_process):
    tool = tool_process()
    tool.start("--settings")
    until(lambda: "app window ready" in tool.log, 60)
    assert tool.window_processes()
    for pid in tool.tool_processes():  # end only the tool (launcher and interpreter), not the window it started
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True, creationflags=0x08000000)
    until(lambda: not tool.tool_processes(), 10)
    until(lambda: not tool.window_processes(), 20)  # the window saw its pipe close and went too


def test_the_old_settings_window_opens_if_the_new_one_cannot_start(tool_process):
    """The window process runs on a Python that has no pywebview: it reports that and VoiceTool falls back."""
    base = Path(sys.base_prefix) / "python.exe"
    assert base.exists()
    check = subprocess.run([str(base), "-c", "import webview"], capture_output=True, creationflags=0x08000000)
    if check.returncode == 0:
        pytest.skip("the base Python has pywebview, so it can't stand in for a broken one")
    tool = tool_process(window_python=str(base))
    tool.start("--settings")
    until(lambda: "app window failed to start" in tool.log and "opening the simple settings window" in tool.log, 60)
    assert "pywebview is not available" in tool.log
    import win32gui

    def old_window_is_up():
        titles = []
        win32gui.EnumWindows(lambda h, _: titles.append((win32gui.GetClassName(h), win32gui.GetWindowText(h)))
                             if win32gui.IsWindowVisible(h) else None, None)
        return ("TkTopLevel", "VoiceTool") in titles
    until(old_window_is_up, 10)  # the old tkinter settings window is up
    assert tool.proc.poll() is None


def test_a_missing_python_for_the_window_also_falls_back(tool_process):
    tool = tool_process(window_python=r"C:\does\not\exist\python.exe")
    tool.start("--settings")
    until(lambda: "could not open the app window" in tool.log, 60)
    assert tool.proc.poll() is None


# ====================================================================================================== part 3: the real run loop

RUN_LOOP = r'''
import sys, threading, time
sys.path.insert(0, sys.argv[1])
import numpy as np
import voicetool as vt
import meetings

vt.CONFIG["show_light"] = False
vt.CONFIG["save_recordings"] = False
vt.beep = lambda *a: None
vt.win32api.keybd_event = lambda *a: None          # nothing is pressed on this PC
pasted = []
vt.paste = lambda text: pasted.append(text)         # nothing is pasted
import mic_light
mic_light.geometry = lambda work, dpi: (mic_light.scaled(96, dpi), -32000, -32000)  # the light is drawn far off-screen
vt.VoiceTool._start_tray = lambda self: print("tray started", flush=True)

class Listener:                                      # the Windows keyboard hook, replaced
    installed, error = True, 0
    def __init__(self, on_key=None): pass
    def start(self): print("keyboard hook started", flush=True)
    def wait(self, timeout=5): return True
    def is_alive(self): return True
    def stop(self): print("keyboard hook stopped", flush=True)
    def suppress_event(self): pass
    def arm(self): pass
    def disarm(self): pass
vt.KeyboardHook = Listener
vt.MouseHook = Listener                              # (the mouse hook for Ctrl+click is replaced too)
HELD = set()                                         # what Windows itself thinks is held (for the keyboard watchdog)
vt.win32api.GetAsyncKeyState = lambda vk: 0x8000 if vk in HELD else 0

class Watch:                                         # the call watcher, replaced
    def __init__(self, *a, **k): print("call watcher started", flush=True)
meetings.MeetingWatch = Watch

class Recorder:                                      # the microphone, replaced
    device = None
    level = 0.0
    def start(self): self.on = True
    def stop(self):
        was, self.on = getattr(self, "on", False), False
        return np.full(vt.RATE * 2, 0.3, np.float32) if was else np.zeros(0, np.float32)

class Cleaner:                                       # the model, replaced
    local = None
    def warm_up(self): pass
    def sync_offline(self): print("offline model start requested", flush=True)   # (the real one would load the model)
    def __call__(self, text, hint=""): return "Hello there, this is a test."
    def _client(self): raise RuntimeError("no model in this test")

tool = vt.VoiceTool(recorder=Recorder(), transcriber=lambda audio: "hello there this is a test", cleaner=Cleaner())

def keys():
    time.sleep(1.0)
    class D: flags = 0
    d = D(); d.vkCode = vt.keyboard.Key.alt_l.value.vk
    tool.listener = Listener()
    HELD.add(d.vkCode)
    tool._filter(vt.win32con.WM_KEYDOWN, d)          # hold Left Alt...
    time.sleep(0.8)
    HELD.discard(d.vkCode)
    tool._filter(vt.win32con.WM_KEYUP, d)            # ...and let go
    time.sleep(2.0)
    for vk, down in ((0xA2, True), (0x43, True), (0x43, False), (0xA2, False)):   # Ctrl+C: never a command
        e = D(); e.vkCode = vk
        tool._filter(vt.win32con.WM_KEYDOWN if down else vt.win32con.WM_KEYUP, e)
    time.sleep(0.6)
    print("pasted", pasted, flush=True)
    tool.ui.put(tool._quit)

threading.Thread(target=keys, daemon=True).start()
tool.run()
print("exited cleanly", flush=True)
'''


def test_the_real_run_loop_takes_a_hold_and_pastes_with_a_fake_keyboard_and_microphone(copy_of_the_tool):
    """The real start-up, key loop, worker and quit, with the keyboard hook, microphone, Azure, clipboard, tray and call
    watcher replaced, so it can run beside a running VoiceTool. A hold of Left Alt ends in a paste of the tidy-up."""
    env = dict(os.environ, PYTHONIOENCODING="utf-8", VOICETOOL_TASK="VoiceTool-test-none")
    done = subprocess.run([PYTHON, "-c", RUN_LOOP, str(copy_of_the_tool)], cwd=copy_of_the_tool, env=env,
                          capture_output=True, text=True, timeout=120, creationflags=0x08000000)
    out = done.stdout
    assert done.returncode == 0, done.stderr[-2000:]
    for line in ("keyboard hook started", "offline model start requested", "tray started", "call watcher started",
                 "keyboard hook stopped", "exited cleanly"):
        assert line in out, out + done.stderr[-1000:]
    assert "pasted ['Hello there, this is a test. ']" in out, out
    history = (copy_of_the_tool / "history.jsonl").read_text(encoding="utf-8")
    assert "Hello there, this is a test." in history
    assert '"command"' not in history and "pasted ['Hello there, this is a test. ']" in out  # Ctrl+C did nothing
