

import json
import os
import re
import stat
import time
import uuid
from pathlib import Path

import light_keys

SAVED = "Saved"
NOT_SAVED = "Couldn't save this change. Your words and fixes were left as they were."
CLEARED = "History cleared"
ALREADY_EMPTY = "History is already empty"
NOT_CLEARED = "Couldn't clear History. Nothing was removed."
PAIRS_KEPT = "History cleared, but Chirpy couldn't remove the word counts it learned from it. They only stay on this PC."
WORDS_HEADER = "# One word or name per line. Chirpy spells these exactly like this.\n"

CLEAR_TRIES = 6
_TEMP = re.compile(r"(?:words\.txt|corrections\.json|usual_words\.json)\.\d+-[0-9a-f]{8}\.tmp")

def kept_note(name):
    return f"The earlier file couldn't be read, so Chirpy kept it as {name} in its folder."

def said(out):

    if not out["saved"]:
        return NOT_SAVED
    return SAVED + (". " + kept_note(out["kept"]) if out.get("kept") else "")

def _kept_name(path):

    stamp = time.strftime("%Y%m%d-%H%M%S")
    aside = path.with_name(f"{path.stem}.damaged-{stamp}{path.suffix}")
    for n in range(2, 100):
        if not aside.exists():
            break
        aside = path.with_name(f"{path.stem}.damaged-{stamp}-{n}{path.suffix}")
    return aside

def _rewrite(path, make):

    path = Path(path)
    out = {"saved": False, "kept": None, "error": None}
    with light_keys._LOCK:
        try:
            try:
                raw = light_keys._read_bytes(path)
            except FileNotFoundError:
                raw = None
            new, damaged = make(raw)
        except Exception as e:
            out["error"] = str(e)
            return out
        if new is None:
            out["saved"] = True
            return out
        tmp = path.with_name(f"{path.name}.{os.getpid()}-{uuid.uuid4().hex[:8]}.tmp")
        try:
            if damaged and raw is not None:
                kept = _kept_name(path)
                light_keys._new_file(kept, raw)
                out["kept"] = kept.name
            light_keys._new_file(tmp, new)
            light_keys._replace_atomic(tmp, path)
            out["saved"] = True
        except Exception as e:
            out["error"] = str(e)
            try:
                tmp.unlink()
            except OSError:
                pass
    return out

def cleanup(folder):

    removed = []
    try:
        entries = list(os.scandir(folder))
    except OSError:
        return removed
    for entry in entries:
        if not _TEMP.fullmatch(entry.name):
            continue
        try:
            info = os.lstat(entry.path)
            if not stat.S_ISREG(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
                continue
            os.unlink(entry.path)
            removed.append(entry.name)
        except OSError:
            pass
    return removed

def _words_in(text):
    return [w for line in text.splitlines() if (w := line.strip()) and not w.startswith("#")]

def read_words(path):

    try:
        raw = light_keys._read_bytes(Path(path))
    except OSError:
        return []
    try:
        return _words_in(raw.decode("utf-8-sig"))
    except UnicodeDecodeError:
        return []

def _decode(raw):

    if raw is None:
        return "", False
    try:
        return raw.decode("utf-8-sig"), False
    except UnicodeDecodeError:
        return "", True

def _lines(text):
    return text.splitlines()

def _same_word(line, word):
    return line.strip() and not line.strip().startswith("#") and line.strip().lower() == word.lower()

def _text(lines):
    return ("\n".join(lines) + "\n").encode("utf-8")

def remove_word(path, word):

    def make(raw):
        text, damaged = _decode(raw)
        lines = _lines(text)
        keep = [line for line in lines if not _same_word(line, word)]
        return (None if len(keep) == len(lines) else _text(keep)), damaged
    return _rewrite(path, make)

def edit_word(path, old, new):

    reason = {"text": ""}

    def make(raw):
        text, damaged = _decode(raw)
        lines = _lines(text)
        if any(_same_word(line, new) and not _same_word(line, old) for line in lines):
            reason["text"] = "That word is already on your list."
            return None, False
        at = next((i for i, line in enumerate(lines) if _same_word(line, old)), None)
        if at is None:
            lines.append(new)
        else:
            lines[at] = new
        return _text(lines), damaged
    out = _rewrite(path, make)
    out["reason"] = reason["text"]
    if reason["text"]:
        out["saved"] = False
    return out

def add_word(path, word):

    found = {"duplicate": False}

    def make(raw):
        text, damaged = _decode(raw)
        if word.lower() in {w.lower() for w in _words_in(text)}:
            found["duplicate"] = True
            return None, False
        lead = "" if not text or text.endswith("\n") else "\n"
        return (text + lead + word + "\n").encode("utf-8"), damaged
    out = _rewrite(path, make)
    out["duplicate"] = found["duplicate"]
    return out

def _fixes_in(raw):

    if raw is None:
        return [], False
    try:
        data = json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, ValueError):
        return [], True
    if isinstance(data, dict):
        good = {h: m for h, m in data.items() if isinstance(m, str)}
        return [{"heard": h, "meant": m} for h, m in good.items()], len(good) != len(data)
    if not isinstance(data, list):
        return [], True
    rows = [d for d in data if isinstance(d, dict)]
    return rows, len(rows) != len(data)

def read_fixes(path):

    try:
        return _fixes_in(light_keys._read_bytes(Path(path)))[0]
    except OSError:
        return []

def change_fixes(path, change):

    def make(raw):
        rows, damaged = _fixes_in(raw)
        return json.dumps(change(rows), indent=2, ensure_ascii=False).encode("utf-8"), damaged
    return _rewrite(path, make)

def append_history(path, line):

    with light_keys._LOCK:
        with open(path, "a", encoding="utf-8") as f:
            f.write(line)

def _delete(path):

    for attempt in range(CLEAR_TRIES):
        try:
            os.unlink(path)
            return True
        except FileNotFoundError:
            return False
        except OSError:
            if attempt == CLEAR_TRIES - 1:
                return None
            time.sleep(light_keys.READ_WAIT_SECONDS)

def _leftovers(path):

    path = Path(path)
    mine = re.compile(re.escape(path.name) + r"\.\d+-[0-9a-f]{8}\.tmp")
    try:
        entries = list(os.scandir(path.parent))
    except OSError:
        return []
    found = []
    for entry in entries:
        try:
            info = os.lstat(entry.path)
            if mine.fullmatch(entry.name) and stat.S_ISREG(info.st_mode) and not getattr(info, "st_file_attributes", 0) & 0x400:
                found.append(Path(entry.path))
        except OSError:
            pass
    return found

def clear_history(path, also=()):

    with light_keys._LOCK:
        gone = _delete(Path(path))
        if gone is None:
            return {"cleared": False, "toast": NOT_CLEARED}
        results = [_delete(Path(p)) for p in also]
        results += [_delete(left) for p in also for left in _leftovers(p)]
    if any(r is None for r in results):
        return {"cleared": True, "toast": PAIRS_KEPT}
    return {"cleared": True, "toast": CLEARED if gone or any(results) else ALREADY_EMPTY}
