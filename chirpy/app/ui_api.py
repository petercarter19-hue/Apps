

import json
import re
import time
from collections import Counter
from datetime import date, datetime, timedelta
from pathlib import Path

import corrections
import light_files
import light_keys

HERE = Path(__file__).resolve().parent
HISTORY = HERE / "history.jsonl"
WORDS = HERE / "words.txt"

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

def read_history():

    rows = []
    try:
        for line in HISTORY.read_text(encoding="utf-8").splitlines():
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    except FileNotFoundError:
        pass
    return rows

def _fixes_in(row):

    n = row.get("fixes", 0)
    return n if isinstance(n, int) and not isinstance(n, bool) and n > 0 else 0

def describe(row):

    if "transcript" in row:
        kind = "transcript"
        text = f"{row.get('transcript') or 'Transcript'} ({row.get('minutes') or '?'} min). Notes: {Path(row.get('notes') or '').name}"
        said = row.get("source") or ""
    elif "meeting" in row:
        kind = "meeting"
        text = f"{row.get('meeting') or 'Meeting'} ({row.get('minutes') or '?'} min). Notes: {Path(row.get('notes') or '').name}"
        said = ""
    elif "command" in row:
        kind = "claude" if row.get("route") == "claude" else "command"
        text = row.get("result") or ""
        said = row.get("command") or ""
    else:
        kind = "dictate"
        text = row.get("text") or ""
        said = row.get("raw") or ""
    return {"at": row.get("at", ""), "kind": kind, "route": row.get("route", ""), "app": row.get("app") or "",
            "said": said, "text": text, "phone": row.get("source") == "phone", "fixes": _fixes_in(row)}

class API:
    def __init__(self, host, config, save_config):

        self.host, self.config, self.save_config = host, config, save_config

    def call(self, name, args=None):
        method = getattr(self, "api_" + str(name), None)
        if method is None:
            raise ValueError(f"unknown request {name!r}")
        return method(**(args or {}))

    def api_overview(self):
        rows = [describe(r) for r in read_history() if r.get("text")]
        today = date.today().isoformat()
        todays = [r for r in rows if r["at"].startswith(today) and r["kind"] == "dictate"]
        return {
            "enabled": self.config.get("enabled", True),
            "computer": self.host.computer_name(),
            "keys": light_keys.phrases(self.config),
            "theme": self._theme(),
            "notice": getattr(self.host, "settings_notice", lambda: "")(),
            "today": {"dictations": len(todays), "words": sum(len(r["text"].split()) for r in todays),
                      "fixes": sum(r["fixes"] for r in todays)},
            "recent": list(reversed(rows[-6:])),
            "fixes": len(corrections.load()),
            "words": len(self.api_words()["words"]),
        }

    def api_send_feedback(self):

        getattr(self.host, "send_feedback", lambda: None)()
        return {"opened": True}

    def api_dismiss_notice(self):

        getattr(self.host, "notice_seen", lambda: None)()
        return {}

    def _change(self, changes):

        r = self.save_config(self.config, changes)
        return {"saved": bool(r["saved"]), "toast": r["toast"]}

    def api_set_paused(self, paused):
        r = self._change({"enabled": not bool(paused)})
        self.host.paused(bool(paused))
        return dict(r, enabled=self.config.get("enabled", True))

    def api_history(self, query="", kind="all", limit=200):
        words = [w for w in re.split(r"\s+", (query or "").lower()) if w]
        out = []
        for row in reversed(read_history()):
            r = describe(row)
            if not (r["text"] or r["said"]):
                continue
            if kind != "all" and r["kind"] != kind:
                continue
            hay = f"{r['text']} {r['said']} {r['app']}".lower()
            if all(w in hay for w in words):
                out.append(r)
                if len(out) >= limit:
                    break
        return {"items": out, "total": len(out)}

    def api_settings(self):
        features = [{"section": s, "key": k, "title": t, "badge": b, "text": d, "on": bool(self.config.get(k, True))}
                    for s, k, t, b, d in light_keys.features(FEATURES, self.config)]
        return {"features": features, "startup": self.host.startup_enabled(), "enabled": self.config.get("enabled", True),
                "keys": light_keys.phrases(self.config), "theme": self._theme()}

    def _theme(self):

        chosen = self.config.get("theme")
        if chosen in ("light", "dark"):
            return chosen
        return getattr(self.host, "theme", lambda: None)()

    def api_set_theme(self, theme):

        if theme not in ("light", "dark"):
            raise ValueError(f"unknown look {theme!r}")
        r = self._change({"theme": theme})
        if hasattr(self.host, "theme_changed"):
            self.host.theme_changed(theme)
        return dict(r, theme=theme)

    def api_set_keys(self, dictate=None, teach=None):

        changes = {name: value for name, value in (("dictate_key", dictate), ("teach_key", teach)) if value is not None}
        reason = "" if changes else light_keys.problem(None, None)
        for value in changes.values():
            reason = reason or light_keys.problem(value, value)
        r = {"saved": False, "toast": ""}
        if not reason:
            r = self._change(changes)
            if hasattr(self.host, "keys_changed"):
                self.host.keys_changed()
        return dict(r, reason=reason, keys=light_keys.phrases(self.config))

    def api_set_setting(self, key, on):
        if key not in {f[1] for f in FEATURES}:
            raise ValueError(f"unknown setting {key!r}")
        r = self._change({key: bool(on)})
        if hasattr(self.host, "setting_changed"):
            self.host.setting_changed(key, bool(on))
        return dict(r, key=key, on=bool(self.config.get(key, True)))

    def api_set_startup(self, on):
        self.host.set_startup(bool(on))
        return {"startup": self.host.startup_enabled()}

    def api_words(self):
        return {"words": light_files.read_words(WORDS)}

    def api_fixes(self):
        rows = light_files.read_fixes(corrections.FILE)
        return {"fixes": [{"heard": str(d.get("heard", "")), "meant": str(d.get("meant", "")), "added": str(d.get("added", "")),
                           "source": str(d.get("source", ""))} for d in reversed(rows)]}

    def _word_reply(self, out, reason=""):

        reason = reason or out.get("reason", "")
        saved = bool(out["saved"]) and not reason
        return {"words": self.api_words()["words"], "saved": saved, "kept": out.get("kept"), "reason": reason or ("" if saved else light_files.said(out)),
                "toast": reason or light_files.said(out)}

    def api_add_word(self, word):
        word = " ".join(str(word).split())
        if not word:
            return self._word_reply({"saved": False}, "Type a word first.")
        out = light_files.add_word(WORDS, word)
        return self._word_reply(out, "Already in your list" if out.get("duplicate") else "")

    def api_remove_word(self, word):
        return self._word_reply(light_files.remove_word(WORDS, str(word).strip()))

    def api_edit_word(self, old, new):
        new = " ".join(str(new).split())
        if not new:
            return self._word_reply({"saved": False}, "Type a word, or use \u2715 to remove it.")
        return self._word_reply(light_files.edit_word(WORDS, str(old).strip(), new))

    def api_add_fix(self, heard, meant):
        r = corrections.add_saved(heard, meant, "app window")
        return {"added": r["saved"], "failed": r["failed"], "kept": r["kept"], "reason": r["reason"], "toast": light_files.said(r),
                **self.api_fixes()}

    def api_edit_fix(self, old_heard, heard, meant):
        r = corrections.edit_saved(old_heard, heard, meant, "edited")
        return {"saved": r["saved"], "failed": r["failed"], "kept": r["kept"], "reason": r["reason"], "toast": light_files.said(r),
                **self.api_fixes()}

    def api_delete_fix(self, heard):
        r = corrections.delete_saved(heard)
        return {"saved": r["saved"], "kept": r["kept"], "toast": light_files.said(r), **self.api_fixes()}

    def api_clear_history(self):

        return light_files.clear_history(HISTORY)

    def api_open(self, what):
        paths = {"log": HERE / "voicetool.log", "folder": HERE, "words": WORDS, "fixes": corrections.FILE}
        if what not in paths:
            raise ValueError(f"can't open {what!r}")
        if not paths[what].exists():
            return {"opened": False}
        self.host.open_path(paths[what])
        return {"opened": True}

    def api_copy(self, text):
        self.host.copy_text(str(text))
        return {"copied": True}

    def api_quit(self):
        self.host.quit()
        return {}
