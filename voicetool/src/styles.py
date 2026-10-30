"""Named writing styles and per-app rules. Shared by the Mac and the PC.

styles.json holds:
- "styles": named styles, e.g. {"name": "Formal email", "instructions": "..."}. Say "make this LinkedIn post style"
  on the Command key, or tie a style to an app.
- "app_rules": what happens in each app, e.g. {"apps": "Microsoft Outlook, OUTLOOK.EXE, olk.exe",
  "mode": "style", "style": "Formal email"}. Modes: "style" (use that style), "raw" (no tidy-up: terminals and code
  editors, where your words must land exactly as spoken), "normal".
  "apps" lists names to match, separated by commas, so one rule can name the Mac app and the Windows .exe.
  On Windows the app is its program name (OUTLOOK.EXE, ms-teams.exe, chrome.exe...). Names are matched ignoring case
  and a trailing ".exe", so "Slack" and "slack.exe" are the same. An entry written "title:Gmail" matches any window
  whose title contains that text instead (the only way to tell Gmail from the rest of a browser).

Dictation keeps your own words: a style only steers punctuation, capitalization and layout there. "Write" and
"rewrite" commands and Claude's drafts follow the style fully.
"""
import json
import threading
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILE = HERE / "styles.json"
_lock = threading.Lock()
_cache = {"mtime": None, "data": None}

DEFAULTS = {
    "styles": [
        {"name": "Formal email", "instructions": "Complete, correct sentences. A greeting and a sign-off when it's a "
         "whole message. No contractions, slang or emoji. Polite and clear, with short paragraphs."},
        {"name": "Casual chat", "instructions": "Short and friendly. Contractions are fine. No greeting or sign-off. "
         "Light punctuation; a single exclamation mark at most."},
        {"name": "LinkedIn post", "instructions": "Professional and warm. A strong first line, short paragraphs, "
         "one idea each. No hashtag spam (two at most, at the end). No emoji walls."},
        {"name": "Bullet points", "instructions": "Turn it into short, parallel bullet points. Keep every fact; "
         "drop filler."},
    ],
    "app_rules": [
        {"apps": "Terminal, iTerm2, Warp, Ghostty, WindowsTerminal.exe, wt.exe, cmd.exe, powershell.exe, pwsh.exe, "
                 "conhost.exe, OpenConsole.exe, wezterm-gui.exe, alacritty.exe, mintty.exe, putty.exe",
         "mode": "raw", "note": "Terminals: exactly what you said, no tidy-up."},
        {"apps": "Code, Visual Studio Code, Cursor, Zed, Code.exe, Cursor.exe", "mode": "raw",
         "note": "Code editors: no tidy-up."},
        {"apps": "Microsoft Outlook, Mail, OUTLOOK.EXE, olk.exe, HxOutlook.exe", "mode": "style", "style": "Formal email"},
        {"apps": "Microsoft Teams, Slack, Messages, WhatsApp, ms-teams.exe, MSTeams.exe, Teams.exe, slack.exe, "
                 "WhatsApp.exe", "mode": "style", "style": "Casual chat"},
    ],
}


def load():
    """The current styles and rules (defaults until styles.json exists)."""
    with _lock:
        try:
            mtime = FILE.stat().st_mtime
        except FileNotFoundError:
            return json.loads(json.dumps(DEFAULTS))
        if mtime != _cache["mtime"]:
            try:
                data = json.loads(FILE.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                data = DEFAULTS
            data.setdefault("styles", [])
            data.setdefault("app_rules", [])
            _cache.update(mtime=mtime, data=data)
        return json.loads(json.dumps(_cache["data"]))


def save(data):
    clean = {"styles": [], "app_rules": []}
    seen = set()
    for s in data.get("styles", []):
        name = " ".join(str(s.get("name", "")).split())
        if name and name.lower() not in seen:
            seen.add(name.lower())
            clean["styles"].append({"name": name, "instructions": str(s.get("instructions", "")).strip()})
    for r in data.get("app_rules", []):
        apps = ", ".join(a.strip() for a in str(r.get("apps", "")).split(",") if a.strip())
        mode = r.get("mode") if r.get("mode") in ("style", "raw", "normal") else "normal"
        if not apps:
            continue
        rule = {"apps": apps, "mode": mode}
        if mode == "style":
            rule["style"] = str(r.get("style", ""))
        if r.get("note"):
            rule["note"] = str(r["note"])
        clean["app_rules"].append(rule)
    with _lock:
        tmp = FILE.with_suffix(".tmp")
        tmp.write_text(json.dumps(clean, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        tmp.replace(FILE)
        _cache["mtime"] = None
    return clean


def _norm(name):
    """'OUTLOOK.EXE' and 'outlook' are the same app: lower case, no trailing .exe."""
    name = str(name).strip().lower()
    return name[:-4] if name.endswith(".exe") else name


def rule_for(*names, title=""):
    """The first rule whose app list contains any of the given names (app name, bundle id or .exe), or None.
    `title` is the window title: it only matches rule entries written "title:...", checked in the same order."""
    wanted = {_norm(n) for n in names if n and str(n).strip()}
    title = str(title or "").lower()
    if not wanted and not title:
        return None
    for r in load()["app_rules"]:
        for entry in r["apps"].split(","):
            entry = entry.strip()
            if entry.lower().startswith("title:"):
                needle = entry[6:].strip().lower()
                if needle and needle in title:
                    return r
            elif entry and _norm(entry) in wanted:
                return r
    return None


def instructions(name):
    for s in load()["styles"]:
        if s["name"].lower() == str(name).lower():
            return s["instructions"]
    return ""


def is_raw(rule):
    return bool(rule) and rule.get("mode") == "raw"


def dictation_hint(rule, app=""):
    """Extra words for the tidy-up prompt. Dictation keeps the speaker's words, so only the style's NAME is passed
    (its instructions may ask for word changes, like "no contractions", which dictation must never make)."""
    if not rule or rule.get("mode") != "style" or not rule.get("style"):
        return ""
    return (f"This text is going into {app or 'an app'}, where the user writes in a \"{rule['style']}\" tone. Let that "
            "guide only punctuation, capitalization and paragraph breaks. Keep every word exactly as it was said, "
            "including contractions; never add, remove or reword anything, and never add a greeting or sign-off "
            "that wasn't said.")


def writing_hint(rule, app=""):
    """For write / rewrite commands and Claude drafts: the app's style, plus the list of named styles the user can ask for."""
    parts = []
    if rule and rule.get("mode") == "style" and instructions(rule.get("style", "")):
        parts.append(f"The user is in {app or 'an app'}, where they write in their \"{rule['style']}\" style: "
                     f"{instructions(rule['style'])} Use it unless they ask for something else.")
    named = load()["styles"]
    if named:
        parts.append("Their named styles (use one when they name it, e.g. \"make this LinkedIn post style\"): "
                     + " | ".join(f"{s['name']}: {s['instructions']}" for s in named))
    return "\n".join(parts)
