

import difflib

from spelling import americanize
import light_files
import json
import re
import threading
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
FILE = HERE / "corrections.json"
PENDING = HERE / "corrections_pending.json"
AUTO_LEARN_AFTER = 2
MAX_FIX_WORDS = 5

SPOKEN_SYMBOLS = {"at", "dot", "period", "point", "comma", "colon", "semicolon", "underscore", "dash", "hyphen", "slash",
                  "backslash", "hash", "hashtag", "plus", "equals"}

_lock = threading.Lock()

def _heard(text):

    return " ".join(str(text).split()).strip(".,!?;:\"“”()").strip()
_cache = {"mtime": None, "pairs": []}

def _read(path, default):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError):
        return default

def load():

    try:
        mtime = FILE.stat().st_mtime
    except FileNotFoundError:
        return []
    if mtime != _cache["mtime"]:
        pairs = [(d.get("heard") or d.get("wrote") or d.get("from"), d.get("meant") or d.get("to"))
                 for d in light_files.read_fixes(FILE)]
        pairs = [(h, m) for h, m in pairs if isinstance(h, str) and isinstance(m, str)]
        pairs = [(_heard(h), m.strip()) for h, m in pairs if h and m]
        pairs = [(h, m) for h, m in pairs if h and h.lower() != m.lower()]
        _cache.update(mtime=mtime, pairs=sorted(pairs, key=lambda p: -len(p[0])))
    return _cache["pairs"]

def apply_counted(text):

    text = americanize(text)
    n = 0
    for heard, meant in load():

        text, k = re.subn(r"(?<!\w)" + r"\s+".join(map(re.escape, heard.split())) + r"(?!\w)", lambda m, meant=meant: meant,
                          text, flags=re.IGNORECASE)
        n += k
    return text, n

def apply(text):
    return apply_counted(text)[0]

def describe():

    return "; ".join(f"{h} -> {m}" for h, m in load()[:60])

def problem(heard, meant):

    heard, meant = _heard(heard), " ".join(meant.split())
    if not heard or not meant:
        return "Fill in both sides."
    if heard.lower() == meant.lower():
        return "The two sides must be different."
    if len(heard.split()) > MAX_FIX_WORDS:
        return f'"It wrote" can be {MAX_FIX_WORDS} words at most.'
    return ""

def _same(row, heard):
    return str(row.get("heard") or "").lower() == str(heard).lower()

def _fixes(change):

    out = light_files.change_fixes(FILE, change)
    _cache["mtime"] = None
    return {"saved": out["saved"], "failed": not out["saved"], "kept": out["kept"], "error": out["error"],
            "reason": "" if out["saved"] else light_files.NOT_SAVED}

def add_saved(heard, meant, source):

    heard, meant = _heard(heard), " ".join(meant.split())
    why = problem(heard, meant)
    if why:
        return {"saved": False, "failed": False, "kept": None, "error": None, "reason": why}

    def change(rows):
        data = [d for d in rows if not _same(d, heard)]
        data.append({"heard": heard, "meant": meant, "added": time.strftime("%Y-%m-%d"), "source": source})
        return data
    return _fixes(change)

def add(heard, meant, source):
    return add_saved(heard, meant, source)["saved"]

def edit_saved(old_heard, heard, meant, source):

    heard, meant = _heard(heard), " ".join(meant.split())
    why = problem(heard, meant)
    if why:
        return {"saved": False, "failed": False, "kept": None, "error": None, "reason": why}
    old = " ".join(str(old_heard).split())

    def change(rows):
        at = next((i for i, d in enumerate(rows) if _same(d, old)), len(rows))
        keep = [d for i, d in enumerate(rows) if i != at and not _same(d, heard)]
        at -= sum(1 for i, d in enumerate(rows) if i < at and _same(d, heard))
        keep.insert(at, {"heard": heard, "meant": meant, "added": time.strftime("%Y-%m-%d"), "source": source})
        return keep
    return _fixes(change)

def edit(old_heard, heard, meant, source):
    return edit_saved(old_heard, heard, meant, source)["saved"]

def delete_saved(heard):
    return _fixes(lambda rows: [d for d in rows if not _same(d, heard)])

def _words(text):
    return re.findall(r"[\w'’-]+", text)

def learn_from(raw, cleaned, known):

    known_lower = {k.lower(): k for k in known}
    a, b = _words(raw), _words(cleaned)
    learned = []
    for op, i1, i2, j1, j2 in difflib.SequenceMatcher(a=[w.lower() for w in a], b=[w.lower() for w in b]).get_opcodes():
        if op != "replace" or i2 - i1 > 4 or j2 - j1 > 3:
            continue
        if any(w.lower() in SPOKEN_SYMBOLS for w in a[i1:i2]):
            continue
        heard, meant = " ".join(a[i1:i2]), " ".join(b[j1:j2])
        target = known_lower.get(meant.lower())
        if not target:
            continue
        if any(h.lower() == heard.lower() and m.lower() == target.lower() for h, m in load()):
            continue
        with _lock:
            pending = _read(PENDING, {})
            key = f"{heard.lower()}\t{target}"
            pending[key] = pending.get(key, 0) + 1
            ready = pending[key] >= AUTO_LEARN_AFTER
            if ready:
                pending.pop(key)
            PENDING.write_text(json.dumps(pending, indent=2, ensure_ascii=False), encoding="utf-8")
        if ready and add(heard, target, "auto"):
            learned.append((heard, target))
    return learned

def remember_word(word):

    out = light_files.add_word(HERE / "words.txt", word)
    return {"saved": out["saved"], "kept": out["kept"], "error": out["error"]}
